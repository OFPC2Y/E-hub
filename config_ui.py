import tkinter as tk
from tkinter import messagebox, ttk

from app_config import (DEFAULT_CONFIG, MAX_BAUD, MIN_BAUD,
                        MIN_INTERVAL_SECONDS, app_dir)
from app_log import log_path, open_log

try:
    import lhm_sensors
except Exception:
    lhm_sensors = None


def _lhm_error_text():
    if lhm_sensors is None:
        return "传感器模块未加载"
    try:
        return lhm_sensors.get_error() or "无"
    except Exception:
        return "未知"

BG = "#1e1e2e"
CARD_BG = "#2a2a3e"
TEXT = "#cdd6f4"
SUBTEXT = "#a6adc8"
ACCENT = "#89b4fa"

# 一整帧 JSON 约 880 字节：9600 要 0.92 秒（几乎占满 1 秒间隔），115200 只要 0.077 秒
BAUD_HINT = "必须与 HUB 固件一致；低于 19200 会发不完整帧"


class ConfigDialog:
    def __init__(self, parent, config, save_callback):
        self.result = None
        self.config = dict(config)
        self.save_callback = save_callback

        self.win = tk.Toplevel(parent)
        self.win.title("设置")
        self.win.configure(bg=BG)
        self.win.resizable(False, False)
        self.win.transient(parent)
        self.win.grab_set()

        self._build()
        self.win.update_idletasks()
        w = self.win.winfo_reqwidth() + 20
        h = self.win.winfo_reqheight() + 20
        self.win.geometry(f"{w}x{h}")

    def _labeled_entry(self, parent, label, key, row, hint=None):
        tk.Label(
            parent, text=label, bg=CARD_BG, fg=SUBTEXT,
            anchor="w", width=16,
        ).grid(row=row, column=0, sticky="w", padx=8, pady=4)
        var = tk.StringVar(value=str(self.config.get(key, "")))
        entry = tk.Entry(
            parent, textvariable=var, bg="#181825", fg=TEXT,
            insertbackground=TEXT, relief="flat", width=30,
        )
        entry.grid(row=row, column=1, sticky="ew", padx=8, pady=4)
        if hint:
            tk.Label(
                parent, text=hint, bg=CARD_BG, fg=SUBTEXT,
                anchor="w", font=("Segoe UI", 8),
            ).grid(row=row, column=2, sticky="w", padx=(0, 8))
        return var

    def _build(self):
        main = tk.Frame(self.win, bg=BG, padx=16, pady=12)
        main.pack(fill="both", expand=True)

        tk.Label(
            main, text="e-hub 设置", bg=BG, fg=ACCENT,
            font=("Segoe UI", 12, "bold"),
        ).pack(pady=(0, 10))

        card = tk.Frame(main, bg=CARD_BG, padx=12, pady=10)
        card.pack(fill="x", pady=4)
        card.columnconfigure(1, weight=1)

        self.ping_var = self._labeled_entry(card, "Ping 目标", "ping_target", 0)
        self.port_var = self._labeled_entry(card, "串口端口", "serial_port", 1)
        self.baud_var = self._labeled_entry(card, "波特率", "baud_rate", 2,
                                           hint=BAUD_HINT)
        self.interval_var = self._labeled_entry(
            card, "采集间隔(秒)", "interval_seconds", 3,
            hint=f"最小 {MIN_INTERVAL_SECONDS}")

        tk.Label(
            card, text="开机自启监控", bg=CARD_BG, fg=SUBTEXT,
            anchor="w", width=16,
        ).grid(row=4, column=0, sticky="w", padx=8, pady=4)
        self.auto_var = tk.BooleanVar(value=self.config.get("auto_start", False))
        tk.Checkbutton(
            card, variable=self.auto_var, bg=CARD_BG, fg=TEXT,
            selectcolor="#181825", activebackground=CARD_BG,
        ).grid(row=4, column=1, sticky="w", padx=8, pady=4)

        tk.Label(
            main, text=f"配置文件：{app_dir()}", bg=BG, fg=SUBTEXT,
            anchor="w", font=("Segoe UI", 8),
        ).pack(fill="x", pady=(6, 0))

        btn_frame = tk.Frame(main, bg=BG)
        btn_frame.pack(fill="x", pady=(10, 0))

        tk.Button(
            btn_frame, text="恢复默认", bg="#45475a", fg=TEXT,
            relief="flat", padx=12, pady=4, command=self._reset,
        ).pack(side="left")
        tk.Button(
            btn_frame, text="取消", bg="#45475a", fg=TEXT,
            relief="flat", padx=12, pady=4, command=self.win.destroy,
        ).pack(side="right", padx=(6, 0))
        tk.Button(
            btn_frame, text="保存", bg=ACCENT, fg="#1e1e2e",
            relief="flat", padx=12, pady=4, command=self._save,
        ).pack(side="right")

    def _save(self):
        try:
            baud_rate = int(self.baud_var.get().strip() or DEFAULT_CONFIG["baud_rate"])
            interval = float(self.interval_var.get().strip()
                             or DEFAULT_CONFIG["interval_seconds"])
        except ValueError:
            messagebox.showerror("错误", "波特率和采集间隔必须是数字",
                                 parent=self.win)
            return

        if not MIN_BAUD <= baud_rate <= MAX_BAUD:
            messagebox.showerror(
                "错误", f"波特率需要在 {MIN_BAUD}–{MAX_BAUD} 之间",
                parent=self.win)
            return

        if interval <= 0:
            messagebox.showerror("错误", "采集间隔必须大于 0", parent=self.win)
            return
        if interval < MIN_INTERVAL_SECONDS:
            interval = MIN_INTERVAL_SECONDS

        self.config["ping_target"] = (
            self.ping_var.get().strip() or DEFAULT_CONFIG["ping_target"])
        self.config["serial_port"] = (
            self.port_var.get().strip() or DEFAULT_CONFIG["serial_port"])
        self.config["baud_rate"] = baud_rate
        self.config["interval_seconds"] = interval
        self.config["auto_start"] = self.auto_var.get()

        self.save_callback(self.config)
        self.win.destroy()

    def _reset(self):
        self.ping_var.set(DEFAULT_CONFIG["ping_target"])
        self.port_var.set(DEFAULT_CONFIG["serial_port"])
        self.baud_var.set(str(DEFAULT_CONFIG["baud_rate"]))
        self.interval_var.set(str(DEFAULT_CONFIG["interval_seconds"]))
        self.auto_var.set(DEFAULT_CONFIG["auto_start"])


class DiagDialog:
    def __init__(self, parent, data_func):
        self.data_func = data_func
        self.win = tk.Toplevel(parent)
        self.win.title("诊断信息")
        self.win.configure(bg=BG)
        self.win.geometry("800x600")
        self.win.transient(parent)

        nb = ttk.Notebook(self.win)
        nb.pack(fill="both", expand=True, padx=8, pady=8)

        if lhm_sensors is not None:
            self._build_tab_lhm(nb)
        self._build_tab_overview(nb)
        self._build_tab_raw(nb)

        self.win.grab_set()

    @staticmethod
    def _text_area(parent, wrap="none"):
        text = tk.Text(
            parent, bg="#181825", fg=TEXT, insertbackground=TEXT,
            relief="flat", font=("Consolas", 10), wrap=wrap,
        )
        vsb = tk.Scrollbar(parent, orient="vertical", command=text.yview)
        text.configure(yscrollcommand=vsb.set)
        text.pack(side="left", fill="both", expand=True)
        vsb.pack(side="right", fill="y")
        if wrap == "none":
            hsb = tk.Scrollbar(parent, orient="horizontal", command=text.xview)
            text.configure(xscrollcommand=hsb.set)
            hsb.pack(side="bottom", fill="x")
        return text

    def _build_tab_lhm(self, nb):
        frame = tk.Frame(nb, bg="#181825", padx=8, pady=8)
        nb.add(frame, text="LHM 原始传感器")

        text = self._text_area(frame)

        lines = []
        try:
            categories = lhm_sensors.get_sensors_by_category()
            if not categories:
                lines.append("LHM 未检测到任何传感器")
                lines.append(f"LHM 错误: {_lhm_error_text()}")
            for key in sorted(categories.keys()):
                lines.append(f"\n{key}")
                lines.extend(categories[key])
        except Exception as e:
            lines.append(f"读取失败: {e}")
            lines.append(f"LHM 状态: {lhm_sensors.is_available()}")
            lines.append(f"LHM 错误: {_lhm_error_text()}")

        text.insert("1.0", "\n".join(lines))
        text.config(state="disabled")

    def _build_tab_overview(self, nb):
        frame = tk.Frame(nb, bg="#181825", padx=8, pady=8)
        nb.add(frame, text="当前数据总览")

        text = self._text_area(frame, wrap="word")
        data = self.data_func()

        lines = []
        lines.append("【CPU】")
        lines.append(f"  型号:   {data.get('cpu_model') or 'N/A'}")
        lines.append(f"  使用率: {_val(data.get('cpu_percent'), '%')}")
        lines.append(f"  温度:   {_val(data.get('cpu_temp'), '°C')}")
        lines.append(f"  功耗:   {_val(data.get('cpu_power'), ' W')}")
        lines.append(f"  风扇:   {_val(data.get('cpu_fan_rpm'), ' RPM')}")
        lines.append("")
        lines.append("【GPU】")
        lines.append(f"  型号:   {data.get('gpu_name') or 'N/A'}")
        lines.append(f"  使用率: {_val(data.get('gpu_percent'), '%')}")
        lines.append(f"  温度:   {_val(data.get('gpu_temp'), '°C')}")
        mem_total = data.get('gpu_mem_total')
        if mem_total:
            lines.append(f"  显存:   {_val(data.get('gpu_mem_used'), '')}"
                         f" / {mem_total} MB")
        else:
            lines.append("  显存:   N/A")
        lines.append(f"  功耗:   {_val(data.get('gpu_power'), ' W')}")
        lines.append(f"  风扇:   {_val(data.get('gpu_fan_speed'), '%')}")
        lines.append("")
        lines.append("【内存】")
        lines.append(f"  使用率: {_val(data.get('mem_percent'), '%')}")
        lines.append(f"  已用/总量: {_val(data.get('mem_used'), '')}"
                     f" / {_val(data.get('mem_total'), '')} MB")
        lines.append("")
        lines.append("【磁盘】")
        disks = data.get('disks', [])
        if disks:
            for d in disks:
                mount = d.get('mount', '')
                letter = mount[0] if mount else ''
                lines.append(
                    f"  {mount}  {_val(d.get('used'), '')}/{_val(d.get('total'), '')} GB"
                    f"  ({_val(d.get('percent'), '%')})"
                    f"  R:{data.get(f'disk_io_{letter}_read', 0)} KB/s"
                    f"  W:{data.get(f'disk_io_{letter}_write', 0)} KB/s")
        else:
            lines.append("  (无)")
        # 键名对不上时磁盘速度会恒为 0，这里把实际收到的键列出来便于核对
        io_keys = sorted(k for k in data if k.startswith('disk_io_'))
        lines.append(f"  收到的 IO 键: {', '.join(io_keys) if io_keys else '(无)'}")
        lines.append("")
        lines.append("【网络】")
        lines.append(f"  下载: {_val(data.get('net_download_kbs'), ' KB/s')}")
        lines.append(f"  上传: {_val(data.get('net_upload_kbs'), ' KB/s')}")
        lines.append(f"  延迟: {_val(data.get('net_latency_ms'), ' ms')}")
        lines.append(f"  目标: {data.get('ping_target', 'N/A')}")
        lines.append("")
        lines.append("【系统】")
        lines.append(f"  音量:   {_val(data.get('volume'), '%')}")
        lines.append(f"  运行时间: {data.get('uptime', 'N/A')}")
        lines.append(f"  时间戳: {data.get('timestamp', 'N/A')}")
        lines.append("")
        lines.append("【状态】")
        lines.append(f"  串口: {data.get('serial_error') or '正常'}")
        lines.append(f"  配置: {data.get('config_error') or '正常'}")
        lines.append(f"  LHM:  {data.get('lhm_error') or '正常'}")
        lines.append(f"  日志: {log_path()}")

        text.insert("1.0", "\n".join(lines))
        text.config(state="disabled")

        tk.Button(
            frame, text="打开日志", bg="#45475a", fg=TEXT, relief="flat",
            padx=10, pady=2, command=open_log,
        ).pack(side="bottom", anchor="e", pady=(6, 0))

    def _build_tab_raw(self, nb):
        frame = tk.Frame(nb, bg="#181825", padx=8, pady=8)
        nb.add(frame, text="Raw JSON")

        text = self._text_area(frame)

        import json
        data = self.data_func()
        text.insert("1.0", json.dumps(data, indent=2, ensure_ascii=False,
                                      default=str))
        text.config(state="disabled")


def _val(v, unit):
    if v is None:
        return "N/A"
    return f"{v}{unit}"
