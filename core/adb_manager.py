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
        self.wireless_devices = []  # 存储无线设备信息

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
                self.client = AdbClient(host="127.0.0.1", port=5037)
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

    def pair_wireless_device(self, ip_port, pairing_code):
        """配对无线设备（Android 11+）"""
        try:
            # 格式：adb pair ip:port pairing_code
            cmd = [str(self.adb_path), "pair", ip_port]
            
            # 使用stdin输入配对码
            result = subprocess.run(
                cmd,
                input=f"{pairing_code}\n",
                capture_output=True, 
                text=True, 
                timeout=10
            )
            
            output = result.stdout.strip()
            
            # 解析配对结果
            if "Successfully paired" in output or "成功配对" in output:
                # 提取IP地址用于后续连接
                ip = ip_port.split(':')[0] if ':' in ip_port else ip_port
                return True, f"配对成功|{ip}"  # 返回IP用于后续连接
            elif "Failed" in output or "失败" in output:
                return False, f"配对失败: {output}"
            else:
                return False, f"未知结果: {output}"
                
        except subprocess.TimeoutExpired:
            return False, "配对超时"
        except Exception as e:
            return False, f"配对错误: {str(e)}"
    
    def connect_wireless_device(self, ip_port):
        """连接无线设备"""
        try:
            # 格式：adb connect ip:port
            result = subprocess.run(
                [str(self.adb_path), "connect", ip_port],
                capture_output=True, 
                text=True, 
                timeout=10
            )
            
            output = result.stdout.strip()
            
            # 判断各种可能的返回信息
            if "connected" in output.lower():
                # 添加到无线设备列表
                if ip_port not in self.wireless_devices:
                    self.wireless_devices.append(ip_port)
                return True, output
            elif "already connected" in output.lower():
                # 已经连接也是成功
                if ip_port not in self.wireless_devices:
                    self.wireless_devices.append(ip_port)
                return True, "设备已连接"
            elif "failed" in output.lower():
                return False, f"连接失败: {output}"
            elif "cannot connect" in output.lower():
                return False, f"无法连接: {output}"
            elif "refused" in output.lower():
                return False, f"连接被拒绝，请检查设备是否开启无线调试"
            else:
                # 未知响应，但可能成功了，尝试检查设备列表
                if result.returncode == 0:
                    return True, f"可能已连接: {output}"
                else:
                    return False, f"连接结果未知: {output}"
                
        except subprocess.TimeoutExpired:
            return False, "连接超时"
        except Exception as e:
            return False, f"连接错误: {str(e)}"
    
    def disconnect_wireless_device(self, ip_port=None):
        """断开无线设备"""
        try:
            if ip_port:
                cmd = [str(self.adb_path), "disconnect", ip_port]
            else:
                cmd = [str(self.adb_path), "disconnect"]
                
            result = subprocess.run(
                cmd,
                capture_output=True, 
                text=True, 
                timeout=5
            )
            
            # 从列表中移除
            if ip_port and ip_port in self.wireless_devices:
                self.wireless_devices.remove(ip_port)
                
            return True, result.stdout
            
        except Exception as e:
            return False, f"断开错误: {str(e)}"
    
    def enable_wireless_debugging(self, port=5555):
        """在设备上启用无线调试（需要先USB连接）"""
        try:
            if not self.device_serial:
                return False, "请先连接设备"
            
            # 设置TCP/IP模式
            result = self.shell(f"setprop service.adb.tcp.port {port}")
            
            # 重启adbd
            self.shell("stop adbd")
            time.sleep(1)
            self.shell("start adbd")
            time.sleep(2)
            
            # 获取设备IP
            ip = self.get_device_ip()
            if ip:
                return True, f"{ip}:{port}"
            else:
                return False, "无法获取设备IP"
                
        except Exception as e:
            return False, f"启用失败: {str(e)}"
    
    def get_device_ip(self):
        """获取设备IP地址"""
        try:
            # 尝试多种方式获取IP
            result = self.shell("ip addr show wlan0")
            if result:
                import re
                match = re.search(r'inet (\d+\.\d+\.\d+\.\d+)', result)
                if match:
                    return match.group(1)
            
            # 备用方法
            result = self.shell("ifconfig wlan0")
            if result:
                import re
                match = re.search(r'inet addr:(\d+\.\d+\.\d+\.\d+)', result)
                if match:
                    return match.group(1)
                    
            return None
            
        except:
            return None

    def connect_device(self, serial=None):
        """连接设备（支持USB和无线）"""
        if serial:
            self.device_serial = serial
            # 如果是IP地址格式，先尝试连接
            if ':' in serial and '.' in serial:
                success, msg = self.connect_wireless_device(serial)
                if not success:
                    return False
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
            return self.shell(f"input swipe {x1} {y1} {x2} {y2} {duration}")
        return None

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
        """截图 """
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

            print("[ADB] 截图失败")
            return None

        except Exception as e:
            print(f"[ADB] 截图异常: {e}")
            return None