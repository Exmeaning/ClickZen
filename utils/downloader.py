import os
import zipfile
import requests
import shutil
from pathlib import Path
from PyQt6.QtCore import QThread, pyqtSignal


class ScrcpyDownloader(QThread):
    progress = pyqtSignal(int)
    status = pyqtSignal(str)
    finished = pyqtSignal(bool)

    def __init__(self, config):
        super().__init__()
        self.config = config

    def run(self):
        try:
            scrcpy_exe = Path(self.config.get("scrcpy_path"))
            version = self.config.get("scrcpy_version")
            version_file = self.config.scrcpy_dir / "version.txt"
            
            # 检查版本
            need_update = False
            current_version = None
            
            if scrcpy_exe.exists() and version_file.exists():
                try:
                    with open(version_file, 'r') as f:
                        current_version = f.read().strip()
                    if current_version != version:
                        self.status.emit(f"发现新版本: {current_version} → {version}")
                        need_update = True
                    else:
                        self.status.emit(f"Scrcpy已是最新版本 (v{version})")
                        self.finished.emit(True)
                        return
                except:
                    need_update = True
            elif scrcpy_exe.exists():
                # 旧版本没有版本文件，需要更新
                self.status.emit(f"检测到旧版本Scrcpy，将更新到 v{version}...")
                need_update = True
            else:
                # 全新安装
                self.status.emit(f"首次安装Scrcpy v{version}...")
                need_update = True
            
            # 如果需要更新，先删除旧文件
            if need_update and scrcpy_exe.exists():
                self.status.emit("正在清理旧版本...")
                try:
                    import shutil
                    # 保留配置文件，只删除程序文件
                    for item in self.config.scrcpy_dir.iterdir():
                        if item.is_file() and item.name != 'config.json':
                            item.unlink()
                        elif item.is_dir():
                            shutil.rmtree(item)
                except Exception as e:
                    self.status.emit(f"清理旧版本失败: {e}")

            self.status.emit(f"正在下载Scrcpy v{version}...")

            url = self.config.get("scrcpy_download_url").format(version=version)

            # 下载文件
            response = requests.get(url, stream=True)
            total_size = int(response.headers.get('content-length', 0))

            zip_path = self.config.scrcpy_dir / f"scrcpy-{version}.zip"

            downloaded = 0
            with open(zip_path, 'wb') as f:
                for chunk in response.iter_content(chunk_size=8192):
                    if chunk:
                        f.write(chunk)
                        downloaded += len(chunk)
                        if total_size > 0:
                            progress = int(downloaded * 100 / total_size)
                            self.progress.emit(progress)

            self.status.emit("正在解压...")

            # 解压文件
            with zipfile.ZipFile(zip_path, 'r') as zip_ref:
                zip_ref.extractall(self.config.scrcpy_dir)

            # 移动文件到正确位置
            extracted_dir = self.config.scrcpy_dir / f"scrcpy-win64-v{version}"
            if extracted_dir.exists():
                for file in extracted_dir.iterdir():
                    file.rename(self.config.scrcpy_dir / file.name)
                extracted_dir.rmdir()

            # 删除zip文件
            zip_path.unlink()
            
            # 保存版本信息
            version_file = self.config.scrcpy_dir / "version.txt"
            with open(version_file, 'w') as f:
                f.write(version)

            self.status.emit(f"Scrcpy v{version} 安装完成")
            self.finished.emit(True)

        except Exception as e:
            self.status.emit(f"下载失败: {str(e)}")
            self.finished.emit(False)


class ADBDownloader(QThread):
    progress = pyqtSignal(int)
    status = pyqtSignal(str)
    finished = pyqtSignal(bool)

    def __init__(self, config):
        super().__init__()
        self.config = config
        self.adb_url = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip"

    def run(self):
        try:
            adb_dir = Path(self.config.get("adb_path")).parent
            adb_dir.mkdir(parents=True, exist_ok=True)

            self.status.emit("正在下载ADB...")

            # 下载文件
            response = requests.get(self.adb_url, stream=True)
            total_size = int(response.headers.get('content-length', 0))

            zip_path = adb_dir / "platform-tools.zip"

            downloaded = 0
            with open(zip_path, 'wb') as f:
                for chunk in response.iter_content(chunk_size=8192):
                    if chunk:
                        f.write(chunk)
                        downloaded += len(chunk)
                        if total_size > 0:
                            progress = int(downloaded * 100 / total_size)
                            self.progress.emit(progress)

            self.status.emit("正在解压ADB...")

            # 解压文件
            with zipfile.ZipFile(zip_path, 'r') as zip_ref:
                zip_ref.extractall(adb_dir.parent)

            # 移动文件
            platform_tools = adb_dir.parent / "platform-tools"
            if platform_tools.exists():
                for file in platform_tools.iterdir():
                    target = adb_dir / file.name
                    if file.is_file():
                        file.rename(target)

            # 删除zip文件
            zip_path.unlink()

            self.status.emit("ADB安装完成")
            self.finished.emit(True)

        except Exception as e:
            self.status.emit(f"ADB下载失败: {str(e)}")
            self.finished.emit(False)