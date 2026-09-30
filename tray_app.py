import queue
import threading
import time
import tkinter as tk

import pystray
from PIL import Image, ImageDraw

from app_config import (DEFAULT_CONFIG, MIN_INTERVAL_SECONDS,
                        load as load_config, save as save_config)
from app_log import get_logger, log_path, open_log
from gui_panel import MonitorPanel
from monitor import collect_all, collect_ping, get_all_disk_usage, get_cpu_model
from serial_sender import SerialSender

log = get_logger(__name__)

# 只用于界面展示，不随串口发出去
UI_ONLY_KEYS = ("pawnio_message", "serial_error", "config_error")

PING_INTERVAL_SECONDS = 5.0
DISK_REFRESH_SECONDS = 1800
UI_QUEUE_POLL_MS = 50
PAWNIO_CHECK_DELAY_MS = 2000
ICON_RESYNC_MS = 1500
# Windows 托盘提示气泡上限 128 字符，超了 Shell_NotifyIcon 可能整个失败
_TITLE_MAX = 120

# 串口没打开时的重试间隔。设备晚插上、驱动晚加载都是常态，不该要求重启程序
RECONNECT_INTERVAL_SECONDS = 5.0


def _create_icon_image(color=(0, 180, 80)):
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.ellipse([8, 8, 56, 56], fill=color)
    return img


_GREEN = _create_icon_image((0, 180, 80))
_RED = _create_icon_image((200, 50, 50))
_GRAY = _create_icon_image((120, 120, 120))


def _import_lhm():
    try:
        import lhm_sensors
        return lhm_sensors
    except Exception:
        log.exception("lhm_sensors import failed")
        return None


class TrayApp:
    def __init__(self, config_path=None):
        self.config_path = config_path or None
        self._state_lock = threading.Lock()
        self._data_lock = threading.Lock()
        self._data_cache = {}
        self._config_error = ""
        self.config = self._load_config()

        self.sender = SerialSender(
            port=self.config["serial_port"],
            baud_rate=self.config["baud_rate"],
        )

        self._running = False
        self._shutting_down = False
        self._serial_skipped = False
        # 下次允许尝试打开串口的时刻（time.monotonic），0 表示立刻可试
        self._next_connect_at = 0.0
        self._connect_failure_logged = False
        self._stop_event = threading.Event()
        self._thread = None
        self._ping_thread = None
        self._icon = None
        self._icon_color = _GRAY
        self._icon_title = "系统监控 - 就绪"
        self._cached_menu = None
        self._tk_root = None
        self._panel = None
        self._panel_visible = False
        self._ui_queue = queue.Queue()

    # ── 配置 ────────────────────────────────────────────────
    def _load_config(self):
        config, error = load_config(self.config_path)
        self._config_error = error
        if error:
            log.warning("config load: %s", error)
        return config

    def _save_config(self):
        ok, error = save_config(self.config, self.config_path)
        self._config_error = error
        if not ok:
            log.warning("config save: %s", error)
        return ok

    # ── 采集线程 ────────────────────────────────────────────
    def _current_interval(self):
        try:
            interval = float(self.config.get("interval_seconds",
                                             DEFAULT_CONFIG["interval_seconds"]))
        except (TypeError, ValueError):
            return DEFAULT_CONFIG["interval_seconds"]
        return max(MIN_INTERVAL_SECONDS, interval)

    def _data_loop(self, stop_event):
        cached_disks = None
        disk_refresh_counter = 0.0
        while not stop_event.is_set():
            try:
                interval = self._current_interval()
                disk_refresh_counter += interval
                if cached_disks is None or disk_refresh_counter >= DISK_REFRESH_SECONDS:
                    cached_disks = get_all_disk_usage()
                    disk_refresh_counter = 0.0
                data = collect_all(self.config, cached_disks=cached_disks)
                with self._data_lock:
                    self._data_cache.update(data)
                self._connect_serial(stop_event=stop_event)
                self._send_serial(stop_event, interval)
            except Exception as e:
                log.exception("data loop iteration failed")
                self._set_icon(None, f"数据采集出错: {e}")
                interval = self._current_interval()
            stop_event.wait(interval)

    def _ping_loop(self, stop_event):
        while not stop_event.is_set():
            try:
                result = collect_ping(self.config)
                with self._data_lock:
                    self._data_cache.update(result)
            except Exception:
                log.exception("ping loop iteration failed")
            stop_event.wait(PING_INTERVAL_SECONDS)

    def _connect_serial(self, force=False, stop_event=None):
        """确保串口是打开的，没打开就按 RECONNECT_INTERVAL_SECONDS 重试。

        以前只在「开始监控」那一刻试一次，失败后 _serial_open 就永久停在 False：
        采集循环照跑、面板照刷新，看起来一切正常，但线上一个字节都发不出去。
        设备晚插上、驱动晚加载、被别的程序临时占着，都只能重启程序才能恢复 ——
        拔插一次 USB 串口也一样。
        """
        if stop_event is not None and stop_event.is_set():
            return
        if self._shutting_down or not self._running:
            return
        if self.sender.is_open:
            return

        now = time.monotonic()
        if not force and now < self._next_connect_at:
            return
        self._next_connect_at = now + RECONNECT_INTERVAL_SECONDS

        try:
            self.sender.open()
        except Exception as e:
            # 首次失败记 warning（这是排查问题的关键线索），后续重试压到 debug
            if self._connect_failure_logged:
                log.debug("serial connect retry failed: %s", e)
            else:
                self._connect_failure_logged = True
                log.warning("serial open failed: %s", e)
            self._set_cache(
                "serial_error",
                f"串口未连接: {e}"
                f"（每 {int(RECONNECT_INTERVAL_SECONDS)} 秒重试）")
            self._set_icon(_RED, f"串口未连接: {self.sender.port}")
            return

        self._connect_failure_logged = False
        log.info("serial opened: %s @ %s", self.sender.port, self.sender.baud_rate)
        self._set_cache("serial_error", "")
        self._set_icon(_GREEN, f"监控运行中 ({self.sender.port})")

    def _send_serial(self, stop_event, interval):
        """发送合并后的缓存，这样 ping 线程拿到的延迟也会一起发出去。"""
        if stop_event.is_set() or not self.sender.is_open:
            return
        with self._data_lock:
            payload = {k: v for k, v in self._data_cache.items()
                       if k not in UI_ONLY_KEYS}
        if not payload:
            return
        try:
            # 留出 20% 余量，免得刚好卡着间隔
            sent = self.sender.send(payload, budget_seconds=interval * 0.8)
        except Exception as e:
            # 掉线要说出来，不能一直假装在发。断开后交给 _connect_serial 重连
            log.warning("serial send failed: %s", e)
            self.sender.close()
            # 端口是刚刚才坏的，下一次循环立刻重试，不用等满一个节流周期
            self._next_connect_at = 0.0
            self._set_cache("serial_error", f"串口已断开: {e}（正在重连）")
            self._set_icon(_RED, f"串口已断开: {e}")
            return

        if not sent:
            # 波特率带不动一帧完整数据：丢帧好过写半行把对端解析搞乱
            if not self._serial_skipped:
                self._serial_skipped = True
                log.warning("serial frame skipped: %d bytes won't drain in %.2fs "
                            "at %s baud", self.sender.frame_bytes(payload),
                            interval * 0.8, self.sender.baud_rate)
                message = (f"串口 {self.sender.baud_rate} 波特率带不动当前数据量，"
                           "已丢弃部分数据（建议与 HUB 固件一起提高波特率）")
                self._set_cache("serial_error", message)
                self._set_icon(None, message)
        elif self._serial_skipped:
            self._serial_skipped = False
            self._set_cache("serial_error", "")

    def _start_monitor(self, icon=None, item=None):
        with self._state_lock:
            if self._running or self._shutting_down:
                return
            self._running = True
            stop_event = threading.Event()
            self._stop_event = stop_event

        # 先立刻试一次，成功的话第一帧不用等一个重试周期。
        # 失败也没关系，采集循环会一直重试
        self._connect_serial(force=True)

        self._thread = threading.Thread(
            target=self._data_loop, args=(stop_event,),
            daemon=True, name="data-loop")
        self._ping_thread = threading.Thread(
            target=self._ping_loop, args=(stop_event,),
            daemon=True, name="ping-loop")
        self._thread.start()
        self._ping_thread.start()

    def _stop_monitor(self, icon=None, item=None):
        with self._state_lock:
            self._running = False
            stop_event = self._stop_event
            threads = [self._thread, self._ping_thread]

        # 两个循环都用 wait() 等事件，set 后立刻返回，join 很快
        stop_event.set()
        for thread in threads:
            if thread is not None and thread.is_alive():
                thread.join(timeout=8)

        with self._state_lock:
            self._thread = None
            self._ping_thread = None
            self._next_connect_at = 0.0
            self._connect_failure_logged = False
        try:
            self.sender.close()
        except Exception:
            log.warning("serial close failed", exc_info=True)
        self._set_icon(_GRAY, "监控已停止")

    # ── 图标与菜单 ──────────────────────────────────────────
    def _set_icon(self, color=None, title=None):
        if color is not None:
            self._icon_color = color
        if title is not None:
            self._icon_title = title[:_TITLE_MAX]

        icon = self._icon
        if icon is None or self._shutting_down:
            return
        try:
            if color is not None:
                icon.icon = color
            if title is not None:
                icon.title = self._icon_title
        except Exception:
            log.debug("icon update failed", exc_info=True)

    def _reapply_icon(self):
        """补一次图标状态。

        pystray 的 setup 回调可能早于托盘窗口创建完成，那时设置的颜色/标题会被
        丢弃，图标就会一直停在灰色的「就绪」上。
        """
        self._set_icon(self._icon_color, self._icon_title)

    def _set_cache(self, key, value):
        with self._data_lock:
            self._data_cache[key] = value

    def _get_data(self):
        with self._data_lock:
            data = dict(self._data_cache)
        if self._config_error:
            data.setdefault("config_error", self._config_error)
        return data

    def _build_menu(self):
        ports = SerialSender.list_ports()
        current = self.config.get("serial_port")
        if ports:
            port_items = [
                pystray.MenuItem(
                    f"{'✓ ' if p == current else ''}{p}",
                    self._select_port(p),
                )
                for p in ports
            ]
        else:
            port_items = [pystray.MenuItem("(无可用串口)", None, enabled=False)]

        return pystray.Menu(
            pystray.MenuItem("打开面板", self._open_panel),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("开始监控", self._start_monitor),
            pystray.MenuItem("停止监控", self._stop_monitor),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("选择串口", pystray.Menu(*port_items)),
            pystray.MenuItem("刷新串口列表", self._refresh_ports),
            pystray.MenuItem("查看日志", lambda icon, item: open_log()),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("退出", self._quit),
        )

    def _refresh_ports(self, icon=None, item=None):
        self._cached_menu = self._build_menu()
        if self._icon is not None:
            try:
                self._icon.menu = self._cached_menu
            except Exception:
                log.debug("menu refresh failed", exc_info=True)
        # 刷新端口多半就是因为刚把设备插上，顺手把重连节流清零，别让用户再等
        # 一个重试周期
        self._next_connect_at = 0.0
        self._connect_failure_logged = False

    def _select_port(self, port):
        def handler(icon=None, item=None):
            was_running = self._running
            self._stop_monitor()
            self.config["serial_port"] = port
            self.sender.port = port
            self._save_config()
            self._refresh_ports()
            if was_running:
                # 切换端口后必须重新启动，否则监控会静悄悄地停掉
                self._start_monitor()
            else:
                self._set_icon(_GRAY, f"串口已切换: {port}（监控未运行）")
        return handler

    # ── 主线程投递 ──────────────────────────────────────────
    def _on_ui(self, func):
        """把任务排到 Tk 主线程执行。

        pystray 的菜单回调运行在托盘线程上，直接碰 Tk 控件会崩。
        """
        self._ui_queue.put(func)

    def _drain_ui_queue(self):
        while True:
            try:
                func = self._ui_queue.get_nowait()
            except queue.Empty:
                break
            try:
                func()
            except Exception:
                log.exception("ui task failed")
        if self._tk_root is not None:
            self._tk_root.after(UI_QUEUE_POLL_MS, self._drain_ui_queue)

    # ── 面板 ────────────────────────────────────────────────
    def _open_panel(self, icon=None, item=None):
        self._on_ui(self._do_open_panel)

    def _do_open_panel(self):
        if self._panel is None:
            self._panel = MonitorPanel(
                self._tk_root, self._get_data,
                config=self.config, save_callback=self._apply_config,
                on_exit=self._quit, can_hide=self._tray_usable,
            )
        else:
            self._panel.reopen()
        self._tk_root.deiconify()
        self._tk_root.lift()
        self._panel_visible = True

    def _tray_usable(self):
        """托盘图标是否真的显示出来了。

        没显示出来的时候面板不能只「隐藏到托盘」——用户会找不到任何入口，
        只能去任务管理器结束进程。这时关闭面板等同于退出程序。
        """
        if self._shutting_down:
            return False
        icon = self._icon
        if icon is None:
            return False
        try:
            return bool(icon.visible)
        except Exception:
            log.debug("tray visibility check failed", exc_info=True)
            return False

    def _apply_config(self, new_config):
        was_running = self._running
        needs_restart = was_running and (
            new_config.get("serial_port") != self.config.get("serial_port")
            or new_config.get("baud_rate") != self.config.get("baud_rate")
        )

        self.config.update(new_config)
        self._save_config()
        self.sender.port = self.config["serial_port"]
        self.sender.baud_rate = self.config["baud_rate"]
        self._set_cache("serial_error", "")

        if needs_restart:
            # 停/启要 join 采集线程，放到后台做，别卡住设置窗口
            threading.Thread(target=self._restart_monitor, daemon=True,
                             name="monitor-restart").start()
        else:
            self._set_icon(None, "设置已保存")

    def _restart_monitor(self):
        self._stop_monitor()
        self._start_monitor()

    # ── PawnIO ──────────────────────────────────────────────
    def _start_pawnio_check(self):
        threading.Thread(target=self._pawnio_check, daemon=True,
                         name="pawnio-check").start()

    def _pawnio_check(self):
        lhm = _import_lhm()
        if lhm is None:
            log.warning("pawnio check: lhm_sensors 导入失败，跳过")
            return
        if not lhm.is_available():
            # 这里以前是静默 return，CPU 数据缺失时完全查不出原因
            log.warning("pawnio check: LHM 未加载，跳过（%s）",
                        lhm.get_error() or "无错误信息")
            return
        try:
            needed = lhm.check_pawnio_needed()
        except Exception:
            log.exception("pawnio check failed")
            return
        log.info("pawnio check: needed=%s", needed)
        if needed:
            self._on_ui(self._prompt_pawnio)

    def _prompt_pawnio(self):
        from tkinter import messagebox

        log.info("pawnio check: 正在提示用户安装驱动")
        if self._shutting_down:
            log.info("pawnio check: 已开始退出，跳过弹窗")
            return
        result = messagebox.askyesno(
            "安装 PawnIO 内核驱动",
            "LibreHardwareMonitor 已加载，\n"
            "但 CPU 温度/风扇传感器数据缺失。\n\n"
            "原因：ASUS B760M 等主板的 SuperIO 芯片\n"
            "需要 PawnIO 内核驱动才能读取传感器数据。\n\n"
            "是否立即运行 PawnIO 驱动安装程序？\n"
            "（安装路径：external\\PawnIO_setup.exe）",
            parent=self._tk_root,
        )
        if result:
            self._set_cache("pawnio_message", "正在安装 PawnIO 驱动，请稍候...")
            threading.Thread(target=self._run_pawnio_install, daemon=True,
                             name="pawnio-install").start()

    def _run_pawnio_install(self):
        from tkinter import messagebox

        lhm = _import_lhm()
        if lhm is None:
            self._set_cache("pawnio_message", "PawnIO 安装失败: 传感器模块不可用")
            return

        result = lhm.launch_pawnio_setup()
        message = result["message"]

        if result["success"]:
            self._set_icon(None, "PawnIO 驱动已安装 - 系统监控")
            self._on_ui(lambda: messagebox.showinfo(
                "安装成功", message, parent=self._tk_root))
        elif result.get("needs_restart"):
            self._set_icon(_RED, "PawnIO 需重启程序")
            self._on_ui(lambda: messagebox.showwarning(
                "需要重启程序", message, parent=self._tk_root))
        else:
            self._set_cache("pawnio_message", f"PawnIO 安装失败: {message}")
            self._set_icon(_RED, "PawnIO 安装失败 - 系统监控")
            self._on_ui(lambda: messagebox.showerror(
                "安装失败",
                f"{message}\n\n"
                "你也可以手动安装：\n"
                "1. 找到 external\\PawnIO_setup.exe\n"
                "2. 右键 → 以管理员身份运行\n"
                "3. 完成后重启本程序",
                parent=self._tk_root))
            return

        self._set_cache("pawnio_message", message)

    # ── 生命周期 ────────────────────────────────────────────
    def _quit(self, icon=None, item=None):
        with self._state_lock:
            if self._shutting_down:
                return
            self._shutting_down = True
        self._stop_monitor()
        self._on_ui(self._do_quit)

    def _do_quit(self):
        if self._panel is not None:
            self._panel.stop_refresh()
        if self._tk_root is not None:
            self._tk_root.destroy()

    def _run_tray(self):
        try:
            self._cached_menu = self._build_menu()
            self._icon = pystray.Icon(
                name="SerialMonitor",
                icon=_GRAY,
                title="系统监控 - 就绪",
                menu=self._cached_menu,
            )
        except Exception:
            # 托盘起不来也得继续采集，不能连带把监控也停了
            log.exception("tray icon init failed")
            self._start_monitor()
            return

        # setup 在图标就绪后回调，此时改颜色/标题才会生效
        try:
            self._icon.run(setup=self._on_tray_ready)
        except Exception:
            log.exception("tray loop failed")
            self._start_monitor()

    def _on_tray_ready(self, icon):
        # pystray 的坑：传了自定义 setup 之后，它就不再用默认的 setup 帮你把
        # visible 置为 True 了，图标会永远不显示。必须自己设。
        try:
            icon.visible = True
        except Exception:
            log.exception("tray icon 显示失败")
        log.info("tray icon ready: visible=%s", self._tray_usable())
        self._start_monitor()

    def _report_callback_exception(self, exc, val, tb):
        """接管 Tk 回调里的异常。

        console=False 打包后 stderr 是不存在的，after 回调里抛异常会被默认处理器
        静默丢掉 —— 界面停住但日志一片干净，完全查不出原因。
        """
        log.error("tk callback failed", exc_info=(exc, val, tb))

    def run(self):
        self._tk_root = tk.Tk()
        self._tk_root.report_callback_exception = self._report_callback_exception
        self._tk_root.withdraw()
        self._tk_root.after(UI_QUEUE_POLL_MS, self._drain_ui_queue)

        threading.Thread(target=self._run_tray, daemon=True, name="tray").start()

        self._do_open_panel()
        threading.Thread(target=get_cpu_model, daemon=True, name="cpu-model").start()
        self._tk_root.after(ICON_RESYNC_MS, self._reapply_icon)
        self._tk_root.after(PAWNIO_CHECK_DELAY_MS, self._start_pawnio_check)
        log.info("startup complete, pawnio check in %dms", PAWNIO_CHECK_DELAY_MS)

        self._tk_root.mainloop()

        if self._icon is not None:
            try:
                self._icon.stop()
            except Exception:
                log.debug("icon stop failed", exc_info=True)
        log.info("exited, log: %s", log_path())


def main():
    TrayApp().run()


if __name__ == "__main__":
    main()
