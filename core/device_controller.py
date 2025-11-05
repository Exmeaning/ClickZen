import time
import random
from PIL import Image
import io
from PyQt6.QtCore import QObject, pyqtSignal
# from core.windows_hook_monitor import WindowsHookMonitor
from core.simple_mouse_monitor import SimpleMouseMonitor  # 使用新的简化监控器



class DeviceController(QObject):
    # 信号
    action_recorded = pyqtSignal(dict)

    def __init__(self, adb_manager, scrcpy_manager):
        super().__init__()
        self.adb = adb_manager
        self.scrcpy = scrcpy_manager
        # self.monitor = WindowsHookMonitor()
        self.monitor = SimpleMouseMonitor()  # 使用简化监控器
        self.recording = False
        self.recorded_actions = []
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
        """获取设备分辨率（带缓存）"""
        import time
        current_time = time.time()

        # 如果有缓存且未过期（5秒内），直接返回缓存
        if (not force_refresh and
                self._cached_resolution and
                current_time - self._resolution_cache_time < 5):
            return self._cached_resolution

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

        # 返回缓存或默认值
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
        """截图 - 优先使用Scrcpy窗口截图"""
        try:
            from core.window_capture import WindowCapture

            # 尝试从Scrcpy窗口截图
            screenshot = WindowCapture.capture_window_safe("scrcpy", client_only=True)

            if screenshot:
                print(f"[Controller] Scrcpy窗口截图成功: {screenshot.size}, 模式: {screenshot.mode}")
                return screenshot

            # 如果窗口截图失败，才使用ADB截图
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
        print("[Controller] 准备开始录制...")
        self.recording = True
        self.recorded_actions = []

        # 获取设备分辨率
        width, height = self.get_device_resolution()

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

        print(f"[Controller] 录制开始，设备分辨率: {width}x{height}")
        return True
    def stop_recording(self):
        """停止录制"""
        print("[Controller] 停止录制...")
        self.recording = False
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

                if use_random and self.enable_randomization:
                    x1 = self.add_random_offset(x1, self.position_random_range)
                    y1 = self.add_random_offset(y1, self.position_random_range)
                    x2 = self.add_random_offset(x2, self.position_random_range)
                    y2 = self.add_random_offset(y2, self.position_random_range)
                    duration = int(duration * random.uniform(0.9, 1.1))

                actual_duration = max(50, int(duration / speed))
                print(f"    滑动: ({x1}, {y1}) -> ({x2}, {y2}) 持续 {actual_duration}ms")
                self.adb.swipe(x1, y1, x2, y2, actual_duration)

            elif action_type == 'text':
                print(f"    输入文本: {action['text']}")
                self.adb.text(action['text'])

            elif action_type == 'key':
                print(f"    按键: {action.get('key_name', action['keycode'])}")
                self.adb.keyevent(action['keycode'])

        except Exception as e:
            print(f"    ❌ 执行失败: {e}")
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