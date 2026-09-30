"""配置的默认值、校验与原子读写。"""
import json
import os
import sys

# 一帧完整 JSON 约 880 字节，9600 波特率下要 0.92 秒，占满 1 秒间隔没有余量；
# 115200 只需 0.077 秒。改这里必须同步改 HUB 固件的波特率。
DEFAULT_CONFIG = {
    "serial_port": "COM3",
    "baud_rate": 115200,
    "ping_target": "8.8.8.8",
    "interval_seconds": 1,
    "auto_start": False,
}

MIN_INTERVAL_SECONDS = 0.5
MIN_BAUD = 300
MAX_BAUD = 4000000


def app_dir():
    """配置文件、日志都放在 exe（或源码）所在目录，不受工作目录影响。"""
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


def default_config_path():
    return os.path.join(app_dir(), "config.json")


def validate(raw):
    """把外部配置收敛到可用范围，避免坏值（0、负数、字符串）打爆采集循环。"""
    cfg = dict(DEFAULT_CONFIG)
    if not isinstance(raw, dict):
        return cfg, "配置文件格式不正确，已使用默认值"

    port = str(raw.get("serial_port") or "").strip()
    if port:
        cfg["serial_port"] = port

    target = str(raw.get("ping_target") or "").strip()
    if target:
        cfg["ping_target"] = target

    try:
        baud = int(raw.get("baud_rate"))
        if MIN_BAUD <= baud <= MAX_BAUD:
            cfg["baud_rate"] = baud
    except (TypeError, ValueError):
        pass

    try:
        interval = float(raw.get("interval_seconds"))
        if interval > 0:
            cfg["interval_seconds"] = max(MIN_INTERVAL_SECONDS, interval)
    except (TypeError, ValueError):
        pass

    cfg["auto_start"] = bool(raw.get("auto_start", False))
    return cfg, ""


def load(path=None):
    """返回 (配置, 错误信息)。配置损坏时回退到默认值而不是崩溃。"""
    path = path or default_config_path()
    try:
        with open(path, "r", encoding="utf-8") as f:
            raw = json.load(f)
    except FileNotFoundError:
        return dict(DEFAULT_CONFIG), ""
    except (OSError, ValueError, UnicodeDecodeError) as e:
        return dict(DEFAULT_CONFIG), f"配置文件读取失败，已使用默认值: {e}"
    return validate(raw)


def save(config, path=None):
    """原子写入，避免写一半崩溃把配置弄坏。返回 (成功, 错误信息)。"""
    path = path or default_config_path()
    tmp = path + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(config, f, indent=4, ensure_ascii=False)
        os.replace(tmp, path)
        return True, ""
    except OSError as e:
        try:
            os.remove(tmp)
        except OSError:
            pass
        return False, f"配置保存失败: {e}"
