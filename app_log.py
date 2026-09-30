"""开发层日志：英文＋时间戳，滚动写入 exe 同目录的 logs/ehub.log。

用户可见的错误仍由界面负责展示，这里只做排查用的记录。
"""
import logging
import os
from logging.handlers import RotatingFileHandler

from app_config import app_dir

_LOGGER_NAME = "ehub"
_initialized = False
LOG_DIR = os.path.join(app_dir(), "logs")
LOG_FILE = os.path.join(LOG_DIR, "ehub.log")


def log_path():
    return LOG_FILE


def _init():
    global _initialized
    if _initialized:
        return
    _initialized = True

    logger = logging.getLogger(_LOGGER_NAME)
    logger.setLevel(logging.DEBUG)
    logger.propagate = False
    if logger.handlers:
        return

    try:
        os.makedirs(LOG_DIR, exist_ok=True)
        handler = RotatingFileHandler(
            LOG_FILE, maxBytes=512 * 1024, backupCount=3, encoding="utf-8")
        handler.setFormatter(logging.Formatter(
            "%(asctime)s %(levelname)s [%(threadName)s] %(name)s: %(message)s"))
        logger.addHandler(handler)
    except Exception:
        # 日志写不了不能影响主程序
        logger.addHandler(logging.NullHandler())


def get_logger(name):
    _init()
    return logging.getLogger(f"{_LOGGER_NAME}.{name}")


def open_log():
    """「查看日志」菜单用：用系统默认程序打开日志文件。"""
    _init()
    if not os.path.exists(LOG_FILE):
        return False
    starter = getattr(os, "startfile", None)
    if starter is None:
        return False
    try:
        starter(LOG_FILE)
        return True
    except Exception:
        return False
