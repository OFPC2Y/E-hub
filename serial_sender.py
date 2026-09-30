import json

import serial
import serial.tools.list_ports

from app_log import get_logger

log = get_logger(__name__)


def _ports_from_registry():
    """从注册表 SERIALCOMM 读串口列表。

    pyserial 3.5 的 comports() 只通过 SetupAPI 枚举「设备接口」，而虚拟串口驱动
    （VSerial、com0com 之类）只往 SERIALCOMM 注册、不暴露设备接口：comports()
    返回空列表，但这些端口照样能正常打开。少列一个端口，用户就完全没法选它。
    """
    try:
        import winreg
    except ImportError:
        return []

    try:
        key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                             r"HARDWARE\DEVICEMAP\SERIALCOMM")
    except OSError:
        return []

    ports = []
    try:
        index = 0
        while True:
            try:
                _, value, _ = winreg.EnumValue(key, index)
            except OSError:
                break
            index += 1
            if isinstance(value, str) and value.strip():
                ports.append(value.strip())
    finally:
        key.Close()
    return ports


def _port_sort_key(name):
    """COM2 要排在 COM10 前面，按字符串排会反。"""
    digits = "".join(c for c in name if c.isdigit())
    if digits:
        return (0, int(digits), name)
    return (1, 0, name)


class SerialSender:
    def __init__(self, port=None, baud_rate=9600):
        self.port = port
        self.baud_rate = baud_rate
        self._serial = None

    @staticmethod
    def list_ports():
        """可用串口列表（去重 + 按序号排序）。

        必须两层都查：只靠 pyserial 的 comports() 会漏掉虚拟串口驱动注册的端口，
        菜单里就一个都看不到，刷新多少次都一样。
        """
        setupapi = []
        try:
            setupapi = [p.device for p in serial.tools.list_ports.comports()]
        except Exception:
            log.debug("comports() failed", exc_info=True)

        registry = _ports_from_registry()
        only_registry = [p for p in registry if p not in setupapi]
        if only_registry:
            # SetupAPI 枚举不到、只有注册表能看到 —— 这类端口最容易被漏掉
            log.debug("仅注册表可见的串口: %s", only_registry)

        ports = sorted(dict.fromkeys(p for p in setupapi + registry if p),
                       key=_port_sort_key)
        log.debug("可用串口: %s", ports)
        return ports

    def _bytes_per_second(self):
        try:
            baud = int(self.baud_rate)
        except (TypeError, ValueError):
            baud = 9600
        # 串口 8N1：每字节 10 bit
        return max(1.0, baud / 10.0)

    def frame_bytes(self, data: dict):
        return len(self._encode(data))

    def drain_seconds(self, data: dict):
        """这一帧在线上要占用多久。"""
        return len(self._encode(data)) / self._bytes_per_second()

    def open(self):
        if self._serial and self._serial.is_open:
            return
        if not self.port:
            raise ValueError("未配置串口端口")
        self.close()
        self._serial = serial.Serial(
            port=self.port,
            baudrate=self.baud_rate,
            bytesize=serial.EIGHTBITS,
            parity=serial.PARITY_NONE,
            stopbits=serial.STOPBITS_ONE,
            timeout=1,
            # 波特率不够时 write 会一直阻塞，给个上限兜底
            write_timeout=2,
        )

    def close(self):
        ser, self._serial = self._serial, None
        if ser is None:
            return
        try:
            if ser.is_open:
                ser.close()
        except Exception:
            pass

    @property
    def is_open(self):
        return self._serial is not None and self._serial.is_open

    @staticmethod
    def _encode(data: dict):
        return (json.dumps(data, ensure_ascii=False) + "\n").encode("utf-8")

    def send(self, data: dict, budget_seconds=None):
        """发送一帧。

        返回 False 表示这一帧在给定时间内发不完，已主动跳过。
        主动跳过而不是硬写，是为了避免超时只写出去半行，把对端的 JSON
        解析搞乱（串口没有帧边界，只能靠换行）。
        """
        if not self.is_open:
            raise RuntimeError("串口未打开")
        payload = self._encode(data)
        if budget_seconds is not None:
            if len(payload) / self._bytes_per_second() > budget_seconds:
                return False
        self._serial.write(payload)
        self._serial.flush()
        return True
