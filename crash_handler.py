
import sys
import traceback
import datetime
import os
from pathlib import Path

def setup_crash_handler():
    """设置全局崩溃处理器"""

    def handle_exception(exc_type, exc_value, exc_traceback):
        if issubclass(exc_type, KeyboardInterrupt):
            sys.__excepthook__(exc_type, exc_value, exc_traceback)
            return

        timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
        error_dir = Path.home() / ".phone_controller" / "crash_reports"
        error_dir.mkdir(parents=True, exist_ok=True)

        error_file = error_dir / f"crash_{timestamp}.txt"

        with open(error_file, 'w', encoding='utf-8') as f:
            f.write(f"=== Phone Controller Crash Report ===\n")
            f.write(f"Time: {datetime.datetime.now()}\n")
            f.write(f"Version: 1.0.0\n")
            f.write(f"Python: {sys.version}\n")
            f.write(f"OS: {os.name} {sys.platform}\n\n=== Error Details ===\n")
            traceback.print_exception(exc_type, exc_value, exc_traceback, file=f)

        try:
            from PyQt6.QtWidgets import QApplication, QMessageBox
            if QApplication.instance():
                QMessageBox.critical(
                    None,
                    "程序崩溃",
                    f"程序已崩溃！\n\n"
                    f"类型: {exc_type.__name__}\n"
                    f"信息: {exc_value}\n\n"
                    f"报告保存位置:\n{error_file}"
                )
        except Exception:
            print(f"错误报告已保存到: {error_file}")

    sys.excepthook = handle_exception
