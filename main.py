
import sys
import os
import traceback
from PyQt6.QtWidgets import QApplication, QProgressDialog, QMessageBox
from PyQt6.QtCore import Qt, QCoreApplication

# 添加项目路径
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from utils.config import config
from utils.downloader import ScrcpyDownloader
from core.adb_manager import ADBManager
from core.scrcpy_manager import ScrcpyManager
from core.device_controller import DeviceController
from gui.main_window import MainWindow


class PhoneControllerApp:
    def __init__(self):
        # 在创建QApplication之前设置属性（PyQt6）
        QApplication.setHighDpiScaleFactorRoundingPolicy(
            Qt.HighDpiScaleFactorRoundingPolicy.PassThrough
        )
        
        self.app = QApplication(sys.argv)
        self.app.setStyle('Fusion')

        # 设置应用信息
        self.app.setApplicationName("Phone Controller")
        self.app.setOrganizationName("PhoneController")

    def check_and_install_tools(self):
        """检查并安装ADB和Scrcpy"""
        from pathlib import Path
        from utils.downloader import ADBDownloader
        
        # 检查ADB
        adb_path = Path(config.get("adb_path"))
        if not adb_path.exists():
            # 下载ADB
            progress = QProgressDialog("正在下载ADB Platform Tools...", "取消", 0, 100)
            progress.setWindowModality(Qt.WindowModality.WindowModal)
            progress.setWindowTitle("安装ADB")
            progress.show()

            downloader = ADBDownloader(config)
            downloader.progress.connect(progress.setValue)
            downloader.status.connect(progress.setLabelText)
            downloader.start()

            while not downloader.isFinished():
                self.app.processEvents()
                if progress.wasCanceled():
                    downloader.terminate()
                    return False

            progress.close()

            if not adb_path.exists():
                QMessageBox.critical(None, "错误", "ADB安装失败")
                return False

        # 检查Scrcpy（总是检查更新）
        from utils.downloader import ScrcpyDownloader
        
        # 创建下载进度对话框
        progress = QProgressDialog("检查Scrcpy版本...", "取消", 0, 100)
        progress.setWindowModality(Qt.WindowModality.WindowModal)
        progress.setWindowTitle("Scrcpy管理")
        progress.show()

        # 创建下载器
        downloader = ScrcpyDownloader(config)

        # 连接信号
        downloader.progress.connect(progress.setValue)
        downloader.status.connect(progress.setLabelText)

        # 开始下载/更新
        downloader.start()

        # 等待完成
        while not downloader.isFinished():
            self.app.processEvents()
            if progress.wasCanceled():
                downloader.terminate()
                # 即使取消也继续运行（可能已有旧版本）
                break

        progress.close()
        
        # 检查结果
        scrcpy_path = Path(config.get("scrcpy_path"))
        if not scrcpy_path.exists():
            reply = QMessageBox.warning(
                None, 
                "警告", 
                "Scrcpy未安装或更新失败。\n"
                "某些功能可能无法正常工作。\n\n"
                "是否继续运行？",
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
            )
            if reply == QMessageBox.StandardButton.No:
                return False

        return True

    def run(self):
        """运行应用"""
        try:
            # PyQt6默认启用高DPI支持，不需要手动设置
            
            # 检查并安装必要工具
            if not self.check_and_install_tools():
                return 1

            # 初始化管理器
            adb_manager = ADBManager(config.get("adb_path"))

            # 启动ADB服务
            if not adb_manager.start_server():
                QMessageBox.critical(None, "错误",
                                     f"无法启动ADB服务\n请确认ADB路径正确: {config.get('adb_path')}")
                return 1

            # 创建Scrcpy管理器
            scrcpy_manager = ScrcpyManager(config, adb_manager)

            # 创建设备控制器
            controller = DeviceController(adb_manager, scrcpy_manager)

            # 创建主窗口
            window = MainWindow(config, adb_manager, scrcpy_manager, controller)
            window.show()

            # 自动刷新设备列表
            window.refresh_devices()

            # 运行应用
            return self.app.exec()

        except Exception as e:
            QMessageBox.critical(None, "错误", f"程序启动失败:\n{str(e)}")
            return 1


# 设置异常钩子
def exception_hook(exctype, value, tb):
    """全局异常处理 - 增强版，支持错误报告生成"""
    import datetime
    from pathlib import Path
    
    error_msg = ''.join(traceback.format_exception(exctype, value, tb))
    print(f"未捕获的异常:\n{error_msg}")
    
    # 生成错误报告
    try:
        timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
        error_dir = Path.home() / ".phone_controller" / "crash_reports"
        error_dir.mkdir(parents=True, exist_ok=True)
        
        error_file = error_dir / f"crash_{timestamp}.txt"
        
        with open(error_file, 'w', encoding='utf-8') as f:
            f.write(f"=== Phone Controller Crash Report ===\n")
            f.write(f"Time: {datetime.datetime.now()}\n")
            f.write(f"Python: {sys.version}\n")
            f.write(f"OS: {os.name} {sys.platform}\n")
            f.write(f"\n=== Error Details ===\n")
            f.write(error_msg)
            f.write(f"\n=== System Info ===\n")
            f.write(f"Working Directory: {os.getcwd()}\n")
            f.write(f"Executable: {sys.executable}\n")
        
        # 显示错误对话框
        try:
            QMessageBox.critical(None, "程序错误",
                                f"发生了一个错误:\n{exctype.__name__}: {value}\n\n"
                                f"错误报告已保存到:\n{error_file}\n\n"
                                "请查看错误报告获取详细信息")
        except:
            print(f"错误报告已保存到: {error_file}")
            
    except Exception as e:
        print(f"无法生成错误报告: {e}")


sys.excepthook = exception_hook

def main():
    """主函数"""
    app = PhoneControllerApp()
    sys.exit(app.run())


if __name__ == "__main__":
    main()