import time
import random
from PIL import Image
import io
import os
import json
from PyQt6.QtCore import QObject, pyqtSignal
# from core.windows_hook_monitor import WindowsHookMonitor
from core.simple_mouse_monitor import SimpleMouseMonitor  # 使用新的简化监控器
from core.device_event_monitor import DeviceEventMonitor  # 添加设备事件监控器


class DeviceController(QObject):
    # 信号
    action_recorded = pyqtSignal(dict)

    def __init__(self, adb_manager, scrcpy_manager):
        super().__init__()
        self.adb = adb_manager
        self.scrcpy = scrcpy_manager
        # self.monitor = WindowsHookMonitor()
        self.monitor = SimpleMouseMonitor()  # 使用简化监控器
        self.device_monitor = DeviceEventMonitor(adb_manager)  # 设备事件监控器
        self.recording = False
        self.recorded_actions = []
        self.recording_mode = 'window'  # 'window' 或 'device'
        # 模拟器模式配置
        self.simulator_hwnd = None
        self.simulator_crop_rect = None
        self.target_resolution = None

        
        # 分辨率缓存
        self._cached_resolution = None
        self._resolution_cache_time = 0
        # 随机化设置
        self.enable_randomization = False
        self.position_random_range = 0.01  # 1%的坐标随机偏移
        self.delay_random_range = 0.001  # 20%的延迟随机
        self.long_press_random_range = 0.01  # 15%的长按时间随机
        self.playing = False  # 添加播放状态标志
        self.stop_playing_flag = False  # 添加停止播放标志
        # 连接监控器信号
        self.monitor.action_captured.connect(self.on_action_captured)
        self.device_monitor.action_captured.connect(self.on_action_captured)
    
    def set_simulator_config(self, hwnd, crop_rect=None, target_resolution=None):
        """设置模拟器模式配置"""
        self.simulator_hwnd = hwnd
        self.simulator_crop_rect = crop_rect
        self.target_resolution = target_resolution # (width, height)
        
        # 同步到监控器
        if self.monitor:
            self.monitor.set_simulator_config(hwnd, crop_rect)
            # 监控器的分辨率需要单独设置，因为它通常使用ADB读取
            if target_resolution:
                 self.monitor.set_manual_resolution(target_resolution)
                 
        print(f"[Controller] 模拟器配置更新: hwnd={hwnd}, crop={crop_rect}, res={target_resolution}")

    def clear_simulator_config(self):
        """清除模拟器模式配置"""
        self.simulator_hwnd = None
        self.simulator_crop_rect = None
        self.target_resolution = None
        if self.monitor:
            self.monitor.clear_simulator_config()
            self.monitor.clear_manual_resolution()
        print("[Controller] 模拟器配置已清除")



    # ... existing code ...
    
    def load_simulator_config(self, window_title):
        """加载模拟器配置"""
        try:
            config_path = "simulator_configs.json"
            if not os.path.exists(config_path):
                return None
                
            with open(config_path, 'r', encoding='utf-8') as f:
                configs = json.load(f)
                
            return configs.get(window_title)
        except Exception as e:
            print(f"加载模拟器配置失败: {e}")
            return None
            
    def save_simulator_config(self, window_title, crop_rect, resolution):
        """保存模拟器配置"""
        try:
            config_path = "simulator_configs.json"
            configs = {}
            if os.path.exists(config_path):
                with open(config_path, 'r', encoding='utf-8') as f:
                    configs = json.load(f)
                    
            configs[window_title] = {
                'crop_rect': crop_rect,
                'resolution': resolution,
                'updated_at': time.time()
            }
            
            with open(config_path, 'w', encoding='utf-8') as f:
                json.dump(configs, f, indent=2, ensure_ascii=False)
            print(f"模拟器配置已保存: {window_title}")
        except Exception as e:
            print(f"保存模拟器配置失败: {e}")
        
    def set_recording_mode(self, mode):
        """设置录制模式: 'window' 或 'device'"""
        if mode in ['window', 'device']:
            self.recording_mode = mode
            print(f"[Controller] 录制模式设置为: {mode}")
            return True
        return False
    
    def stop_playing(self):
        """停止播放"""
        if self.playing:
            self.stop_playing_flag = True
            return True
        return False

    def on_action_captured(self, action):
        """处理捕获的操作"""
        if self.recording:
            self.recorded_actions.append(action)
            self.action_recorded.emit(action)
            print(f"[Controller] 记录操作 #{len(self.recorded_actions)}: {action['type']}")

    def get_device_resolution(self, force_refresh=False):
        """获取设备分辨率"""
        # 1. 如果有手动设置的目标分辨率(模拟器模式)，优先使用
        if self.target_resolution:
            return self.target_resolution
            
        import time
        current_time = time.time()

        # 2. 如果有缓存且未过期（5秒内），直接返回缓存
        if (not force_refresh and
                self._cached_resolution and
                current_time - self._resolution_cache_time < 5):
            return self._cached_resolution

        # 3. 尝试通过ADB获取
        try:
            # 获取物理分辨率
            result = self.adb.shell("wm size")
            if result:
                if "Physical size:" in result:
                    size_str = result.split("Physical size:")[1].strip()
                    width, height = map(int, size_str.split('x'))

                    # 同时获取屏幕方向（仅在录制时打印）
                    if self.recording:
                        rotation_result = self.adb.shell("dumpsys input | grep SurfaceOrientation")
                        orientation = "portrait"

                        if rotation_result:
                            if "SurfaceOrientation: 1" in rotation_result or "SurfaceOrientation: 3" in rotation_result:
                                orientation = "landscape"
                                print(f"[Controller] 检测到横屏模式")
                            else:
                                print(f"[Controller] 检测到竖屏模式")

                        print(f"[Controller] 获取设备分辨率: {width}x{height} ({orientation})")

                    # 更新缓存
                    self._cached_resolution = (width, height)
                    self._resolution_cache_time = current_time

                    return width, height

        except Exception as e:
            if self.recording:  # 仅在录制时打印错误
                print(f"[Controller] 获取分辨率失败: {e}")

        # 4. 返回缓存或默认值
        if self._cached_resolution:
            return self._cached_resolution
        return 1440, 3200

    def add_random_offset(self, value, range_percent):
        """添加随机偏移"""
        if self.enable_randomization:
            offset = value * random.uniform(-range_percent, range_percent)
            return int(value + offset)
        return value

    def add_random_delay(self, delay):
        """添加随机延迟"""
        if self.enable_randomization:
            random_factor = random.uniform(1 - self.delay_random_range, 1 + self.delay_random_range)
            return delay * random_factor
        return delay

    def click(self, x, y, use_random=True):
        """点击指定坐标（带随机偏移）"""
        if use_random and self.enable_randomization:
            x = self.add_random_offset(x, self.position_random_range)
            y = self.add_random_offset(y, self.position_random_range)

        self.adb.tap(x, y)

        if self.recording:
            self.recorded_actions.append({
                'type': 'click',
                'x': x,
                'y': y,
                'time': time.time()
            })

    def long_click(self, x, y, duration=1000, use_random=True):
        """长按（带随机化）"""
        if use_random and self.enable_randomization:
            x = self.add_random_offset(x, self.position_random_range)
            y = self.add_random_offset(y, self.position_random_range)
            duration = self.add_random_offset(duration, self.long_press_random_range)

        self.adb.swipe(x, y, x, y, duration)

        if self.recording:
            self.recorded_actions.append({
                'type': 'long_click',
                'x': x,
                'y': y,
                'duration': duration,
                'time': time.time()
            })

    def swipe(self, x1, y1, x2, y2, duration=300, use_random=True):
        """滑动（带随机化）"""
        if use_random and self.enable_randomization:
            x1 = self.add_random_offset(x1, self.position_random_range)
            y1 = self.add_random_offset(y1, self.position_random_range)
            x2 = self.add_random_offset(x2, self.position_random_range)
            y2 = self.add_random_offset(y2, self.position_random_range)
            duration = self.add_random_offset(duration, 0.1)  # 10%的持续时间随机

        self.adb.swipe(x1, y1, x2, y2, duration)

        if self.recording:
            self.recorded_actions.append({
                'type': 'swipe',
                'x1': x1, 'y1': y1,
                'x2': x2, 'y2': y2,
                'duration': duration,
                'time': time.time()
            })

    def input_text(self, text):
        """输入文本"""
        self.adb.text(text)

        if self.recording:
            self.recorded_actions.append({
                'type': 'text',
                'text': text,
                'time': time.time()
            })

    def press_back(self):
        """返回键"""
        self.adb.keyevent(4)
        if self.recording:
            self.recorded_actions.append({
                'type': 'key',
                'keycode': 4,
                'key_name': 'BACK',
                'time': time.time()
            })

    def press_home(self):
        """主页键"""
        self.adb.keyevent(3)
        if self.recording:
            self.recorded_actions.append({
                'type': 'key',
                'keycode': 3,
                'key_name': 'HOME',
                'time': time.time()
            })

    def press_recent(self):
        """最近任务键"""
        self.adb.keyevent(187)
        if self.recording:
            self.recorded_actions.append({
                'type': 'key',
                'keycode': 187,
                'key_name': 'RECENT',
                'time': time.time()
            })

    def screenshot(self):
        """截图 - 支持模拟器窗口和Scrcpy窗口"""
        try:
            from core.window_capture import WindowCapture
            
            # 1. 模拟器模式：截图指定窗口并裁剪
            if self.simulator_hwnd:
                # 直接使用 WindowCapture.capture_window_by_hwnd，它支持传入 crop_rect
                return WindowCapture.capture_window_by_hwnd(self.simulator_hwnd, self.simulator_crop_rect)

            # 2. 尝试从Scrcpy窗口截图
            screenshot = WindowCapture.capture_window_safe("scrcpy", client_only=True)

            if screenshot:
                # print(f"[Controller] Scrcpy窗口截图成功: {screenshot.size}, 模式: {screenshot.mode}")
                return screenshot

            # 3. 如果窗口截图失败，才使用ADB截图
            print("[Controller] Scrcpy窗口截图失败，尝试ADB截图...")
            png_data = self.adb.screenshot()
            if png_data:
                img = Image.open(io.BytesIO(png_data))
                if img.mode not in ['RGB', 'RGBA']:
                    img = img.convert('RGB')
                print(f"[Controller] ADB截图成功: {img.size}, 模式: {img.mode}")
                return img

        except Exception as e:
            print(f"[Controller] 截图失败: {e}")

        return None

    def start_recording(self):
        """开始录制操作"""
        print(f"[Controller] 准备开始录制... 模式: {self.recording_mode}")
        self.recording = True
        self.recorded_actions = []

        # 获取设备分辨率
        width, height = self.get_device_resolution()

        if self.recording_mode == 'device':
            # 使用设备事件监控
            self.device_monitor.set_device_resolution(width, height)
            
            # 启动监控
            if not self.device_monitor.start_monitoring():
                self.recording = False
                print("[Controller] 启动设备监控失败")
                return False
                
            print(f"[Controller] 设备录制开始，分辨率: {width}x{height}")
        else:
            # 使用窗口监控
            # 设置到监控器（监控器会自动根据窗口判断方向）
            self.monitor.set_device_resolution(width, height)

            # 设置随机化参数
            self.monitor.set_randomization(
                self.enable_randomization,
                self.position_random_range
            )

            # 启动监控
            if not self.monitor.start_monitoring():
                self.recording = False
                print("[Controller] 启动监控失败")
                return False

            print(f"[Controller] 窗口录制开始，设备分辨率: {width}x{height}")
            
        return True
    def stop_recording(self):
        """停止录制"""
        print("[Controller] 停止录制...")
        self.recording = False
        
        if self.recording_mode == 'device':
            self.device_monitor.stop_monitoring()
        else:
            self.monitor.stop_monitoring()
            
        print(f"[Controller] 录制停止，共记录 {len(self.recorded_actions)} 个操作")

        # 打印录制的操作
        for i, action in enumerate(self.recorded_actions):
            print(f"  操作{i + 1}: {action}")

        return self.recorded_actions

    def play_recording(self, actions, speed=1.0, use_random=True):
        """播放录制 - 基于动作开始时间的精确控制"""
        if not actions or self.playing:
            return False

        self.playing = True
        self.stop_playing_flag = False

        try:
            # 检查设备连接
            if not self.adb.shell("echo test"):
                if not self.adb.connect_device(self.adb.device_serial):
                    return False

            print(f"[Controller] 开始播放 {len(actions)} 个操作")
            print(f"  播放速度: {speed}x")
            print(f"  随机化: {'开启' if use_random and self.enable_randomization else '关闭'}")

            # 获取第一个动作的开始时间作为基准
            base_time_ms = actions[0].get('start_time_ms', 0)

            # 记录播放开始的实际时间
            play_start_time = time.perf_counter()

            for i, action in enumerate(actions):
                if self.stop_playing_flag:
                    print("[Controller] 播放已中断")
                    break

                # 获取动作的开始时间（相对于第一个动作）
                action_start_ms = action.get('start_time_ms', 0)
                relative_start_ms = action_start_ms - base_time_ms

                # 计算应该执行的时间点
                target_time = relative_start_ms / 1000.0 / speed

                # 计算实际需要等待的时间
                elapsed_time = time.perf_counter() - play_start_time
                wait_time = target_time - elapsed_time

                # 执行等待
                if wait_time > 0.01:
                    print(f"  等待 {wait_time:.3f} 秒")
                    time.sleep(wait_time)
                elif wait_time < -0.5 and i > 0:
                    print(f"  ⚠️ 延迟 {-wait_time:.3f} 秒")

                # 执行动作
                self._execute_action(action, i, len(actions), use_random, speed)

            return not self.stop_playing_flag

        finally:
            self.playing = False
            self.stop_playing_flag = False
            print("[Controller] 播放完成")

    def _execute_action(self, action, index, total, use_random, speed):
        """执行单个动作"""
        print(f"  执行操作 {index + 1}/{total}: {action['type']}")

        try:
            action_type = action['type']

            if action_type == 'click':
                x, y = action['x'], action['y']
                if use_random and self.enable_randomization:
                    x = self.add_random_offset(x, self.position_random_range)
                    y = self.add_random_offset(y, self.position_random_range)
                print(f"    点击: ({x}, {y})")
                self.adb.tap(x, y)

            elif action_type == 'long_click':
                x, y = action['x'], action['y']
                duration = action.get('duration', 1000)

                if use_random and self.enable_randomization:
                    x = self.add_random_offset(x, self.position_random_range)
                    y = self.add_random_offset(y, self.position_random_range)
                    duration = int(duration * random.uniform(0.85, 1.15))

                # 根据播放速度调整持续时间
                actual_duration = max(50, int(duration / speed))
                print(f"    长按: ({x}, {y}) 持续 {actual_duration}ms")
                self.adb.swipe(x, y, x, y, actual_duration)

            elif action_type == 'swipe':
                x1, y1 = action['x1'], action['y1']
                x2, y2 = action['x2'], action['y2']
                duration = action.get('duration', 300)
                trajectory = action.get('trajectory', None)

                if use_random and self.enable_randomization:
                    x1 = self.add_random_offset(x1, self.position_random_range)
                    y1 = self.add_random_offset(y1, self.position_random_range)
                    x2 = self.add_random_offset(x2, self.position_random_range)
                    y2 = self.add_random_offset(y2, self.position_random_range)
                    duration = int(duration * random.uniform(0.9, 1.1))

                actual_duration = max(50, int(duration / speed))
                
                # 如果有轨迹数据，使用轨迹播放
                if trajectory and len(trajectory) > 2:
                    print(f"    滑动（带轨迹）: {len(trajectory)}个轨迹点, 持续 {actual_duration}ms")
                    self._play_swipe_with_trajectory(trajectory, actual_duration, use_random)
                else:
                    # 兼容旧版本：简单的直线滑动
                    print(f"    滑动（直线）: ({x1}, {y1}) -> ({x2}, {y2}) 持续 {actual_duration}ms")
                    self.adb.swipe(x1, y1, x2, y2, actual_duration)

            elif action_type == 'text':
                print(f"    输入文本: {action['text']}")
                self.adb.text(action['text'])

            elif action_type == 'key':
                print(f"    按键: {action.get('key_name', action['keycode'])}")
                self.adb.keyevent(action['keycode'])

        except Exception as e:
            print(f"    ❌ 执行失败: {e}")
    def _play_swipe_with_trajectory(self, trajectory, duration_ms, use_random):
        """使用轨迹数据播放滑动
        
        Args:
            trajectory: [(x, y, time_ms), ...] 轨迹点列表
            duration_ms: 播放持续时间（毫秒）
            use_random: 是否添加随机偏移
        """
        if len(trajectory) < 2:
            # 退化为简单滑动
            x1, y1 = trajectory[0][0], trajectory[0][1]
            x2, y2 = trajectory[-1][0], trajectory[-1][1]
            if use_random and self.enable_randomization:
                x1 = self.add_random_offset(x1, self.position_random_range)
                y1 = self.add_random_offset(y1, self.position_random_range)
                x2 = self.add_random_offset(x2, self.position_random_range)
                y2 = self.add_random_offset(y2, self.position_random_range)
            self.adb.swipe(x1, y1, x2, y2, duration_ms)
            return
        
        # 对于复杂轨迹，使用贝塞尔曲线近似
        if len(trajectory) > 3:
            print(f"      使用贝塞尔曲线播放轨迹: {len(trajectory)}个控制点")
            self._play_bezier_swipe(trajectory, duration_ms, use_random)
            return
        
        # 简单轨迹：分段执行
        print(f"      分段播放轨迹: {len(trajectory)}个点")
        
        # 计算每段的时间
        total_segments = len(trajectory) - 1
        if total_segments <= 0:
            return
        
        # 检查时间跨度，避免division by zero
        time_span = trajectory[-1][2] - trajectory[0][2]
        if time_span <= 0:
            # 如果没有时间差异，平均分配时间
            avg_segment_duration = duration_ms // total_segments
            for i in range(total_segments):
                x1, y1, _ = trajectory[i]
                x2, y2, _ = trajectory[i + 1]
                
                if use_random and self.enable_randomization:
                    x1 = self.add_random_offset(x1, self.position_random_range * 0.3)
                    y1 = self.add_random_offset(y1, self.position_random_range * 0.3)
                    x2 = self.add_random_offset(x2, self.position_random_range * 0.3)
                    y2 = self.add_random_offset(y2, self.position_random_range * 0.3)
                
                self.adb.swipe(x1, y1, x2, y2, max(10, avg_segment_duration))
            return
        
        # 记录开始时间
        import time
        start_time = time.perf_counter()
        
        for i in range(total_segments):
            # 获取当前段的起点和终点
            x1, y1, t1 = trajectory[i]
            x2, y2, t2 = trajectory[i + 1]
            
            # 添加随机偏移
            if use_random and self.enable_randomization:
                x1 = self.add_random_offset(x1, self.position_random_range * 0.3)
                y1 = self.add_random_offset(y1, self.position_random_range * 0.3)
                x2 = self.add_random_offset(x2, self.position_random_range * 0.3)
                y2 = self.add_random_offset(y2, self.position_random_range * 0.3)
            
            # 计算该段应该的持续时间
            segment_duration = int((t2 - t1) * duration_ms / time_span) if time_span > 0 else duration_ms // total_segments
            segment_duration = max(10, segment_duration)
            
            # 执行该段滑动
            self.adb.swipe(x1, y1, x2, y2, segment_duration)
            
            # 为了连贯性，减少段间延迟
            if i < total_segments - 1:
                # 短暂延迟，避免命令堆积
                time.sleep(0.005)  # 5ms延迟
    
    def _play_bezier_swipe(self, trajectory, duration_ms, use_random):
        """使用贝塞尔曲线播放复杂轨迹
        
        将多个轨迹点转换为贝塞尔控制点，生成平滑曲线
        """
        import time
        
        # 提取关键控制点（简化轨迹）
        if len(trajectory) > 6:
            # 选择关键点：起点、1/4点、1/2点、3/4点、终点
            indices = [0, len(trajectory)//4, len(trajectory)//2, 3*len(trajectory)//4, -1]
            control_points = [trajectory[i] for i in indices]
        else:
            control_points = trajectory
        
        # 计算贝塞尔曲线点
        bezier_points = self._calculate_bezier_points(control_points, 10)  # 生成10个插值点
        
        # 执行连续的小段滑动
        segment_duration = max(20, duration_ms // len(bezier_points))
        
        for i in range(len(bezier_points) - 1):
            x1, y1 = bezier_points[i]
            x2, y2 = bezier_points[i + 1]
            
            if use_random and self.enable_randomization:
                x1 = self.add_random_offset(x1, self.position_random_range * 0.2)
                y1 = self.add_random_offset(y1, self.position_random_range * 0.2)
                x2 = self.add_random_offset(x2, self.position_random_range * 0.2)
                y2 = self.add_random_offset(y2, self.position_random_range * 0.2)
            
            self.adb.swipe(x1, y1, x2, y2, segment_duration)
            
            # 极短延迟保证流畅性
            if i < len(bezier_points) - 2:
                time.sleep(0.002)  # 2ms延迟
    
    def _calculate_bezier_points(self, control_points, num_points):
        """计算贝塞尔曲线上的点
        
        Args:
            control_points: 控制点列表 [(x, y, t), ...]
            num_points: 生成的曲线点数量
        
        Returns:
            贝塞尔曲线上的点列表 [(x, y), ...]
        """
        def bezier_point(t, points):
            """计算贝塞尔曲线上参数t对应的点"""
            n = len(points) - 1
            x = 0
            y = 0
            
            for i, (px, py, _) in enumerate(points):
                # 计算贝塞尔基函数
                coeff = self._binomial_coeff(n, i) * ((1 - t) ** (n - i)) * (t ** i)
                x += coeff * px
                y += coeff * py
            
            return int(x), int(y)
        
        # 生成曲线点
        curve_points = []
        for i in range(num_points):
            t = i / (num_points - 1) if num_points > 1 else 0
            point = bezier_point(t, control_points)
            curve_points.append(point)
        
        return curve_points
    
    def _binomial_coeff(self, n, k):
        """计算二项式系数 C(n, k)"""
        import math
        return math.factorial(n) // (math.factorial(k) * math.factorial(n - k))
    
    def set_randomization(self, enabled, position_range=0.01, delay_range=0.2, long_press_range=0.15):
        """设置随机化参数"""
        self.enable_randomization = enabled
        self.position_random_range = position_range
        self.delay_random_range = delay_range
        self.long_press_random_range = long_press_range

        print(
            f"[Controller] 随机化设置: 启用={enabled}, 位置={position_range * 100}%, 延迟={delay_range * 100}%, 长按={long_press_range * 100}%")

        # 同步到监控器
        if hasattr(self, 'monitor'):
            self.monitor.set_randomization(enabled, position_range)
        if hasattr(self, 'device_monitor'):
            # 设备监控器不需要随机化（录制时）
            pass

    def save_recording(self, filename, actions=None):
        """保存录制（新格式）"""
        import json
        if actions is None:
            actions = self.recorded_actions

        # 确保兼容性
        for action in actions:
            # 确保有start_time_ms字段
            if 'start_time_ms' not in action and 'timestamp_ms' in action:
                # 兼容旧格式
                if action['type'] in ['long_click', 'swipe']:
                    action['start_time_ms'] = action['timestamp_ms'] - action.get('duration', 0)
                    action['end_time_ms'] = action['timestamp_ms']
                else:
                    action['start_time_ms'] = action['timestamp_ms']
                    action['end_time_ms'] = action['timestamp_ms']

        with open(filename, 'w', encoding='utf-8') as f:
            json.dump(actions, f, indent=2, ensure_ascii=False)
        print(f"[Controller] 录制已保存到: {filename}")

    def load_recording(self, filename):
        """加载录制（兼容新旧格式）"""
        import json
        with open(filename, 'r', encoding='utf-8') as f:
            actions = json.load(f)

        # 转换旧格式到新格式
        for action in actions:
            if 'start_time_ms' not in action:
                if 'timestamp_ms' in action:
                    if action['type'] in ['long_click', 'swipe']:
                        action['start_time_ms'] = action['timestamp_ms'] - action.get('duration', 0)
                        action['end_time_ms'] = action['timestamp_ms']
                    else:
                        action['start_time_ms'] = action['timestamp_ms']
                        action['end_time_ms'] = action['timestamp_ms']
                elif 'time' in action:
                    # 更旧的格式
                    timestamp_ms = int(action['time'] * 1000)
                    if action['type'] in ['long_click', 'swipe']:
                        action['start_time_ms'] = timestamp_ms - action.get('duration', 0)
                        action['end_time_ms'] = timestamp_ms
                    else:
                        action['start_time_ms'] = timestamp_ms
                        action['end_time_ms'] = timestamp_ms

        print(f"[Controller] 从文件加载 {len(actions)} 个操作")
        return actions