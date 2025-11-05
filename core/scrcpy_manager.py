import subprocess
import time
import threading
import os
from pathlib import Path
from PyQt5.QtCore import QObject, pyqtSignal


class ScrcpyManager(QObject):
    # 信号
    started = pyqtSignal()
    stopped = pyqtSignal()
    error = pyqtSignal(str)
    log = pyqtSignal(str)

    def __init__(self, config, adb_manager):
        super().__init__()
        self.config = config
        self.adb_manager = adb_manager
        self.process = None
        self.window_handle = None

    def check_environment(self):
        """检查Scrcpy环境"""
        scrcpy_path = Path(self.config.get("scrcpy_path"))

        if not scrcpy_path.exists():
            self.error.emit("Scrcpy未找到，请先下载")
            return False

        # 检查scrcpy-server
        scrcpy_dir = scrcpy_path.parent
        server_path = scrcpy_dir / "scrcpy-server"

        if not server_path.exists():
            self.error.emit("scrcpy-server未找到")
            return False

        return True

    def start(self, serial=None, window_title="Scrcpy"):
        """启动Scrcpy"""
        if self.process:
            return False
        try:

            if not self.check_environment():
                return False

            scrcpy_path = Path(self.config.get("scrcpy_path"))
            scrcpy_dir = scrcpy_path.parent

            # 设置环境变量
            env = os.environ.copy()
            env['PATH'] = str(scrcpy_dir) + os.pathsep + env.get('PATH', '')

            # 如果scrcpy目录有自己的ADB，使用它
            scrcpy_adb = scrcpy_dir / "adb.exe"
            if scrcpy_adb.exists():
                env['ADB'] = str(scrcpy_adb)
            else:
                env['ADB'] = self.config.get("adb_path")

            # 构建命令
            cmd = [str(scrcpy_path)]

            # 基础参数 - 兼容新版本
            cmd.extend([
                "--window-title", window_title,
                "--stay-awake",
            ])
            
            # 检查Scrcpy版本以使用正确的参数
            scrcpy_version = self.config.get("scrcpy_version", "2.3.1")
            major_version = int(scrcpy_version.split('.')[0])
            
            # 性能参数 - 新版本参数名可能有变化
            bitrate = self.config.get("bitrate", "8M")
            if bitrate:
                # v3.0+ 使用 --video-bit-rate
                cmd.extend(["--video-bit-rate", bitrate])

            max_fps = self.config.get("max_fps")
            if max_fps:
                cmd.extend(["--max-fps", str(max_fps)])

            # 窗口大小
            window_size = self.config.get("window_size")
            if window_size:
                cmd.extend(["--max-size", str(max(window_size))])
            
            # Android 16 兼容性参数
            if major_version >= 3:
                # 新版本可能需要的额外参数
                cmd.extend(["--no-audio"])  # 默认禁用音频避免兼容问题

            # 指定设备
            if serial:
                cmd.extend(["-s", serial])

            # 打印命令用于调试
            self.log.emit(f"执行命令: {' '.join(cmd)}")

            # Windows下创建进程但不显示控制台窗口
            # 使用CREATE_NO_WINDOW而不是SW_HIDE
            if os.name == 'nt':
                # CREATE_NO_WINDOW = 0x08000000
                creationflags = 0x08000000
            else:
                creationflags = 0

            # 启动进程 - 不使用startupinfo
            self.process = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                env=env,
                creationflags=creationflags,  # 使用creationflags代替startupinfo
                cwd=str(scrcpy_dir),
                universal_newlines=True,
                bufsize=1
            )

            # 启动输出监控线程
            self.monitor_thread = threading.Thread(target=self._monitor_output, daemon=True)
            self.monitor_thread.start()

            # 等待窗口创建
            window_found = False
            for i in range(10):  # 最多等待10秒
                time.sleep(1)

                # 检查进程是否还在运行
                if self.process.poll() is not None:
                    self.error.emit("Scrcpy进程已退出")
                    return False

                # 检查窗口是否存在
                if self._check_window_exists(window_title):
                    window_found = True
                    break

                # 检查日志中是否有Renderer信息（表示窗口已创建）
                # 这个会在_monitor_output中处理

            if window_found or self.process.poll() is None:
                self.started.emit()
                self.log.emit("Scrcpy启动成功")
                return True
            else:
                self.error.emit("Scrcpy启动失败")
                return False

        except Exception as e:
            self.error.emit(f"启动Scrcpy出错: {str(e)}")
            return False

    def _check_window_exists(self, title):
        """检查窗口是否存在"""
        try:
            import win32gui

            def enum_callback(hwnd, windows):
                if win32gui.IsWindowVisible(hwnd):
                    window_text = win32gui.GetWindowText(hwnd)
                    if title in window_text:
                        windows.append(hwnd)
                return True

            windows = []
            win32gui.EnumWindows(enum_callback, windows)

            if windows:
                self.log.emit(f"找到Scrcpy窗口: {len(windows)}个")
                return True
            return False
        except Exception as e:
            self.log.emit(f"检查窗口时出错: {e}")
            # 如果无法检查窗口，假设成功
            return True

    def _monitor_output(self):
        """监控Scrcpy输出"""
        if not self.process:
            return

        try:
            for line in self.process.stdout:
                if line:
                    line = line.strip()
                    self.log.emit(f"[Scrcpy] {line}")

                    # 检查关键信息
                    if "ERROR" in line:
                        if "Could not find any ADB device" in line:
                            self.error.emit("未找到ADB设备")
                        else:
                            self.error.emit(f"Scrcpy错误: {line}")
                    elif "Device disconnected" in line:
                        self.error.emit("设备断开连接")
                        self.stop()
                    elif "INFO: Renderer:" in line:
                        # 渲染器初始化成功，窗口应该已创建
                        self.log.emit("Scrcpy窗口初始化成功")

        except Exception as e:
            self.log.emit(f"输出监控错误: {e}")

    def stop(self):
        """停止Scrcpy"""
        if self.process:
            try:
                self.process.terminate()
                time.sleep(1)
                if self.process.poll() is None:
                    self.process.kill()
            except:
                pass
            finally:
                self.process = None
                self.stopped.emit()
                self.log.emit("Scrcpy已停止")

    def is_running(self):
        """检查是否正在运行"""
        return self.process is not None and self.process.poll() is None

    def restart(self):
        """重启Scrcpy"""
        self.stop()
        time.sleep(1)
        self.start()