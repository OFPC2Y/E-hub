from admin_elevate import ensure_admin

ensure_admin()

from tray_app import main  # noqa: E402 - 必须在提权之后导入，避免无权限时初始化硬件

if __name__ == "__main__":
    main()
