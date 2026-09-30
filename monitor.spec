# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller 打包配置（单文件 exe）。

与 SystemMonitor.spec 内容一致 —— 两个文件只是历史遗留，改动请同步。
关键点：
  * uac_admin=True 才是让 exe 自带 requireAdministrator 的开关。
    只写 manifest='admin.manifest' 是没用的：PyInstaller 会拿
    uac_admin/uac_uiaccess 覆盖清单里的 requestedExecutionLevel，
    默认 False 就是 asInvoker —— exe 不申请管理员权限，LHM 读不到
    CPU/风扇传感器，而且失败方式是静默的。
  * runtime_hooks 里的 admin_runtime_hook 是二次兜底。
  * external/ 里的 .NET DLL 必须随包带出去（含 PawnIO_setup.exe）。
"""
from PyInstaller.utils.hooks import collect_all

datas = [('config.json', '.'), ('external', 'external')]
binaries = []
hiddenimports = [
    'gui_panel', 'config_ui', 'lhm_sensors',
    'app_config', 'app_log', 'admin_elevate',
    'serial', 'clr', 'pythonnet', 'ping3',
    'pycaw', 'pycaw.pycaw', 'comtypes',
]
for package in ('pystray', 'PIL', 'pynvml'):
    pkg_datas, pkg_binaries, pkg_imports = collect_all(package)
    datas += pkg_datas
    binaries += pkg_binaries
    hiddenimports += pkg_imports

a = Analysis(
    ['serial_monitor.py'],
    pathex=[],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=['admin_runtime_hook.py'],
    excludes=[],
    noarchive=False,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='SystemMonitor',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    uac_admin=True,
)
