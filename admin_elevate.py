"""管理员权限提升：未提权时以 runas 重新启动本进程，然后退出原进程。

LibreHardwareMonitor 读取传感器需要管理员权限，因此入口脚本的最前面就要调用。
"""
import ctypes
import os
import subprocess
import sys


def is_admin():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def _relaunch_command():
    """返回 (可执行文件, 参数行)。

    未打包时 argv[0] 是脚本路径，必须一起带上，否则重新启动的只是一个空的解释器。
    """
    if getattr(sys, "frozen", False):
        args = list(sys.argv[1:])
    else:
        args = [os.path.abspath(sys.argv[0])] + list(sys.argv[1:])
    return sys.executable, subprocess.list2cmdline(args)


def _warn_no_elevation():
    try:
        import tkinter.messagebox as mb
        mb.showerror(
            "权限不足",
            "本程序需要管理员权限运行。\n"
            "请右键点击程序，选择「以管理员身份运行」。",
        )
    except Exception:
        pass


def ensure_admin():
    """已提权返回 True；否则以管理员身份重启本进程并直接退出。"""
    if is_admin():
        return True

    try:
        exe, params = _relaunch_command()
        ret = ctypes.windll.shell32.ShellExecuteW(
            None, "runas", exe, params, None, 1)
    except Exception:
        ret = 0

    # ShellExecuteW 返回值 <= 32 表示失败（含用户拒绝 UAC）
    if ret <= 32:
        _warn_no_elevation()
    sys.exit(0)
