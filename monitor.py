import platform
import re
import string
import subprocess
import time
import warnings
from datetime import datetime

import psutil

from app_log import get_logger

warnings.filterwarnings("ignore", category=FutureWarning, message=".*pynvml.*")

log = get_logger(__name__)

try:
    import pynvml
    _has_nvml = True
except ImportError:
    _has_nvml = False

try:
    import lhm_sensors as _lhm
except Exception:
    _lhm = None

try:
    from ping3 import ping as _ping3
    _has_ping3 = True
except ImportError:
    _has_ping3 = False

try:
    from pycaw.pycaw import AudioUtilities, IAudioEndpointVolume
    from comtypes import CLSCTX_ALL
    _has_pycaw = True
except ImportError:
    _has_pycaw = False

_NO_WINDOW = 0x08000000

# sensors_temperatures / sensors_fans 是 Linux 专有接口，Windows 版 psutil
# 根本没有这两个函数，不先判断的话会每秒刷一条 AttributeError 日志
_PSUTIL_TEMPS = hasattr(psutil, "sensors_temperatures")
_PSUTIL_FANS = hasattr(psutil, "sensors_fans")

_prev_net_io = None
_prev_net_time = None
_prev_disk_io = None
_prev_disk_io_time = None
_cpu_model_cache = None

# 物理磁盘序号 → 盘符，例如 {"physicaldrive0": "C"}
# 变量名不能叫 _disk_letter_map：会和下面同名函数互相覆盖，第二次调用就崩
_disk_letter_cache = None
_disk_letter_cache_time = 0.0
_DISK_MAP_TTL = 1800.0

_volume_iface = None
_volume_last = None
_volume_next_try = 0.0
_VOLUME_RETRY_SECONDS = 10.0

_nvml_handle = None
_nvml_next_try = 0.0
_NVML_RETRY_SECONDS = 60.0


def _lhm_available():
    """LHM 状态随时可能变化（例如刚装完 PawnIO），所以每次都问一次。"""
    if _lhm is None:
        return False
    try:
        return bool(_lhm.is_available())
    except Exception:
        return False


def get_cpu_model():
    global _cpu_model_cache
    if _cpu_model_cache is not None:
        return _cpu_model_cache

    if platform.system().lower() == "windows":
        for cmd, timeout in (
            (["powershell", "-NoProfile", "-Command",
              "(Get-CimInstance Win32_Processor).Name"], 10),
            (["wmic", "cpu", "get", "Name"], 5),
        ):
            try:
                output = subprocess.check_output(
                    cmd, stderr=subprocess.DEVNULL, timeout=timeout,
                    creationflags=_NO_WINDOW,
                )
                for line in output.decode("utf-8", errors="replace").splitlines():
                    line = line.strip()
                    if line and line.lower() != "name":
                        _cpu_model_cache = line
                        return line
            except Exception:
                continue

    _cpu_model_cache = platform.processor() or "Unknown CPU"
    return _cpu_model_cache


def get_cpu_info(sensors=None):
    result = {"cpu_percent": None, "cpu_temp": None,
              "cpu_fan_rpm": None, "cpu_power": None}

    if _lhm_available():
        try:
            lhm = _lhm.get_cpu_info(sensors)
        except Exception:
            log.exception("lhm cpu read failed")
            lhm = {}
        # 用 is not None 判断：空闲时 0% / 0W 是有效读数
        for key in result:
            if lhm.get(key) is not None:
                result[key] = lhm[key]

    if result["cpu_percent"] is None:
        try:
            result["cpu_percent"] = psutil.cpu_percent(interval=0)
        except Exception:
            log.exception("psutil cpu_percent failed")

    if result["cpu_temp"] is None and _PSUTIL_TEMPS:
        try:
            temps = psutil.sensors_temperatures()
            if temps:
                for name in ("coretemp", "cpu_thermal", "k10temp", "acpitz"):
                    if name in temps and temps[name]:
                        result["cpu_temp"] = temps[name][0].current
                        break
                if result["cpu_temp"] is None:
                    first = next(iter(temps.values()))
                    if first:
                        result["cpu_temp"] = first[0].current
        except Exception:
            log.exception("psutil sensors_temperatures failed")

    if result["cpu_fan_rpm"] is None and _PSUTIL_FANS:
        try:
            fans = psutil.sensors_fans()
            if fans:
                first = next(iter(fans.values()))
                if first:
                    result["cpu_fan_rpm"] = first[0].current
        except Exception:
            log.exception("psutil sensors_fans failed")

    return result


def _get_nvml_handle():
    """NVML 初始化一次即可，反复 init/shutdown 既慢又容易泄漏。"""
    global _nvml_handle, _nvml_next_try
    if _nvml_handle is not None:
        return _nvml_handle
    if not _has_nvml or time.monotonic() < _nvml_next_try:
        return None
    try:
        pynvml.nvmlInit()
        _nvml_handle = pynvml.nvmlDeviceGetHandleByIndex(0)
    except Exception:
        # 驱动可能是稍后才就绪，隔一段时间再试，不要永久放弃
        log.warning("nvml init failed", exc_info=True)
        _nvml_handle = None
        _nvml_next_try = time.monotonic() + _NVML_RETRY_SECONDS
    return _nvml_handle


def get_gpu_info(sensors=None):
    gpu = {
        "gpu_name": None, "gpu_percent": None, "gpu_temp": None,
        "gpu_fan_speed": None, "gpu_power": None,
        "gpu_mem_used": None, "gpu_mem_total": None,
    }

    if _lhm_available():
        try:
            lhm = _lhm.get_gpu_info(sensors)
        except Exception:
            log.exception("lhm gpu read failed")
            lhm = {}
        if lhm.get("gpu_name"):
            return lhm

    handle = _get_nvml_handle()
    if handle is None:
        # 保持字段齐全，串口对端解析时不会因为缺键而失败
        return gpu

    try:
        name_raw = pynvml.nvmlDeviceGetName(handle)
        if isinstance(name_raw, bytes):
            name_raw = name_raw.decode("utf-8", errors="replace")
        mem = pynvml.nvmlDeviceGetMemoryInfo(handle)
        gpu["gpu_name"] = str(name_raw)
        gpu["gpu_percent"] = pynvml.nvmlDeviceGetUtilizationRates(handle).gpu
        gpu["gpu_temp"] = pynvml.nvmlDeviceGetTemperature(
            handle, pynvml.NVML_TEMPERATURE_GPU)
        gpu["gpu_mem_used"] = mem.used // (1024 * 1024)
        gpu["gpu_mem_total"] = mem.total // (1024 * 1024)
    except Exception:
        log.exception("nvml read failed")
        return gpu

    try:
        gpu["gpu_power"] = round(pynvml.nvmlDeviceGetPowerUsage(handle) / 1000.0, 1)
    except Exception:
        pass
    try:
        gpu["gpu_fan_speed"] = pynvml.nvmlDeviceGetFanSpeed(handle)
    except Exception:
        pass
    return gpu


def get_memory_info():
    try:
        mem = psutil.virtual_memory()
        return {
            "mem_total": mem.total // (1024 * 1024),
            "mem_used": (mem.total - mem.available) // (1024 * 1024),
            "mem_percent": round((mem.total - mem.available) / mem.total * 100, 1),
        }
    except Exception:
        log.exception("memory read failed")
        return {"mem_total": 0, "mem_used": 0, "mem_percent": 0}


def _disk_entry(mount, usage):
    total_gb = usage.total / (1024 ** 3)
    if total_gb <= 0:
        return None
    label = mount[:2] if len(mount) >= 2 and mount[1] == ":" else mount
    return {
        "mount": label,
        "total": round(total_gb, 1),
        "used": round(usage.used / (1024 ** 3), 1),
        "percent": usage.percent,
    }


def get_all_disk_usage():
    """只查已挂载的分区；逐盘符探测（A:–Z:）在断开的网络盘上会长时间阻塞。"""
    disks = []
    seen = set()
    try:
        partitions = psutil.disk_partitions(all=False)
    except Exception:
        log.exception("disk_partitions failed")
        partitions = []

    for part in partitions:
        mount = getattr(part, "mountpoint", "") or ""
        if not mount or mount in seen:
            continue
        seen.add(mount)
        try:
            entry = _disk_entry(mount, psutil.disk_usage(mount))
        except (OSError, PermissionError):
            continue
        if entry:
            disks.append(entry)

    if not disks:
        for letter in string.ascii_uppercase:
            path = f"{letter}:\\"
            try:
                entry = _disk_entry(f"{letter}:", psutil.disk_usage(path))
            except (OSError, PermissionError):
                continue
            if entry:
                disks.append(entry)
    return disks


def _load_disk_letter_map():
    """"PhysicalDrive0" → "C"，用 Get-Partition 查一次。

    psutil 在 Windows 上按物理磁盘计数，键名不是盘符；不映射的话面板上按盘符
    查 read/write 永远查不到。失败就返回空字典，退化为不显示速度。
    """
    mapping = {}
    try:
        # 全部用单引号，避免 subprocess 在 Windows 上转义双引号把命令弄坏
        output = subprocess.check_output(
            ["powershell", "-NoProfile", "-Command",
             "Get-Partition | Where-Object { $_.DriveLetter } | "
             "ForEach-Object { [string]$_.DiskNumber + '=' + $_.DriveLetter }"],
            stderr=subprocess.DEVNULL, timeout=10, creationflags=_NO_WINDOW,
        )
    except Exception:
        log.warning("disk letter map unavailable", exc_info=True)
        return mapping

    for line in output.decode("utf-8", errors="replace").splitlines():
        if "=" not in line:
            continue
        index, _, letter = line.strip().partition("=")
        index = index.strip()
        letter = letter.strip().upper()
        if index.isdigit() and len(letter) == 1 and letter.isalpha():
            mapping.setdefault(f"physicaldrive{index}", letter)
    return mapping


def _disk_letter_map():
    global _disk_letter_cache, _disk_letter_cache_time
    now = time.monotonic()
    if _disk_letter_cache is None or now - _disk_letter_cache_time > _DISK_MAP_TTL:
        _disk_letter_cache = _load_disk_letter_map()
        _disk_letter_cache_time = now
    return _disk_letter_cache


def _disk_letter_for(name, mapping):
    stripped = name.rstrip(":").upper()
    if len(stripped) == 1 and stripped.isalpha():
        return stripped
    return mapping.get(name.lower())


def get_disk_io_speed():
    global _prev_disk_io, _prev_disk_io_time
    now = time.monotonic()
    try:
        counters = psutil.disk_io_counters(perdisk=True)
    except Exception:
        log.exception("disk_io_counters failed")
        return {}
    if not counters:
        return {}

    result = {}
    if _prev_disk_io is not None and _prev_disk_io_time is not None:
        elapsed = now - _prev_disk_io_time
        if elapsed > 0:
            mapping = _disk_letter_map()
            for name, cur in counters.items():
                prev = _prev_disk_io.get(name)
                if prev is None:
                    continue
                letter = _disk_letter_for(name, mapping)
                if letter is None:
                    continue
                read_kb = max(0, cur.read_bytes - prev.read_bytes) / elapsed / 1024
                write_kb = max(0, cur.write_bytes - prev.write_bytes) / elapsed / 1024
                result[f"disk_io_{letter}_read"] = round(read_kb, 1)
                result[f"disk_io_{letter}_write"] = round(write_kb, 1)

    _prev_disk_io = counters
    _prev_disk_io_time = now
    return result


def get_network_speed():
    global _prev_net_io, _prev_net_time
    now = time.monotonic()
    try:
        counters = psutil.net_io_counters()
    except Exception:
        log.exception("net_io_counters failed")
        return {"net_upload_kbs": 0.0, "net_download_kbs": 0.0}

    upload_speed = 0.0
    download_speed = 0.0
    if _prev_net_io is not None and _prev_net_time is not None:
        elapsed = now - _prev_net_time
        if elapsed > 0:
            upload_speed = max(
                0, counters.bytes_sent - _prev_net_io.bytes_sent) / elapsed / 1024
            download_speed = max(
                0, counters.bytes_recv - _prev_net_io.bytes_recv) / elapsed / 1024
    _prev_net_io = counters
    _prev_net_time = now
    return {
        "net_upload_kbs": round(upload_speed, 1),
        "net_download_kbs": round(download_speed, 1),
    }


def get_network_latency(target="8.8.8.8"):
    if _has_ping3:
        try:
            result = _ping3(target, timeout=3, unit="ms")
            if result is not None and not isinstance(result, bool):
                return round(result, 1)
        except Exception:
            log.debug("ping3 failed for %s", target, exc_info=True)
        return None

    param = "-n" if platform.system().lower() == "windows" else "-c"
    try:
        output = subprocess.check_output(
            ["ping", param, "1", target],
            stderr=subprocess.DEVNULL, timeout=5, creationflags=_NO_WINDOW,
        )
        if platform.system().lower() == "windows":
            text = output.decode("cp936", errors="replace")
        else:
            text = output.decode("utf-8", errors="replace")
        match = re.search(r"[<=]([\d.]+)\s*ms", text)
        if match:
            return float(match.group(1))
    except Exception:
        log.debug("ping subprocess failed for %s", target, exc_info=True)
    return None


def get_system_volume():
    """用 pycaw 直接读，缓存接口。

    以前每轮都走一次 PowerShell 兜底，等于每秒起一个进程；而那个兜底语句本身
    在 Windows PowerShell 5.1 下语法无效、WMI 类也不存在，索性去掉。
    """
    global _volume_iface, _volume_last, _volume_next_try
    now = time.monotonic()

    if _volume_iface is None:
        if not _has_pycaw or now < _volume_next_try:
            return _volume_last
        try:
            import comtypes
            try:
                comtypes.CoInitialize()
            except Exception:
                pass
            speakers = AudioUtilities.GetSpeakers()
            # pycaw 2025 起 GetSpeakers() 返回 AudioDevice，端点音量在
            # EndpointVolume 属性上；旧版返回 IMMDevice，得自己 Activate
            endpoint = getattr(speakers, "EndpointVolume", None)
            if endpoint is None:
                endpoint = speakers.Activate(
                    IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
                endpoint = endpoint.QueryInterface(IAudioEndpointVolume)
            _volume_iface = endpoint
        except Exception:
            log.warning("audio endpoint unavailable", exc_info=True)
            _volume_iface = None
            _volume_next_try = now + _VOLUME_RETRY_SECONDS
            return _volume_last

    try:
        _volume_last = round(_volume_iface.GetMasterVolumeLevelScalar() * 100)
    except Exception:
        log.warning("volume read failed", exc_info=True)
        _volume_iface = None
        _volume_next_try = time.monotonic() + _VOLUME_RETRY_SECONDS
    return _volume_last


def get_system_uptime():
    try:
        elapsed = time.time() - psutil.boot_time()
        hours = int(elapsed // 3600)
        minutes = int((elapsed % 3600) // 60)
        if hours >= 24:
            return f"{hours // 24}天{hours % 24}小时{minutes}分"
        return f"{hours}小时{minutes}分"
    except Exception:
        log.exception("uptime read failed")
        return None


def collect_all(config=None, cached_disks=None):
    # LHM 全量扫描很重，一次扫完给 CPU/GPU/驱动检测共用
    sensors = None
    if _lhm_available():
        try:
            sensors = _lhm.scan_sensors()
        except Exception:
            log.exception("lhm scan failed")
            sensors = None

    cpu = get_cpu_info(sensors)
    gpu = get_gpu_info(sensors)
    mem = get_memory_info()
    disks = cached_disks if cached_disks is not None else get_all_disk_usage()
    net_speed = get_network_speed()
    disk_io = get_disk_io_speed()

    lhm_err = ""
    if _lhm_available():
        try:
            lhm_err = _lhm.get_error()
        except Exception:
            pass

    return {
        "cpu_model": get_cpu_model(),
        "cpu_percent": cpu.get("cpu_percent"),
        "cpu_temp": cpu.get("cpu_temp"),
        "cpu_fan_rpm": cpu.get("cpu_fan_rpm"),
        "cpu_power": cpu.get("cpu_power"),
        **gpu,
        "mem_total": mem.get("mem_total", 0),
        "mem_used": mem.get("mem_used", 0),
        "mem_percent": mem.get("mem_percent", 0),
        "disks": disks,
        **disk_io,
        **net_speed,
        "volume": get_system_volume(),
        "uptime": get_system_uptime(),
        "lhm_error": lhm_err,
        "timestamp": datetime.now().isoformat(timespec="seconds"),
    }


def collect_ping(config=None):
    target = "8.8.8.8"
    if config:
        target = config.get("ping_target") or target
    return {"net_latency_ms": get_network_latency(target), "ping_target": target}
