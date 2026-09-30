import os
import subprocess
import sys
import time

_lhm_loaded = False
_lhm_error = ""          # DLL / CLR 加载失败
_computer_error = ""     # Computer.Open 失败
_scan_error = ""         # 传感器扫描失败
_assembly = None
_computer = None
_computer_next_try = 0.0

_RETRY_AFTER_SECONDS = 60.0
_GPU_TYPES = ("GpuNvidia", "GpuAmd", "GpuIntel")
_PAWNIO_TIMEOUT_SECONDS = 600


def _base_dir():
    if getattr(sys, "frozen", False):
        return sys._MEIPASS
    return os.path.dirname(os.path.abspath(__file__))


def _reset():
    """清空加载状态，让下一次 _init() 重新尝试。"""
    global _lhm_loaded, _lhm_error, _assembly, _computer
    global _computer_error, _scan_error, _computer_next_try
    _lhm_loaded = False
    _lhm_error = ""
    _computer_error = ""
    _scan_error = ""
    _assembly = None
    _computer = None
    _computer_next_try = 0.0


def _init():
    """加载 LibreHardwareMonitor DLL（net472 版本 + .NET Framework CLR）。"""
    global _lhm_loaded, _lhm_error, _assembly
    if _lhm_loaded or _lhm_error:
        return

    ext_dir = os.path.join(_base_dir(), "external")
    dll_path = os.path.join(ext_dir, "LibreHardwareMonitorLib.dll")
    if not os.path.exists(dll_path):
        _lhm_error = f"DLL 未找到: {dll_path}"
        return

    try:
        import clr  # noqa: F401
    except ImportError as e:
        _lhm_error = f"pythonnet(clr) 未安装: {e}"
        return

    try:
        import System
    except Exception as e:
        _lhm_error = f"System 命名空间不可用: {e}"
        return

    # ── AssemblyResolve: 提供随包附带的依赖 DLL ──
    def _resolve_handler(sender, args):
        req_name = System.Reflection.AssemblyName(args.Name)
        key = req_name.Name
        for asm in System.AppDomain.CurrentDomain.GetAssemblies():
            if asm.GetName().Name == key:
                return asm
        dep_path = os.path.join(ext_dir, key + ".dll")
        if os.path.exists(dep_path):
            return System.Reflection.Assembly.LoadFile(dep_path)
        return None

    System.AppDomain.CurrentDomain.AssemblyResolve += (
        System.ResolveEventHandler(_resolve_handler)
    )

    for dep in [
        "System.Numerics.Vectors.dll",
        "System.Runtime.CompilerServices.Unsafe.dll",
        "System.Memory.dll",
    ]:
        dep_path = os.path.join(ext_dir, dep)
        if os.path.exists(dep_path):
            try:
                System.Reflection.Assembly.LoadFile(dep_path)
            except Exception:
                pass

    try:
        clr.AddReference(dll_path)
    except Exception as e:
        _lhm_error = f"clr.AddReference 失败: {type(e).__name__}: {e}"
        return

    try:
        for asm in System.AppDomain.CurrentDomain.GetAssemblies():
            if "LibreHardwareMonitor" in str(asm.GetName().Name):
                if asm.GetType("LibreHardwareMonitor.Hardware.Computer") is not None:
                    _assembly = asm
                    _lhm_loaded = True
                    return
        _lhm_error = "找不到 Computer 类型"
    except Exception as e:
        _lhm_error = f"DLL 加载失败: {type(e).__name__}: {e}"


def _ensure_computer():
    global _computer, _computer_error, _computer_next_try
    if _computer is not None:
        return _computer

    _init()
    if not _lhm_loaded or _assembly is None:
        return None

    now = time.monotonic()
    if now < _computer_next_try:
        return None

    import System

    try:
        _Computer = _assembly.GetType(
            "LibreHardwareMonitor.Hardware.Computer")
        computer = System.Activator.CreateInstance(_Computer)
        for prop in _Computer.GetProperties():
            if prop.Name.startswith("Is") and "Enabled" in prop.Name:
                try:
                    prop.SetValue(computer, True)
                except Exception:
                    pass
        computer.Open()
        _computer = computer
        _computer_error = ""
        return computer
    except Exception as e:
        # 只影响本次调用，下次隔一段时间再试，避免每轮都重试 Open
        _computer_error = f"Computer.Open 失败: {type(e).__name__}: {e}"
        _computer_next_try = time.monotonic() + _RETRY_AFTER_SECONDS
        return None


def _collect_all():
    """递归扫描所有硬件传感器。每次全量扫描都很重，调用方应复用结果。"""
    global _scan_error
    computer = _ensure_computer()
    sensors = []
    if computer is None:
        return sensors

    def add(hw_name, hw_type, sub, sensor):
        try:
            value = sensor.Value
            if value is None:
                return
            sensors.append({
                "hw": hw_name, "hw_type": hw_type, "sub": sub,
                "name": sensor.Name,
                "type": sensor.SensorType.ToString(),
                "value": float(value),
            })
        except Exception:
            # 单个传感器取值异常不该让整次扫描白跑
            pass

    try:
        for hw in computer.Hardware:
            hw.Update()
            hw_type = hw.HardwareType.ToString()
            hw_name = hw.Name
            for s in hw.Sensors:
                add(hw_name, hw_type, "", s)
            for sub in hw.SubHardware:
                sub.Update()
                for s in sub.Sensors:
                    add(hw_name, hw_type, sub.Name, s)
                for sub2 in sub.SubHardware:
                    sub2.Update()
                    nested = f"{sub.Name}/{sub2.Name}"
                    for s in sub2.Sensors:
                        add(hw_name, hw_type, nested, s)
        _scan_error = ""
    except Exception as e:
        _scan_error = f"传感器扫描异常: {e}"
    return sensors


def scan_sensors():
    """公开接口：一次扫描，结果传给 get_cpu_info / get_gpu_info / check_pawnio_needed。"""
    _init()
    if not _lhm_loaded:
        return []
    return _collect_all()


def _match(sensors, sensor_type, keywords=None, exclude_hw_types=None):
    results = []
    for s in sensors:
        if s["type"] != sensor_type:
            continue
        if exclude_hw_types and s["hw_type"] in exclude_hw_types:
            continue
        nl = (s["name"] + " " + s["hw"] + " " + s["sub"]).lower()
        priority = 99
        if keywords:
            for i, kw in enumerate(keywords):
                if kw in nl:
                    priority = i
                    break
            if priority == 99:
                continue
        results.append((priority, s["value"]))
    if not results:
        return None
    results.sort(key=lambda x: x[0])
    return results[0][1]


def get_cpu_info(sensors=None):
    if sensors is None:
        sensors = scan_sensors()

    info = {"cpu_percent": None, "cpu_temp": None,
            "cpu_fan_rpm": None, "cpu_power": None}

    val = _match(sensors, "Load", ["cpu total", "total"])
    if val is not None:
        info["cpu_percent"] = round(val, 1)

    val = _match(sensors, "Temperature", [
        "core average", "core max", "package", "tctl", "tdie", "core #", "cpu",
    ], exclude_hw_types=_GPU_TYPES)
    if val is not None:
        info["cpu_temp"] = round(val, 1)

    val = _match(sensors, "Fan", ["cpu", "processor"],
                 exclude_hw_types=_GPU_TYPES)
    if val is None:
        val = _match(sensors, "Fan", exclude_hw_types=_GPU_TYPES)
    if val is not None:
        info["cpu_fan_rpm"] = round(val)

    val = _match(sensors, "Power", ["package", "cpu package", "cpu"],
                 exclude_hw_types=_GPU_TYPES)
    if val is not None:
        info["cpu_power"] = round(val, 1)

    return info


def get_gpu_info(sensors=None):
    if sensors is None:
        sensors = scan_sensors()

    info = {
        "gpu_name": None, "gpu_percent": None, "gpu_temp": None,
        "gpu_fan_speed": None, "gpu_power": None,
        "gpu_mem_used": None, "gpu_mem_total": None,
    }
    excluded = ["Cpu", "Motherboard"]

    gpu_sensors = [s for s in sensors if s["hw_type"] in _GPU_TYPES]
    if gpu_sensors:
        info["gpu_name"] = gpu_sensors[0]["hw"]

    val = _match(sensors, "Load", ["gpu core", "d3d 3d", "gpu", "d3d"],
                 exclude_hw_types=excluded)
    if val is not None:
        info["gpu_percent"] = round(val, 1)

    val = _match(sensors, "Temperature", [
        "gpu core", "gpu hotspot", "gpu temperature", "hotspot",
        "junction", "edge", "gpu",
    ], exclude_hw_types=excluded)
    if val is not None:
        info["gpu_temp"] = round(val, 1)

    # 必须先按 gpu 关键字匹配，否则主板/机箱风扇会被当成显卡风扇
    val = _match(sensors, "Fan", ["gpu"], exclude_hw_types=excluded)
    if val is None:
        val = _match(sensors, "Fan", exclude_hw_types=excluded)
    if val is not None:
        info["gpu_fan_speed"] = round(val)

    val = _match(sensors, "Power", ["gpu package", "board", "package", "gpu"],
                 exclude_hw_types=excluded)
    if val is not None:
        info["gpu_power"] = round(val, 1)

    val = _match(sensors, "SmallData", [
        "dedicated memory used", "gpu memory used", "memory used",
    ], exclude_hw_types=excluded)
    if val is not None:
        info["gpu_mem_used"] = round(val)

    val = _match(sensors, "SmallData", [
        "dedicated memory total", "gpu memory total", "memory total",
    ], exclude_hw_types=excluded)
    if val is not None:
        info["gpu_mem_total"] = round(val)

    return info


def get_sensors_by_category():
    result = {}
    for s in scan_sensors():
        key = f"[{s['hw_type']}] {s['hw']}"
        if s['sub']:
            key += f" / {s['sub']}"
        result.setdefault(key, []).append(
            f"  {s['name']} = {s['value']:.1f} ({s['type']})")
    return result


def get_error():
    _init()
    return _lhm_error or _computer_error or _scan_error


def is_available():
    _init()
    return _lhm_loaded


def check_pawnio_needed(sensors=None):
    """LHM 已加载但完全没有非 GPU 的温度传感器 → 多半缺 PawnIO 内核驱动。

    任何非 GPU 温度传感器存在就说明驱动工作正常，无需再额外查 CPU 信息。
    """
    if not is_available():
        return False
    if sensors is None:
        sensors = scan_sensors()
    for s in sensors:
        if s["type"] == "Temperature" and s["hw_type"] not in _GPU_TYPES:
            return False
    return True


def launch_pawnio_setup():
    """运行 PawnIO 安装向导，并在结束后确认传感器是否真的可用了。

    返回 {"success": bool, "needs_restart": bool, "message": str}
    """
    exe_path = os.path.join(_base_dir(), "external", "PawnIO_setup.exe")
    if not os.path.exists(exe_path):
        return {"success": False, "needs_restart": False,
                "message": f"未找到安装程序: {exe_path}"}

    try:
        # 交互式 GUI 向导：不接管道，避免它写满缓冲区后卡住
        proc = subprocess.Popen([exe_path])
    except Exception as e:
        return {"success": False, "needs_restart": False,
                "message": f"启动 PawnIO_setup 失败: {e}"}

    try:
        returncode = proc.wait(timeout=_PAWNIO_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired:
        # 用户还在走向导，不能杀掉它
        return {"success": False, "needs_restart": True,
                "message": "安装向导仍未结束（等待超过 "
                           f"{_PAWNIO_TIMEOUT_SECONDS // 60} 分钟）。\n"
                           "请完成安装后重新启动本程序。"}

    _reset()
    if not is_available():
        detail = get_error()
        msg = f"安装程序已退出（代码: {returncode}），但 LibreHardwareMonitor 未能加载"
        if detail:
            msg += f"\n{detail}"
        return {"success": False, "needs_restart": False, "message": msg}

    if check_pawnio_needed():
        return {"success": False, "needs_restart": True,
                "message": "驱动已安装，但传感器仍未就绪。\n"
                           "请重新启动本程序后再查看。"}

    return {"success": True, "needs_restart": False,
            "message": "PawnIO 驱动安装成功，CPU 传感器数据已可用"}
