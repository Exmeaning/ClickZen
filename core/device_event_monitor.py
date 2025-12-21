import subprocess
import threading
import time
import re
from PyQt6.QtCore import QObject, pyqtSignal


class DeviceEventMonitor(QObject):
    """通过ADB监控设备触摸事件"""
    
    action_captured = pyqtSignal(dict)
    log_message = pyqtSignal(str)
    error_occurred = pyqtSignal(str)
    
    def __init__(self, adb_manager):
        super().__init__()
        self.adb = adb_manager
        self.recording = False
        self.monitor_thread = None
        self.stop_event = threading.Event()
        self.recording_start_time = None
        
        # 设备信息
        self.device_resolution = (1080, 2400)
        self.touch_device = None
        self.max_x = 0
        self.max_y = 0
        
        # 触摸状态跟踪
        self.touch_slots = {}  # 多点触控槽位
        self.current_slot = 0
        self.touch_start_time = None
        self.touch_start_pos = None
        self.touch_trajectory = []  # 触摸轨迹
        self.min_trajectory_distance = 20  # 最小轨迹记录距离（设备坐标）
        
        # 调试设置
        self.debug_settings = self._load_debug_settings()
        self.raw_events_file = None
        
    def get_device_info(self):
        """获取设备触摸屏信息"""
        try:
            self._debug_log("开始获取设备信息...")
            
            # 先查找触摸设备
            cmd = "getevent -p"
            self._debug_log(f"执行命令: {cmd}")
            result = self.adb.shell(cmd)
            
            if not result:
                self._debug_log("无法获取设备事件信息")
                # 使用默认值，但返回True以继续
                self.max_x = 4095  # 常见的触摸屏最大值
                self.max_y = 4095
                self.touch_device = '/dev/input/event5'  # 从您的日志看是event5
                return True
            
            self._debug_log(f"命令返回长度: {len(result)}")
            
            # 在调试模式下输出部分结果
            if self.debug_settings.get("device_events", False):
                lines = result.split('\n')[:20]
                for line in lines:
                    if 'event' in line or 'ABS' in line:
                        self._debug_log(f"  {line.strip()}")
            
            if result:
                # 解析最大X和Y值 - 修正正则表达式
                # 格式: ABS_MT_POSITION_X : value 0, min 0, max 1439, fuzz 0, flat 0, resolution 0
                x_match = re.search(r'ABS_MT_POSITION_X.*max\s+(\d+)', result)
                if not x_match:
                    # 尝试另一种格式
                    x_match = re.search(r'0035.*:\s*value.*max\s+(\d+)', result)
                    
                y_match = re.search(r'ABS_MT_POSITION_Y.*max\s+(\d+)', result)
                if not y_match:
                    # 尝试另一种格式
                    y_match = re.search(r'0036.*:\s*value.*max\s+(\d+)', result)
                
                if x_match and y_match:
                    self.max_x = int(x_match.group(1))
                    self.max_y = int(y_match.group(1))
                    # 触控分辨率通常是真实分辨率的10倍左右
                    self.log_message.emit(f"触控坐标范围: 0-{self.max_x} x 0-{self.max_y}")
                    
                    # 获取并显示真实分辨率
                    wm_result = self.adb.shell("wm size")
                    if wm_result and "Physical size:" in wm_result:
                        size_str = wm_result.split("Physical size:")[1].strip()
                        real_width, real_height = map(int, size_str.split('x'))
                        self.log_message.emit(f"设备真实分辨率: {real_width}x{real_height}")
                        # 计算比例关系
                        scale_x = self.max_x / real_width if real_width > 0 else 10
                        scale_y = self.max_y / real_height if real_height > 0 else 10
                        self._debug_log(f"坐标缩放比例: X={scale_x:.1f}, Y={scale_y:.1f}")
                else:
                    # 基于实际观察值设置默认值
                    # 从日志看，坐标值约为 0x1a2d(6701) x 0x4581(17793)
                    # 但考虑到10倍关系，使用标准值
                    self.max_x = 14399  # 1440*10-1
                    self.max_y = 31999  # 3200*10-1
                    self._debug_log(f"使用默认触控参数: {self.max_x}x{self.max_y}")
                    
                # 查找触摸设备 - 查找包含触摸相关关键字的设备
                # 常见的触摸设备名称包括: touch, fts, synaptics, goodix等
                device_patterns = [
                    r'(/dev/input/event\d+).*touch',
                    r'(/dev/input/event\d+).*fts',  # 如您设备上的fts
                    r'(/dev/input/event\d+).*synaptics',
                    r'(/dev/input/event\d+).*goodix'
                ]
                
                for pattern in device_patterns:
                    device_match = re.search(pattern, result, re.IGNORECASE)
                    if device_match:
                        self.touch_device = device_match.group(1)
                        self.log_message.emit(f"找到触摸设备: {self.touch_device}")
                        self._debug_log(f"使用触摸设备: {self.touch_device}")
                        break
                
                # 如果还是没找到，尝试根据您的设备输出，使用event5（fts）
                if not self.touch_device:
                    # 检查是否有event5并且是fts设备
                    if '/dev/input/event5' in result and 'fts' in result:
                        self.touch_device = '/dev/input/event5'
                        self.log_message.emit(f"使用fts触摸设备: {self.touch_device}")
                    else:
                        # 默认使用event5
                        self.touch_device = '/dev/input/event5'
                        self.log_message.emit(f"使用默认触摸设备: {self.touch_device}")
                            
                return True  # 始终返回True以继续
            
        except Exception as e:
            self.error_occurred.emit(f"获取设备信息失败: {e}")
            return False
            
        return False
        
    def set_device_resolution(self, width, height):
        """设置设备分辨率"""
        self.device_resolution = (width, height)
        
    def get_time_ms(self):
        """获取相对于录制开始的毫秒时间"""
        if self.recording_start_time is None:
            self.recording_start_time = time.perf_counter()
            return 0
        return int((time.perf_counter() - self.recording_start_time) * 1000)
        
    def _load_debug_settings(self):
        """加载调试设置"""
        try:
            import json
            import os
            if os.path.exists("settings.json"):
                with open("settings.json", 'r', encoding='utf-8') as f:
                    settings = json.load(f)
                    return settings.get("debug", {})
        except:
            pass
        return {}
    
    def _debug_log(self, msg, level="device_events"):
        """调试日志输出"""
        if self.debug_settings.get(level, False):
            self.log_message.emit(f"[DEBUG] {msg}")
    
    def start_monitoring(self):
        """开始监控"""
        self._debug_log(f"开始监控，设备序列号: {self.adb.device_serial}")
        
        if not self.adb.device_serial:
            self.error_occurred.emit("未连接设备")
            self._debug_log("错误: 设备序列号为空")
            return False
            
        # 测试设备连接
        test_result = self.adb.shell("echo test")
        self._debug_log(f"设备连接测试结果: {test_result}")
        
        if not test_result or "test" not in str(test_result):
            self.error_occurred.emit("设备未正确连接")
            return False
            
        # 获取设备信息
        if not self.get_device_info():
            # 使用默认值
            self.max_x = 1080
            self.max_y = 2400
            self.log_message.emit("使用默认触摸屏参数")
            self._debug_log(f"使用默认参数: {self.max_x}x{self.max_y}")
            
        self.recording = True
        self.stop_event.clear()
        self.recording_start_time = None
        
        self.monitor_thread = threading.Thread(target=self._monitor_events, daemon=True)
        self.monitor_thread.start()
        
        self.log_message.emit("开始监控设备触摸事件")
        return True
        
    def stop_monitoring(self):
        """停止监控"""
        self.recording = False
        self.stop_event.set()
        
        if self.monitor_thread and self.monitor_thread.is_alive():
            self.monitor_thread.join(timeout=2)
            
        self.log_message.emit("停止监控设备触摸事件")
        
    def _monitor_events(self):
        """监控事件循环"""
        try:
            # 使用getevent监控
            if self.touch_device:
                cmd = f"getevent -lt {self.touch_device}"
            else:
                cmd = "getevent -lt"
            
            self._debug_log(f"执行监控命令: {cmd}")
            
            # 如果需要保存原始事件
            if self.debug_settings.get("save_raw_events", False):
                from datetime import datetime
                timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
                filename = f"raw_events_{timestamp}.txt"
                self.raw_events_file = open(filename, 'w', encoding='utf-8')
                self._debug_log(f"保存原始事件到: {filename}")
                
            # 启动getevent进程
            full_cmd = [str(self.adb.adb_path), "-s", self.adb.device_serial, "shell", cmd]
            self._debug_log(f"完整命令: {' '.join(full_cmd)}")
            
            process = subprocess.Popen(
                full_cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                bufsize=1
            )
            
            # 事件缓冲
            event_buffer = []
            last_syn_time = time.time()
            
            # 记录已输出的行数（用于调试）
            debug_line_count = 0
            max_debug_lines = 20  # 增加调试输出行数
            
            while self.recording and not self.stop_event.is_set():
                try:
                    line = process.stdout.readline()
                    if not line:
                        break
                    
                    # 保存原始事件
                    if self.raw_events_file:
                        self.raw_events_file.write(line)
                        self.raw_events_file.flush()
                    
                    # 调试：输出前N行
                    if self.debug_settings.get("device_events", False):
                        if debug_line_count < max_debug_lines:
                            self._debug_log(f"原始行: {line.strip()}", "device_events")
                            debug_line_count += 1
                        
                    # 解析事件
                    event = self._parse_event_line(line.strip())
                    if event:
                        event_buffer.append(event)
                        
                        # 详细的触摸事件调试
                        if self.debug_settings.get("touch_events", False) and debug_line_count < max_debug_lines:
                            if event['type'] in ['EV_ABS', 'EV_KEY', 'EV_SYN']:
                                self._debug_log(f"解析事件: type={event['type']}, code={event['code']}, value={event['value']}", "touch_events")
                        
                        # 当收到SYN_REPORT时，处理整批事件
                        if event['type'] == 'EV_SYN' and event['code'] == 'SYN_REPORT':
                            if len(event_buffer) > 1:  # 有实际事件才处理
                                if self.debug_settings.get("touch_events", False):
                                    self._debug_log(f"收到SYN_REPORT，处理 {len(event_buffer)} 个事件", "touch_events")
                                self._process_event_batch(event_buffer)
                            event_buffer.clear()
                            last_syn_time = time.time()
                        
                        # 防止缓冲区过大
                        elif len(event_buffer) > 100:
                            self._debug_log("事件缓冲区过大，清空", "touch_events")
                            event_buffer.clear()
                            
                except Exception as e:
                    if self.recording:
                        self.log_message.emit(f"解析事件错误: {e}")
                        
            # 终止进程
            try:
                process.terminate()
                process.wait(timeout=1)
            except:
                try:
                    process.kill()
                except:
                    pass
            
            # 关闭原始事件文件
            if self.raw_events_file:
                self.raw_events_file.close()
                self._debug_log("原始事件文件已保存")
                    
        except Exception as e:
            self.error_occurred.emit(f"监控事件失败: {e}")
            self._debug_log(f"监控异常: {str(e)}")
            
    def _parse_event_line(self, line):
        """解析getevent输出行"""
        try:
            # 跳过设备添加行
            if line.startswith('add device') or line.startswith('  name:'):
                return None
            
            # 实际格式（从日志看）: [   23657.216643] EV_ABS       ABS_MT_POSITION_X    00001919
            # 注意：使用特定设备时不会输出设备路径
            
            # 先尝试不带设备路径的格式（这是实际的格式）
            pattern = r'\[\s*([\d.]+)\]\s+(\w+)\s+(\w+)\s+([\w-]+)'
            match = re.match(pattern, line)
            
            if match:
                timestamp = match.group(1)
                event_type = match.group(2)
                event_code = match.group(3)
                event_value = match.group(4)
                device = self.touch_device or '/dev/input/event5'
            else:
                # 尝试带设备路径的格式（备用）
                pattern = r'\[\s*([\d.]+)\]\s*(/dev/input/event\d+):\s*(\w+)\s+(\w+)\s+([\w-]+)'
                match = re.match(pattern, line)
                
                if not match:
                    return None
                    
                timestamp = match.group(1)
                device = match.group(2)
                event_type = match.group(3)
                event_code = match.group(4)
                event_value = match.group(5)
                
            # 转换值
            if event_value == 'DOWN':
                value = 1
            elif event_value == 'UP':
                value = 0
            elif event_value == 'ffffffff':
                value = -1  # tracking_id 结束标记
            else:
                # 十六进制转换
                try:
                    value = int(event_value, 16)
                except:
                    value = 0
            
            return {
                'timestamp': timestamp,
                'device': device,
                'type': event_type,
                'code': event_code,
                'value': value
            }
                
        except Exception as e:
            # 静默忽略解析错误
            pass
            
        return None
        
    def _process_event_batch(self, events):
        """处理一批事件"""
        if not events:
            return
            
        # 提取关键信息
        x = None
        y = None
        tracking_id = None
        pressure = None
        btn_touch = None
        
        for event in events:
            if event['type'] == 'EV_ABS':
                if event['code'] == 'ABS_MT_POSITION_X':
                    x = event['value']
                elif event['code'] == 'ABS_MT_POSITION_Y':
                    y = event['value']
                elif event['code'] == 'ABS_MT_TRACKING_ID':
                    tracking_id = event['value']
                elif event['code'] == 'ABS_MT_PRESSURE':
                    pressure = event['value']
                elif event['code'] == 'ABS_MT_SLOT':
                    self.current_slot = event['value']
            elif event['type'] == 'EV_KEY':
                if event['code'] == 'BTN_TOUCH':
                    btn_touch = event['value']
                    
        if self.debug_settings.get("touch_events", False):
            self._debug_log(f"批处理: btn_touch={btn_touch}, tracking_id={tracking_id}, x={x}, y={y}", "touch_events")
                    
        # 使用BTN_TOUCH事件判断按下和抬起
        if btn_touch is not None:
            if btn_touch == 1:  # 按下
                if x is not None and y is not None:
                    self._debug_log(f"检测到触摸按下: ({x}, {y})", "touch_events")
                    self._handle_touch_move(x, y)
                else:
                    self._debug_log(f"按下但无坐标", "touch_events")
            elif btn_touch == 0:  # 抬起
                self._debug_log(f"检测到触摸抬起", "touch_events")
                self._handle_touch_up()
        # 如果没有BTN_TOUCH，使用tracking_id判断
        elif tracking_id is not None:
            if tracking_id == -1 or tracking_id == 0xffffffff:
                # 触摸结束
                self._debug_log(f"tracking_id指示触摸结束", "touch_events")
                self._handle_touch_up()
            else:
                # 触摸开始或移动
                if x is not None and y is not None:
                    self._debug_log(f"tracking_id指示触摸: ({x}, {y})", "touch_events")
                    self._handle_touch_move(x, y)
        # 仅有坐标更新（移动）
        elif x is not None and y is not None and self.touch_start_pos is not None:
            self._debug_log(f"坐标更新: ({x}, {y})", "touch_events")
            self._handle_touch_move(x, y)
                    
    def _handle_touch_move(self, raw_x, raw_y):
        """处理触摸移动"""
        # 转换坐标（触控坐标转换为真实设备坐标）
        if self.max_x > 0 and self.max_y > 0:
            # 根据比例转换（触控分辨率通常是真实分辨率的10倍）
            device_x = int(raw_x * self.device_resolution[0] / self.max_x)
            device_y = int(raw_y * self.device_resolution[1] / self.max_y)
        else:
            # 如果没有最大值，假设10倍关系
            device_x = raw_x // 10
            device_y = raw_y // 10
        
        if self.debug_settings.get("touch_events", False):
            self._debug_log(f"触摸坐标转换: 原始({raw_x}, {raw_y}) -> 设备({device_x}, {device_y})", "touch_events")
            self._debug_log(f"  触控范围: {self.max_x}x{self.max_y}, 设备分辨率: {self.device_resolution}", "touch_events")
            
        # 确保坐标在有效范围内
        device_x = max(0, min(device_x, self.device_resolution[0] - 1))
        device_y = max(0, min(device_y, self.device_resolution[1] - 1))
        
        # 记录起始位置
        if self.touch_start_pos is None:
            self.touch_start_time = self.get_time_ms()
            self.touch_start_pos = (device_x, device_y)
            self.touch_trajectory = [(device_x, device_y, self.touch_start_time)]
            self.log_message.emit(f"触摸开始: ({device_x}, {device_y})")
        else:
            # 更新当前位置（用于滑动）
            self.touch_slots[self.current_slot] = (device_x, device_y)
            
            # 记录轨迹点
            if self.touch_trajectory:
                last_x, last_y, _ = self.touch_trajectory[-1]
                distance = ((device_x - last_x) ** 2 + (device_y - last_y) ** 2) ** 0.5
                # 只记录有意义的移动
                if distance >= self.min_trajectory_distance:
                    current_time = self.get_time_ms()
                    self.touch_trajectory.append((device_x, device_y, current_time))
            
    def _handle_touch_up(self):
        """处理触摸抬起"""
        if self.touch_start_pos is None:
            self._debug_log("触摸抬起但没有起始位置", "touch_events")
            return
            
        # 计算持续时间
        release_time = self.get_time_ms()
        duration_ms = release_time - self.touch_start_time
        
        self._debug_log(f"触摸抬起，持续时间: {duration_ms}ms", "touch_events")
        
        start_x, start_y = self.touch_start_pos
        
        # 获取结束位置
        end_x, end_y = self.touch_slots.get(self.current_slot, self.touch_start_pos)
        
        # 计算移动距离
        move_distance = ((end_x - start_x) ** 2 + (end_y - start_y) ** 2) ** 0.5
        
        # 创建动作
        action = None
        
        if move_distance > 50:  # 滑动阈值
            # 添加最后一个点到轨迹
            if self.touch_trajectory and (end_x, end_y, release_time) not in self.touch_trajectory:
                self.touch_trajectory.append((end_x, end_y, release_time))
            
            # 简化轨迹
            from core.trajectory_utils import simplify_trajectory
            simplified_trajectory = simplify_trajectory(self.touch_trajectory) if len(self.touch_trajectory) > 2 else self.touch_trajectory
            
            action = {
                'type': 'swipe',
                'x1': start_x,
                'y1': start_y,
                'x2': end_x,
                'y2': end_y,
                'start_time_ms': self.touch_start_time,
                'end_time_ms': release_time,
                'duration': duration_ms,
                'trajectory': simplified_trajectory,  # 添加轨迹数据
                'source': 'device'
            }
            self.log_message.emit(f"滑动: ({start_x},{start_y})->({end_x},{end_y}) 轨迹点:{len(simplified_trajectory)} 持续{duration_ms}ms")
            
        elif duration_ms >= 500:  # 长按阈值
            action = {
                'type': 'long_click',
                'x': end_x,
                'y': end_y,
                'start_time_ms': self.touch_start_time,
                'end_time_ms': release_time,
                'duration': duration_ms,
                'source': 'device'
            }
            self.log_message.emit(f"长按: ({end_x},{end_y}) 持续{duration_ms}ms")
            
        else:  # 点击
            action = {
                'type': 'click',
                'x': end_x,
                'y': end_y,
                'start_time_ms': self.touch_start_time,
                'end_time_ms': release_time,
                'duration': 0,
                'source': 'device'
            }
            self.log_message.emit(f"点击: ({end_x},{end_y})")
            
        # 发送动作
        if action:
            self.action_captured.emit(action)
            
        # 重置状态
        self.touch_start_pos = None
        self.touch_start_time = None
        self.touch_trajectory = []
        self.touch_slots.pop(self.current_slot, None)