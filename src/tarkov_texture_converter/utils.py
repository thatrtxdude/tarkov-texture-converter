import logging
import signal
import sys
import os
import time

def setup_logging():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s", datefmt='%Y-%m-%d %H:%M:%S')

def handle_exit(signum, frame):
    logging.getLogger(__name__).info("Shutdown signal received. Exiting gracefully...")
    sys.exit(0)

def setup_signal_handlers():
    if hasattr(signal, 'SIGTERM'):
        signal.signal(signal.SIGTERM, handle_exit)
    signal.signal(signal.SIGINT, handle_exit)

def format_execution_time(seconds: float) -> str:
    """Converts seconds into a string like 'Xh Ym Z.ZZs'."""
    hours, remainder = divmod(seconds, 3600)
    minutes, seconds = divmod(remainder, 60)
    return f"{int(hours)}h {int(minutes)}m {seconds:.2f}s"

def insert_suffix(filename: str, suffix: str) -> str:
    """Inserts a suffix into a filename before its extension."""
    base, ext = os.path.splitext(filename)
    if base and suffix and not suffix.startswith('_'):
        suffix = '_' + suffix
    return f"{base}{suffix}{ext}"