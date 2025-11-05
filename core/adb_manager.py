import subprocess
import time
import os
from pathlib import Path
from ppadb.client import Client as AdbClient


class ADBManager:
    def __init__(self, adb_path):
        self.adb_path = Path(adb_path)
        self.client = None
        self.device = None
        self.device_serial = None

    # 在 ADBManager 类中添加以下方法

    def check_device_ready(self):
        """检查设备是否就绪"""
        if not self.device_serial:
            return False

        # 测试设备连接
        result = self.shell("echo test")
        return result is not None and "test" in result

    def wake_screen(self):
        """唤醒屏幕"""
        if self.device_serial:
            # 先检查屏幕状态
            result = self.shell("dumpsys power | grep 'Display Power'")
            if result and "state=OFF" in result:
                # 屏幕关闭，发送电源键唤醒
                self.keyevent(26)  # KEYCODE_POWER
                return True
        return False

    def tap(self, x, y):
        """点击屏幕（返回成功状态）"""
        if self.device_serial:
            result = self.shell(f"input tap {x} {y}")
            return result is not None
        return False

    def swipe(self, x1, y1, x2, y2, duration=300):
        """滑动屏幕（返回成功状态）"""
        if self.device_serial:
            result = self.shell(f"input swipe {x1} {y1} {x2} {y2} {duration}")
            return result is not None
        return False

    def text(self, text):
        """输入文本（返回成功状态）"""
        if self.device_serial:
            # 转义特殊字符
            text = text.replace(" ", "%s")
            text = text.replace("'", "\\'")
            text = text.replace('"', '\\"')
            result = self.shell(f'input text "{text}"')
            return result is not None
        return False

    def keyevent(self, keycode):
        """发送按键事件（返回成功状态）"""
        if self.device_serial:
            result = self.shell(f"input keyevent {keycode}")
            return result is not None
        return False

    def start_server(self):
        """启动ADB服务"""
        try:
            # 检查ADB是否存在
            if not self.adb_path.exists():
                raise FileNotFoundError(f"ADB不存在: {self.adb_path}")

            # 先杀死旧的ADB服务
            subprocess.run([str(self.adb_path), "kill-server"],
                           capture_output=True, text=True, timeout=5)
            time.sleep(1)

            # 启动ADB服务
            result = subprocess.run([str(self.adb_path), "start-server"],
                                    capture_output=True, text=True, timeout=10)

            if result.returncode != 0:
                pass
                return False

            time.sleep(2)

            # 尝试连接ADB
            try:
                self.client = AdbClient(host="127.0.0.1.txt", port=5037)
                # 测试连接
                self.client.version()
                return True
            except:
                # 如果连接失败，使用命令行方式
                print("使用命令行模式运行ADB")
                return True

        except subprocess.TimeoutExpired:
            print("ADB启动超时")
            return False
        except Exception as e:
            print(f"启动ADB失败: {e}")
            return False

    def get_devices(self):
        """获取设备列表（使用命令行）"""
        try:
            result = subprocess.run([str(self.adb_path), "devices"],
                                    capture_output=True, text=True, timeout=5)

            devices = []
            lines = result.stdout.strip().split('\n')

            for line in lines[1:]:  # 跳过第一行 "List of devices attached"
                if '\t' in line:
                    serial, status = line.split('\t')
                    if status == 'device':
                        info = self.get_device_info_cmd(serial)
                        devices.append((serial, info))

            return devices

        except Exception as e:
            print(f"获取设备列表失败: {e}")
            return []

    def get_device_info_cmd(self, serial):
        """通过命令行获取设备信息"""
        try:
            brand = self.shell_cmd(serial, "getprop ro.product.brand").strip()
            model = self.shell_cmd(serial, "getprop ro.product.model").strip()
            android = self.shell_cmd(serial, "getprop ro.build.version.release").strip()
            return f"{brand} {model} (Android {android})"
        except:
            return "Unknown Device"

    def shell_cmd(self, serial, command):
        """执行shell命令（命令行方式）"""
        try:
            result = subprocess.run(
                [str(self.adb_path), "-s", serial, "shell", command],
                capture_output=True, text=True, timeout=5
            )
            return result.stdout
        except:
            return ""

    def connect_device(self, serial=None):
        """连接设备"""
        if serial:
            self.device_serial = serial
        else:
            devices = self.get_devices()
            if devices:
                self.device_serial = devices[0][0]
            else:
                return False

        # 测试连接
        result = self.shell(f"echo test")
        return result is not None

    def shell(self, command, root=False):
        """执行shell命令"""
        if not self.device_serial:
            return None

        if root:
            command = f"su -c '{command}'"

        try:
            result = subprocess.run(
                [str(self.adb_path), "-s", self.device_serial, "shell", command],
                capture_output=True, text=True, timeout=5
            )
            return result.stdout
        except:
            return None

    def tap(self, x, y):
        """点击屏幕"""
        if self.device_serial:
            self.shell(f"input tap {x} {y}")

    def swipe(self, x1, y1, x2, y2, duration=300):
        """滑动屏幕"""
        if self.device_serial:
            self.shell(f"input swipe {x1} {y1} {x2} {y2} {duration}")

    def text(self, text):
        """输入文本"""
        if self.device_serial:
            # 转义特殊字符
            text = text.replace(" ", "%s")
            text = text.replace("'", "\\'")
            text = text.replace('"', '\\"')
            self.shell(f'input text "{text}"')

    def keyevent(self, keycode):
        """发送按键事件"""
        if self.device_serial:
            self.shell(f"input keyevent {keycode}")

    def screenshot(self):
        """截图 - 改进版本"""
        if not self.device_serial:
            return None

        try:
            # 方法1: 使用exec-out（推荐）
            result = subprocess.run(
                [str(self.adb_path), "-s", self.device_serial, "exec-out", "screencap", "-p"],
                capture_output=True, timeout=5
            )

            if result.returncode == 0 and result.stdout:
                # 检查数据是否为PNG格式
                if result.stdout[:8] == b'\x89PNG\r\n\x1a\n':
                    print("[ADB] 截图成功 (exec-out)")
                    return result.stdout

            # 方法2: 使用临时文件
            print("[ADB] 尝试使用临时文件方式截图...")

            # 在设备上截图
            device_path = "/sdcard/screenshot.png"
            result = self.shell(f"screencap -p {device_path}")

            if result is not None:
                # 下载到本地
                import tempfile
                with tempfile.NamedTemporaryFile(suffix='.png', delete=False) as tmp:
                    local_path = tmp.name

                # Pull文件
                pull_result = subprocess.run(
                    [str(self.adb_path), "-s", self.device_serial, "pull", device_path, local_path],
                    capture_output=True, timeout=5
                )

                if pull_result.returncode == 0:
                    # 读取文件内容
                    with open(local_path, 'rb') as f:
                        data = f.read()

                    # 删除临时文件
                    import os
                    os.unlink(local_path)

                    # 删除设备上的文件
                    self.shell(f"rm {device_path}")

                    print("[ADB] 截图成功 (pull文件)")
                    return data

            print("[ADB] 所有截图方法都失败了")
            return None

        except Exception as e:
            print(f"[ADB] 截图异常: {e}")
            return None