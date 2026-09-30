"""PyInstaller 运行时钩子：确保进程以管理员权限运行。

exe 的主要提权方式是打包时的 uac_admin=True（见 spec），Windows 在启动时就会
弹 UAC。这个钩子是兜底，覆盖直接从源码运行、或清单因故没生效的情况。
导入失败时直接跳过，不能让提权逻辑本身把程序拖崩。
"""
import sys

try:
    from admin_elevate import ensure_admin
except Exception:
    ensure_admin = None

if ensure_admin is not None:
    ensure_admin()
