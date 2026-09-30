import tkinter as tk
from tkinter import font as tkfont

from app_log import get_logger, log_path, open_log
from config_ui import ConfigDialog, DiagDialog

log = get_logger(__name__)

BG = "#1e1e2e"
CARD_BG = "#2a2a3e"
TEXT = "#cdd6f4"
SUBTEXT = "#a6adc8"
ACCENT = "#89b4fa"
GREEN = "#a6e3a1"
YELLOW = "#f9e2af"
RED = "#f38ba8"
BLUE = "#74c7ec"
PINK = "#f5c2e7"

REFRESH_INTERVAL_MS = 1000


def _fmt_temp(val):
    return f"{val}°C" if val is not None else "N/A"


def _fmt_rpm(val):
    return f"{val} RPM" if val is not None else "N/A"


def _pct_color(pct):
    if pct is None:
        return SUBTEXT
    if pct < 60:
        return GREEN
    if pct < 85:
        return YELLOW
    return RED


def _fmt_pct(val):
    return f"{val}%" if val is not None else "N/A"


class MonitorPanel:
    def __init__(self, root, data_func, config=None, save_callback=None,
                 on_exit=None, can_hide=None):
        self.root = root
        self.data_func = data_func
        self.config = config or {}
        self.save_callback = save_callback
        self.on_exit = on_exit
        self.can_hide = can_hide or (lambda: True)
        self.root.title("e-hub 系统监控面板")
        self.root.configure(bg=BG)
        self.root.resizable(True, True)
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)

        default_font = tkfont.nametofont("TkDefaultFont")
        default_font.configure(family="Segoe UI", size=10)
        self.root.option_add("*Font", default_font)

        self._closed = False
        self._timer_id = None
        self._labels = {}
        self._disk_rows = {}
        self._build_menubar()
        self._build_ui()
        self._build_statusbar()
        self.root.update_idletasks()
        w = max(self.root.winfo_reqwidth() + 32, 660)
        h = max(self.root.winfo_reqheight() + 40, 500)
        self.root.geometry(f"{w}x{h}")
        self._refresh()

    # ── 刷新调度 ────────────────────────────────────────────
    def _schedule_refresh(self):
        """始终只有一条定时链，重复调用是空操作。"""
        if self._closed or self._timer_id is not None:
            return
        try:
            self._timer_id = self.root.after(REFRESH_INTERVAL_MS, self._refresh)
        except Exception:
            self._timer_id = None

    def _cancel_refresh(self):
        if self._timer_id is None:
            return
        try:
            self.root.after_cancel(self._timer_id)
        except Exception:
            pass
        self._timer_id = None

    def reopen(self):
        self._closed = False
        self._cancel_refresh()
        self._refresh()

    def stop_refresh(self):
        self._closed = True
        self._cancel_refresh()

    def _refresh(self):
        self._timer_id = None
        if self._closed:
            return
        try:
            self._update_ui()
        except Exception as e:
            # 格式化出错不能让定时链断掉，否则面板会永久停在旧数据上
            log.exception("panel refresh failed")
            try:
                self._statusbar.config(text=f"面板刷新出错: {e}")
            except Exception:
                pass
        finally:
            self._schedule_refresh()

    # ── 菜单 ────────────────────────────────────────────────
    def _build_menubar(self):
        menubar = tk.Menu(self.root, bg=CARD_BG, fg=TEXT, relief="flat")
        self.root.config(menu=menubar)

        file_menu = tk.Menu(menubar, tearoff=0, bg=CARD_BG, fg=TEXT)
        file_menu.add_command(label="隐藏到托盘", command=self._on_close)
        file_menu.add_separator()
        file_menu.add_command(label="退出", command=self._request_exit)
        menubar.add_cascade(label="文件", menu=file_menu)

        tool_menu = tk.Menu(menubar, tearoff=0, bg=CARD_BG, fg=TEXT)
        tool_menu.add_command(label="设置", command=self._open_settings)
        tool_menu.add_separator()
        tool_menu.add_command(label="诊断信息", command=self._open_diag)
        tool_menu.add_command(label="查看日志", command=self._open_log)
        menubar.add_cascade(label="工具", menu=tool_menu)

        help_menu = tk.Menu(menubar, tearoff=0, bg=CARD_BG, fg=TEXT)
        help_menu.add_command(label="关于", command=self._show_about)
        menubar.add_cascade(label="帮助", menu=help_menu)

    def _build_statusbar(self):
        self._statusbar = tk.Label(
            self.root, text="就绪", bg=CARD_BG, fg=SUBTEXT,
            anchor="w", padx=8, pady=3, font=("Segoe UI", 9),
        )
        self._statusbar.pack(side="bottom", fill="x")

    def _open_settings(self):
        ConfigDialog(self.root, self.config,
                     self.save_callback or (lambda c: None))

    def _open_diag(self):
        DiagDialog(self.root, self.data_func)

    def _open_log(self):
        from tkinter import messagebox
        if not open_log():
            messagebox.showinfo(
                "日志", f"日志文件尚未生成。\n\n位置：{log_path()}",
                parent=self.root,
            )

    def _show_about(self):
        from tkinter import messagebox
        messagebox.showinfo(
            "关于 e-hub",
            "e-hub 系统监控面板\n\n"
            "实时监控 CPU/GPU/内存/磁盘/网络\n"
            "支持 LibreHardwareMonitor 精确传感器\n"
            "数据通过串口发送至硬件 HUB",
            parent=self.root,
        )

    # ── 布局 ────────────────────────────────────────────────
    def _card(self, parent, title, color=ACCENT):
        frame = tk.Frame(parent, bg=CARD_BG, padx=12, pady=8)
        tk.Label(
            frame, text=title, bg=CARD_BG, fg=color,
            font=("Segoe UI", 11, "bold"), anchor="w",
        ).pack(fill="x")
        tk.Frame(frame, bg=color, height=1).pack(fill="x", pady=(2, 5))
        return frame

    def _row(self, parent, label, key, color=TEXT):
        frame = tk.Frame(parent, bg=CARD_BG)
        frame.pack(fill="x", pady=1)
        tk.Label(
            frame, text=label, bg=CARD_BG, fg=SUBTEXT,
            width=14, anchor="w",
        ).pack(side="left")
        lbl = tk.Label(frame, text="--", bg=CARD_BG, fg=color, anchor="w")
        lbl.pack(side="left", fill="x", expand=True)
        self._labels[key] = lbl
        return lbl

    def _build_ui(self):
        main = tk.Frame(self.root, bg=BG)
        main.pack(fill="both", expand=True, padx=12, pady=8)

        tk.Label(
            main, text="e-hub 系统监控面板", bg=BG, fg=ACCENT,
            font=("Segoe UI", 13, "bold"),
        ).pack(pady=(0, 8))

        row_top = tk.Frame(main, bg=BG)
        row_top.pack(fill="x", pady=3)

        cpu_card = self._card(row_top, "CPU", ACCENT)
        cpu_card.pack(side="left", fill="both", expand=True, padx=(0, 3))
        self._row(cpu_card, "型号", "cpu_model", color=GREEN)
        self._row(cpu_card, "使用率", "cpu_percent")
        self._row(cpu_card, "温度", "cpu_temp", color=YELLOW)
        self._row(cpu_card, "功耗", "cpu_power", color=PINK)
        self._row(cpu_card, "风扇", "cpu_fan_rpm", color=PINK)

        gpu_card = self._card(row_top, "GPU", PINK)
        gpu_card.pack(side="right", fill="both", expand=True, padx=(3, 0))
        self._row(gpu_card, "型号", "gpu_name", color=GREEN)
        self._row(gpu_card, "使用率", "gpu_percent")
        self._row(gpu_card, "温度", "gpu_temp", color=YELLOW)
        self._row(gpu_card, "显存", "gpu_mem", color=BLUE)
        self._row(gpu_card, "功耗", "gpu_power", color=PINK)
        self._row(gpu_card, "风扇", "gpu_fan_speed", color=PINK)

        mem_card = self._card(main, "内存", GREEN)
        mem_card.pack(fill="x", pady=3)
        self._row(mem_card, "使用率", "mem_percent")
        self._row(mem_card, "已用/总量", "mem_detail")

        disk_card = self._card(main, "磁盘", YELLOW)
        disk_card.pack(fill="x", pady=3)
        self._disk_frame = disk_card
        self._disk_empty = tk.Label(
            disk_card, text="未检测到磁盘", bg=CARD_BG, fg=SUBTEXT, anchor="w")

        net_card = self._card(main, "网络", BLUE)
        net_card.pack(fill="x", pady=3)
        self._row(net_card, "下载速度", "net_download", color=GREEN)
        self._row(net_card, "上传速度", "net_upload", color=RED)
        self._row(net_card, "延迟", "net_latency", color=TEXT)

        sys_card = self._card(main, "系统", SUBTEXT)
        sys_card.pack(fill="x", pady=3)
        self._row(sys_card, "音量", "volume", color=GREEN)
        self._row(sys_card, "运行时间", "uptime", color=TEXT)

    # ── 磁盘行（原地更新，避免每秒重建控件） ────────────────
    def _create_disk_row(self, mount):
        frame = tk.Frame(self._disk_frame, bg=CARD_BG)
        frame.pack(fill="x", pady=1)
        tk.Label(frame, text=mount, bg=CARD_BG, fg=SUBTEXT,
                 width=6, anchor="w").pack(side="left")
        used = tk.Label(frame, text="--", bg=CARD_BG, fg=TEXT,
                        anchor="w", width=14)
        used.pack(side="left")
        pct = tk.Label(frame, text="--", bg=CARD_BG, fg=SUBTEXT,
                       anchor="w", width=6)
        pct.pack(side="left")
        read = tk.Label(frame, text="R:--", bg=CARD_BG, fg=GREEN,
                        anchor="w", width=16)
        read.pack(side="left")
        write = tk.Label(frame, text="W:--", bg=CARD_BG, fg=RED,
                         anchor="w", width=16)
        write.pack(side="left")
        return {"frame": frame, "used": used, "pct": pct,
                "read": read, "write": write}

    def _update_disks(self, disks, data):
        present = set()
        for d in disks or []:
            mount = d.get("mount", "?")
            present.add(mount)
            row = self._disk_rows.get(mount)
            if row is None:
                row = self._create_disk_row(mount)
                self._disk_rows[mount] = row

            letter = mount[0] if mount else ""
            pct = d.get("percent", 0)
            row["used"].config(text=f"{d.get('used', 0)}/{d.get('total', 0)} GB")
            row["pct"].config(text=f"{pct}%", fg=_pct_color(pct))
            row["read"].config(
                text=f"R:{data.get(f'disk_io_{letter}_read', 0)} KB/s")
            row["write"].config(
                text=f"W:{data.get(f'disk_io_{letter}_write', 0)} KB/s")

        for mount in list(self._disk_rows):
            if mount not in present:
                self._disk_rows.pop(mount)["frame"].destroy()

        if present:
            self._disk_empty.pack_forget()
        else:
            self._disk_empty.pack(fill="x", pady=1)

    # ── 数据填充 ────────────────────────────────────────────
    def _update_ui(self):
        try:
            data = self.data_func()
        except Exception:
            log.exception("data_func failed")
            data = {}

        self._set("cpu_model", data.get("cpu_model", "N/A"))
        pct = data.get("cpu_percent")
        self._set("cpu_percent", _fmt_pct(pct), _pct_color(pct))
        self._set("cpu_temp", _fmt_temp(data.get("cpu_temp")), YELLOW)
        cpu_power = data.get("cpu_power")
        self._set("cpu_power",
                  f"{cpu_power} W" if cpu_power is not None else "N/A", PINK)
        self._set("cpu_fan_rpm", _fmt_rpm(data.get("cpu_fan_rpm")), PINK)

        self._set("gpu_name", data.get("gpu_name") or "N/A")
        gpu_pct = data.get("gpu_percent")
        self._set("gpu_percent", _fmt_pct(gpu_pct), _pct_color(gpu_pct))
        self._set("gpu_temp", _fmt_temp(data.get("gpu_temp")), YELLOW)
        mem_used = data.get("gpu_mem_used") or 0
        mem_total = data.get("gpu_mem_total") or 0
        if mem_total:
            self._set("gpu_mem",
                      f"{mem_used}/{mem_total} MB "
                      f"({int(mem_used / mem_total * 100)}%)", BLUE)
        else:
            self._set("gpu_mem", "N/A")
        power = data.get("gpu_power")
        self._set("gpu_power", f"{power} W" if power is not None else "N/A", PINK)
        fan_speed = data.get("gpu_fan_speed")
        self._set("gpu_fan_speed",
                  f"{fan_speed}%" if fan_speed is not None else "N/A", PINK)

        mem_percent = data.get("mem_percent")
        self._set("mem_percent", _fmt_pct(mem_percent), _pct_color(mem_percent))
        self._set("mem_detail",
                  f"{data.get('mem_used', 0)}/{data.get('mem_total', 0)} MB")

        self._update_disks(data.get("disks", []), data)

        self._set("net_download",
                  f"{data.get('net_download_kbs', 0)} KB/s", GREEN)
        self._set("net_upload", f"{data.get('net_upload_kbs', 0)} KB/s", RED)
        latency = data.get("net_latency_ms")
        target = data.get("ping_target", "8.8.8.8")
        if latency is not None:
            self._set("net_latency", f"{latency} ms ({target})", TEXT)
        else:
            self._set("net_latency", f"检测中... ({target})", SUBTEXT)

        volume = data.get("volume")
        self._set("volume", f"{volume}%" if volume is not None else "N/A", GREEN)
        self._set("uptime", data.get("uptime") or "N/A", TEXT)

        self._update_statusbar(data)

    def _update_statusbar(self, data):
        parts = []
        # 串口/配置问题最影响使用，优先显示
        for key in ("serial_error", "config_error", "pawnio_message"):
            if data.get(key):
                parts.append(data[key])
        if not parts:
            if data.get("lhm_error"):
                parts.append(f"LHM: {data['lhm_error']}")
            elif (data.get("cpu_temp") is None
                  and data.get("cpu_fan_rpm") is None):
                parts.append("LHM: 未检测到CPU传感器（见「工具 → 诊断信息」）")
        if data.get("volume") is None:
            parts.append("音量: 不可用")
        self._statusbar.config(text=" | ".join(parts) if parts else "就绪")

    def _set(self, key, text, color=TEXT):
        lbl = self._labels.get(key)
        if lbl:
            lbl.config(text=str(text), fg=color)

    def _request_exit(self):
        if self.on_exit is not None:
            self.on_exit()
        else:
            self._on_close()

    def _on_close(self):
        # 托盘图标没显示出来时不能只是隐藏：用户找不到任何入口，只能去任务管理器
        if not self.can_hide():
            self._request_exit()
            return
        self._closed = True
        self._cancel_refresh()
        self.root.withdraw()
