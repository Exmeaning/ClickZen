import win32gui
import win32api
import win32con
import threading
import time
from PyQt6.QtCore import QObject, pyqtSignal


class SimpleMouseMonitor(QObject):
    """重构的鼠标监控器 - 正确记录时间戳"""

    action_captured = pyqtSignal(dict)

    def __init__(self):
        super().__init__()
        self.recording = False
        self.scrcpy_hwnd = None
        self.device_resolution = (1080, 2400)
        self.client_rect = None

        # 监控线程
        self.monitor_thread = None
        self.stop_event = threading.Event()

        # 鼠标状态
        self.last_mouse_state = False
        self.mouse_down_time = None
        self.mouse_down_pos = None
        
        # 滑动轨迹记录
        self.swipe_trajectory = []  # [(x, y, time_ms), ...]
        self.min_trajectory_distance = 5  # 最小轨迹记录距离（像素）

        # 时间记录
        self.recording_start_time = None  # 录制开始时间

        # 随机化设置
        self.enable_randomization = False
        self.position_random_range = 0.01

        self.poll_interval = 10  # 轮询间隔（毫秒）
        
        # 模拟器模式配置
        self.simulator_mode = False
        self.custom_hwnd = None  # 自定义窗口句柄
        self.crop_rect = None  # 裁剪区域 (x, y, width, height)
        self.manual_resolution = None # 手动指定的分辨率 (模拟器模式)

    def set_simulator_config(self, hwnd, crop_rect):
        """设置模拟器配置"""
        self.simulator_mode = True
        self.simulator_hwnd = hwnd
        self.custom_hwnd = hwnd  # 确保custom_hwnd也被设置，以便find_scrcpy_window使用
        self.crop_rect = crop_rect
        print(f"[Monitor] 模拟器模式配置: hwnd={hwnd}, crop_rect={crop_rect}")
    
    def set_manual_resolution(self, resolution):
        """设置手动分辨率 (width, height)"""
        self.manual_resolution = resolution
        
    def clear_manual_resolution(self):
        self.manual_resolution = None

    def clear_simulator_config(self):
        """清除模拟器配置"""
        self.simulator_mode = False
        self.simulator_hwnd = None
        self.crop_rect = None
        self.manual_resolution = None
        print("[Monitor] 模拟器模式配置已清除")

    def find_scrcpy_window(self):
        """查找Scrcpy窗口或使用自定义窗口"""
        from core.window_capture import WindowCapture
        
        # 模拟器模式：使用自定义窗口
        if self.simulator_mode and self.custom_hwnd:
            if WindowCapture.find_window_by_hwnd(self.custom_hwnd):
                self.scrcpy_hwnd = self.custom_hwnd
                self.update_window_rect()
                self.detect_orientation()
                print(f"[Monitor] 使用模拟器窗口: {self.custom_hwnd}")
                return True
            else:
                print("[Monitor] 模拟器窗口无效")
                return False

        # 普通模式：查找Scrcpy窗口
        self.scrcpy_hwnd = WindowCapture.find_scrcpy_window()

        if self.scrcpy_hwnd:
            self.update_window_rect()
            self.detect_orientation()
            return True

        print("[Monitor] 未找到Scrcpy窗口")
        return False

    def update_window_rect(self):
        """更新窗口位置和大小"""
        if not self.scrcpy_hwnd:
            return False

        try:
            if not win32gui.IsWindow(self.scrcpy_hwnd):
                return False

            # 获取客户区域
            rect = win32gui.GetClientRect(self.scrcpy_hwnd)
            point = win32gui.ClientToScreen(self.scrcpy_hwnd, (0, 0))

            self.client_rect = (
                point[0],
                point[1],
                point[0] + rect[2],
                point[1] + rect[3]
            )

            self.detect_orientation()
            return True

        except Exception as e:
            print(f"[Monitor] 更新窗口区域失败: {e}")
            return False

    def detect_orientation(self):
        """检测屏幕方向"""
        if self.client_rect:
            window_width = self.client_rect[2] - self.client_rect[0]
            window_height = self.client_rect[3] - self.client_rect[1]

            if window_width > 0 and window_height > 0:
                aspect_ratio = window_width / window_height
                self.device_orientation = "landscape" if aspect_ratio > 1.3 else "portrait"

    def is_point_in_window(self, x, y):
        """检查点是否在窗口内"""
        if not self.client_rect:
            return False
        return (self.client_rect[0] <= x <= self.client_rect[2] and
                self.client_rect[1] <= y <= self.client_rect[3])

    def screen_to_device_coords(self, x, y):
        """将屏幕坐标转换为设备/裁剪区域坐标"""
        if not self.client_rect:
            return None, None

        # 计算相对于窗口的位置
        rel_x = x - self.client_rect[0]
        rel_y = y - self.client_rect[1]

        # 窗口大小
        window_width = self.client_rect[2] - self.client_rect[0]
        window_height = self.client_rect[3] - self.client_rect[1]

        if window_width <= 0 or window_height <= 0:
            return None, None
        
        # 模拟器模式：使用设置的裁剪区域
        if self.simulator_mode and self.crop_rect:
            cx, cy, cw, ch = self.crop_rect
            
            # 检查相对坐标是否在裁剪区域内
            if not (cx <= rel_x <= cx + cw and cy <= rel_y <= cy + ch):
                return None, None
            
            # 转换为相对于裁剪区域的坐标
            crop_rel_x = rel_x - cx
            crop_rel_y = rel_y - cy
            
            # 获取设备分辨率进行缩放映射
            # 逻辑：将裁剪区域映射到整个设备屏幕
            if self.manual_resolution:
               device_w, device_h = self.manual_resolution
            else:
               device_w, device_h = self.device_resolution
            
            # 防止除零错误
            if cw > 0 and ch > 0:
                scale_x = device_w / cw
                scale_y = device_h / ch
                
                device_x = int(crop_rel_x * scale_x)
                device_y = int(crop_rel_y * scale_y)
            else:
                device_x = int(crop_rel_x)
                device_y = int(crop_rel_y)
            
            # 确保坐标在有效范围内
            device_x = max(0, min(device_x, device_w - 1))
            device_y = max(0, min(device_y, device_h - 1))
            
            return device_x, device_y

        # 设备模式（Scrcpy）
        # 获取设备原始分辨率
        original_width, original_height = self.device_resolution

        # 根据窗口宽高比判断实际方向
        window_aspect = window_width / window_height

        if window_aspect > 1.3:  # 横屏显示
            device_width = max(original_width, original_height)
            device_height = min(original_width, original_height)
        else:  # 竖屏显示
            device_width = min(original_width, original_height)
            device_height = max(original_width, original_height)

        # 转换为设备坐标
        device_x = int(rel_x * device_width / window_width)
        device_y = int(rel_y * device_height / window_height)

        # 确保坐标在有效范围内
        device_x = max(0, min(device_x, device_width - 1))
        device_y = max(0, min(device_y, device_height - 1))

        return device_x, device_y

    def get_time_ms(self):
        """获取相对于录制开始的毫秒时间"""
        if self.recording_start_time is None:
            self.recording_start_time = time.perf_counter()
            return 0
        return int((time.perf_counter() - self.recording_start_time) * 1000)

    def start_monitoring(self):
        """开始监控"""
        if not self.find_scrcpy_window():
            return False

        self.recording = True
        self.stop_event.clear()
        self.recording_start_time = None

        self.monitor_thread = threading.Thread(target=self._monitor_loop, daemon=True)
        self.monitor_thread.start()

        print("[Monitor] 开始监控鼠标操作")
        return True

    def stop_monitoring(self):
        """停止监控"""
        self.recording = False
        self.stop_event.set()

        if self.monitor_thread and self.monitor_thread.is_alive():
            self.monitor_thread.join(timeout=1)

    def _monitor_loop(self):
        """监控循环 - 重构的时间记录"""
        while self.recording and not self.stop_event.is_set():
            try:
                # 更新窗口位置
                if not self.update_window_rect():
                    time.sleep(0.1)
                    continue

                # 获取鼠标位置
                cursor_pos = win32gui.GetCursorPos()
                x, y = cursor_pos

                # 检查鼠标是否在窗口内
                if not self.is_point_in_window(x, y):
                    if self.last_mouse_state:
                        self.last_mouse_state = False
                        self.mouse_down_time = None
                        self.mouse_down_pos = None
                    time.sleep(self.poll_interval / 1000.0)
                    continue

                # 检查鼠标左键状态
                left_button_state = win32api.GetAsyncKeyState(win32con.VK_LBUTTON) < 0

                # 检测鼠标按下
                if left_button_state and not self.last_mouse_state:
                    # 鼠标刚按下
                    device_x, device_y = self.screen_to_device_coords(x, y)
                    if device_x is not None:
                        # 记录按下时间（动作开始时间）
                        self.mouse_down_time = self.get_time_ms()
                        self.mouse_down_pos = (x, y, device_x, device_y)
                        # 初始化轨迹
                        self.swipe_trajectory = [(device_x, device_y, self.mouse_down_time)]
                        print(f"[Monitor] 鼠标按下: 设备({device_x}, {device_y}) 时间: {self.mouse_down_time}ms")
                
                # 检测鼠标移动（按下状态）
                elif left_button_state and self.last_mouse_state:
                    # 鼠标按下并移动
                    if self.mouse_down_pos is not None:
                        device_x, device_y = self.screen_to_device_coords(x, y)
                        if device_x is not None:
                            # 检查是否需要记录轨迹点
                            if self.swipe_trajectory:
                                last_x, last_y, _ = self.swipe_trajectory[-1]
                                distance = ((device_x - last_x) ** 2 + (device_y - last_y) ** 2) ** 0.5
                                # 只记录有意义的移动
                                if distance >= self.min_trajectory_distance:
                                    current_time = self.get_time_ms()
                                    self.swipe_trajectory.append((device_x, device_y, current_time))

                # 检测鼠标释放
                elif not left_button_state and self.last_mouse_state:
                    # 鼠标刚释放
                    if self.mouse_down_time is not None and self.mouse_down_pos:
                        device_x, device_y = self.screen_to_device_coords(x, y)
                        if device_x is not None:
                            # 记录释放时间（动作结束时间）
                            release_time = self.get_time_ms()
                            duration_ms = release_time - self.mouse_down_time

                            start_x, start_y, start_device_x, start_device_y = self.mouse_down_pos
                            move_distance = ((x - start_x) ** 2 + (y - start_y) ** 2) ** 0.5

                            # 创建动作记录（包含开始和结束时间）
                            action = None

                            if move_distance > 10:  # 滑动
                                # 添加最后一个点到轨迹
                                if self.swipe_trajectory and (device_x, device_y, release_time) not in self.swipe_trajectory:
                                    self.swipe_trajectory.append((device_x, device_y, release_time))
                                
                                # 简化轨迹
                                from core.trajectory_utils import simplify_trajectory
                                simplified_trajectory = simplify_trajectory(self.swipe_trajectory) if len(self.swipe_trajectory) > 2 else self.swipe_trajectory
                                
                                action = {
                                    'type': 'swipe',
                                    'x1': start_device_x,
                                    'y1': start_device_y,
                                    'x2': device_x,
                                    'y2': device_y,
                                    'start_time_ms': self.mouse_down_time,  # 动作开始时间
                                    'end_time_ms': release_time,  # 动作结束时间
                                    'duration': duration_ms,  # 持续时间
                                    'orientation': self.device_orientation,
                                    'trajectory': simplified_trajectory  # 添加轨迹数据
                                }
                                print(
                                    f"[Monitor] 滑动: 持续{duration_ms}ms, 轨迹点数:{len(simplified_trajectory)}, 开始:{self.mouse_down_time}ms, 结束:{release_time}ms")

                            elif duration_ms >= 500:  # 长按
                                action = {
                                    'type': 'long_click',
                                    'x': device_x,
                                    'y': device_y,
                                    'start_time_ms': self.mouse_down_time,  # 动作开始时间
                                    'end_time_ms': release_time,  # 动作结束时间
                                    'duration': duration_ms,  # 持续时间
                                    'orientation': self.device_orientation
                                }
                                print(
                                    f"[Monitor] 长按: 持续{duration_ms}ms, 开始:{self.mouse_down_time}ms, 结束:{release_time}ms")

                            else:  # 普通点击
                                action = {
                                    'type': 'click',
                                    'x': device_x,
                                    'y': device_y,
                                    'start_time_ms': self.mouse_down_time,  # 点击时间
                                    'end_time_ms': release_time,  # 基本相同
                                    'duration': 0,  # 点击无持续时间
                                    'orientation': self.device_orientation
                                }
                                print(f"[Monitor] 点击: 时间:{self.mouse_down_time}ms")

                            if action:
                                # 发送信号
                                self.action_captured.emit(action)

                        # 重置状态
                        self.mouse_down_time = None
                        self.mouse_down_pos = None

                # 更新状态
                self.last_mouse_state = left_button_state

                # 短暂休眠
                time.sleep(self.poll_interval / 1000.0)

            except Exception as e:
                print(f"[Monitor] 监控循环错误: {e}")
                time.sleep(0.1)

        pass

    def set_device_resolution(self, width, height):
        self.device_resolution = (width, height)
        print(f"[Monitor] 设备分辨率设置为: {width}x{height}")

    def set_randomization(self, enabled, position_range=0.01, *args):
        self.enable_randomization = False
        self.position_random_range = position_range