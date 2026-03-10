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
        # Root 模式
        self.root_mode = False
        self.root_click_method = "su_input"  # "su_input" 或 "sendevent"
        self.touch_device_path = None  # sendevent 用的触摸设备路径
        self.touch_max_x = 0
        self.touch_max_y = 0

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
        """点击屏幕"""
        if self.device_serial:
            result = self.shell(f"input tap {x} {y}")
            return result is not None
        return False

    def swipe(self, x1, y1, x2, y2, duration=300):
        """滑动屏幕"""
        if self.device_serial:
            result = self.shell(f"input swipe {x1} {y1} {x2} {y2} {duration}")
            return result is not None
        return False

    def text(self, text):
        """输入文本"""
        if self.device_serial:
            # 转义特殊字符 - ADB shell 中空格需要用引号包裹
            text = text.replace("'", "\\'")
            text = text.replace('"', '\\"')
            result = self.shell(f'input text "{text}"')
            return result is not None
        return False

    def keyevent(self, keycode):
        """发送按键事件"""
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

            # 方法2: 使用adb pull作为fallback
            try:
                remote_path = "/sdcard/clickzen_screenshot.png"
                self.shell(f"screencap -p {remote_path}")
                pull_result = subprocess.run(
                    [str(self.adb_path), "-s", self.device_serial, "pull", remote_path, "-"],
                    capture_output=True, timeout=10
                )
                self.shell(f"rm {remote_path}")
                if pull_result.returncode == 0 and pull_result.stdout:
                    return pull_result.stdout
            except Exception:
                pass

            print("[ADB] 截图失败")
            return None

        except Exception as e:
            print(f"[ADB] 截图异常: {e}")
            return None

    def scan_emulator_ports(self):
        """扫描本地常见模拟器端口，返回已连接的地址列表"""
        common_ports = [
            5555, 5557, 5559, 5561,     # 雷电/通用多开
            7555,                         # MuMu 旧版
            16384, 16416, 16448, 16480,  # MuMu 12 多开
            62001, 62025, 62026,         # 夜神
            21503,                        # 逍遥
            54001,                        # 安卓模拟器大师
        ]
        found = []
        for port in common_ports:
            addr = f"127.0.0.1:{port}"
            try:
                result = subprocess.run(
                    [str(self.adb_path), "connect", addr],
                    capture_output=True, text=True, timeout=3
                )
                output = result.stdout.strip().lower()
                if "connected" in output and "cannot" not in output and "failed" not in output:
                    found.append(addr)
                elif "already connected" in output:
                    found.append(addr)
            except (subprocess.TimeoutExpired, Exception):
                continue
        return found

    # ==================== Root 模式方法 ====================

    def check_root_access(self):
        """检查设备是否有 Root 权限
        Returns:
            (bool, str): (是否有root权限, 提示信息)
        """
        if not self.device_serial:
            return False, "未连接设备"
        try:
            result = subprocess.run(
                [str(self.adb_path), "-s", self.device_serial, "shell", "su", "-c", "id"],
                capture_output=True, text=True, timeout=10
            )
            output = result.stdout.strip()
            if "uid=0" in output:
                return True, "Root 权限验证成功"
            else:
                return False, (
                    "Root 权限获取失败。\n\n"
                    "请检查以下事项：\n"
                    "① 设备是否已 Root（安装 Magisk / KernelSU / SuperSU）\n"
                    "② 请打开 Root 管理器 App，找到 \"Shell\" 或 \"com.android.shell\"\n"
                    "③ 将其超级用户权限设置为\"允许\"\n"
                    "④ 如果手机上弹出了授权弹窗，请点击\"允许\"后重试"
                )
        except subprocess.TimeoutExpired:
            return False, "Root 检测超时，请检查手机上是否有授权弹窗等待确认"
        except Exception as e:
            return False, f"Root 检测异常: {str(e)}"

    def enable_root_mode(self):
        """启用 Root 模式
        Returns:
            (bool, str): (是否成功, 提示信息)
        """
        success, msg = self.check_root_access()
        if success:
            self.root_mode = True
            # 尝试获取触摸设备信息（用于 sendevent 模式）
            self._detect_touch_device()
            print(f"[ADB] Root 模式已启用, 点击方式: {self.root_click_method}")
        return success, msg

    def disable_root_mode(self):
        """禁用 Root 模式"""
        self.root_mode = False
        print("[ADB] Root 模式已禁用")

    def root_shell(self, command):
        """以 Root 权限执行 shell 命令"""
        if not self.device_serial:
            return None
        try:
            result = subprocess.run(
                [str(self.adb_path), "-s", self.device_serial, "shell", "su", "-c", command],
                capture_output=True, text=True, timeout=10
            )
            return result.stdout
        except:
            return None

    def root_tap(self, x, y):
        """Root 模式点击"""
        if not self.device_serial:
            return False
        if self.root_click_method == "sendevent" and self.touch_device_path:
            return self._sendevent_tap(x, y)
        result = self.root_shell(f"input tap {x} {y}")
        return result is not None

    def root_swipe(self, x1, y1, x2, y2, duration=300):
        """Root 模式滑动"""
        if not self.device_serial:
            return False
        result = self.root_shell(f"input swipe {x1} {y1} {x2} {y2} {duration}")
        return result is not None

    def root_keyevent(self, keycode):
        """Root 模式按键"""
        if not self.device_serial:
            return False
        result = self.root_shell(f"input keyevent {keycode}")
        return result is not None

    def root_text(self, text):
        """Root 模式输入文本"""
        if not self.device_serial:
            return False
        text = text.replace("'", "\\'")
        text = text.replace('"', '\\"')
        result = self.root_shell(f'input text "{text}"')
        return result is not None

    def _detect_touch_device(self):
        """检测触摸设备路径和参数（用于 sendevent 模式）"""
        try:
            result = self.root_shell("getevent -p")
            if not result:
                return
            import re
            # 查找触摸设备
            device_patterns = [
                r'(/dev/input/event\d+).*touch',
                r'(/dev/input/event\d+).*fts',
                r'(/dev/input/event\d+).*synaptics',
                r'(/dev/input/event\d+).*goodix',
            ]
            for pattern in device_patterns:
                match = re.search(pattern, result, re.IGNORECASE)
                if match:
                    self.touch_device_path = match.group(1)
                    break
            # 获取触摸范围
            x_match = re.search(r'ABS_MT_POSITION_X.*max\s+(\d+)', result)
            y_match = re.search(r'ABS_MT_POSITION_Y.*max\s+(\d+)', result)
            if x_match and y_match:
                self.touch_max_x = int(x_match.group(1))
                self.touch_max_y = int(y_match.group(1))
            if self.touch_device_path:
                print(f"[ADB] 触摸设备: {self.touch_device_path}, 范围: {self.touch_max_x}x{self.touch_max_y}")
        except Exception as e:
            print(f"[ADB] 检测触摸设备失败: {e}")

    def _sendevent_tap(self, x, y):
        """通过 sendevent 实现低延迟点击"""
        if not self.touch_device_path:
            # 回退到 su input
            return self.root_shell(f"input tap {x} {y}") is not None
        try:
            # 将屏幕坐标转换为触摸坐标
            # 需要知道屏幕分辨率来做映射
            dev = self.touch_device_path
            # 获取屏幕分辨率
            wm_result = self.shell("wm size")
            screen_w, screen_h = 1080, 2400
            if wm_result and "Physical size:" in wm_result:
                size_str = wm_result.split("Physical size:")[1].strip()
                screen_w, screen_h = map(int, size_str.split('x'))
            # 映射坐标
            touch_x = int(x * self.touch_max_x / screen_w) if self.touch_max_x > 0 else x
            touch_y = int(y * self.touch_max_y / screen_h) if self.touch_max_y > 0 else y
            # 构建 sendevent 序列
            cmds = (
                f"sendevent {dev} 3 57 0;"       # ABS_MT_TRACKING_ID = 0
                f"sendevent {dev} 3 53 {touch_x};"  # ABS_MT_POSITION_X
                f"sendevent {dev} 3 54 {touch_y};"  # ABS_MT_POSITION_Y
                f"sendevent {dev} 1 330 1;"       # BTN_TOUCH DOWN
                f"sendevent {dev} 0 0 0;"         # SYN_REPORT
                f"sendevent {dev} 3 57 -1;"       # ABS_MT_TRACKING_ID = -1
                f"sendevent {dev} 1 330 0;"       # BTN_TOUCH UP
                f"sendevent {dev} 0 0 0"          # SYN_REPORT
            )
            result = self.root_shell(cmds)
            return result is not None
        except Exception as e:
            print(f"[ADB] sendevent 点击失败: {e}")
            return self.root_shell(f"input tap {x} {y}") is not None