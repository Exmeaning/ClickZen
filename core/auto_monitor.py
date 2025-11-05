import time
import threading
from PIL import Image
import numpy as np
import cv2
from PyQt5.QtCore import QObject, pyqtSignal
from datetime import datetime
from core.window_capture import WindowCapture
import json
import base64
from io import BytesIO
import os


class AutoMonitor(QObject):
    """自动化监控器 - 支持执行录制脚本"""

    # 信号
    match_found = pyqtSignal(dict)
    status_update = pyqtSignal(str)
    log_message = pyqtSignal(str)

    def __init__(self, adb_manager, controller):
        super().__init__()
        self.adb = adb_manager
        self.controller = controller
        self.monitoring = False
        self.monitor_thread = None
        self.monitor_configs = []
        self.check_interval = 0.5
        self.use_window_capture = True  # 强制使用窗口截图
        self.global_variables = {}  # 公共变量存储

    def add_monitor_config(self, config):
        """添加监控配置"""
        config['last_executed'] = 0
        self.monitor_configs.append(config)
        self.log_message.emit(f"添加监控任务: {config['name']}")
        return len(self.monitor_configs) - 1

    def start_monitoring(self):
        """开始监控"""
        if self.monitoring or not self.monitor_configs:
            return False

        self.monitoring = True
        self.monitor_thread = threading.Thread(target=self._monitor_loop, daemon=True)
        self.monitor_thread.start()
        self.status_update.emit("监控中...")
        self.log_message.emit("开始自动监控")
        return True

    def stop_monitoring(self):
        """停止监控"""
        self.monitoring = False
        # 停止正在播放的动作
        if self.controller.playing:
            self.controller.stop_playing()
        if self.monitor_thread:
            self.monitor_thread.join(timeout=2)
        # 清空所有公共变量
        self.global_variables.clear()
        self.log_message.emit("已清空所有变量")
        self.status_update.emit("已停止")

    def _monitor_loop(self):
        """监控循环 - 从Scrcpy窗口截图"""
        while self.monitoring:
            try:
                # 从Scrcpy窗口截图
                screenshot = WindowCapture.capture_window_safe("scrcpy", client_only=True)

                if not screenshot:
                    self.log_message.emit("无法获取Scrcpy窗口截图")
                    time.sleep(self.check_interval)
                    continue

                # 检查每个监控配置
                for i, config in enumerate(self.monitor_configs):
                    if not config.get('enabled', True):
                        continue

                    # 检查条件（公共变量）
                    if not self._check_conditions(config.get('conditions', [])):
                        continue

                    # 检查冷却时间
                    current_time = time.time()
                    if current_time - config.get('last_executed', 0) < config.get('cooldown', 5):
                        continue

                    # 如果有模板图片，进行模板匹配
                    if config.get('template'):
                        # 处理监控区域
                        region_img = self._get_region_image(screenshot, config.get('region'))
                        if not region_img:
                            continue

                        # 进行模板匹配
                        if not self._match_template(region_img, config['template'], config['threshold']):
                            continue
                    
                    # 如果没有模板但有条件，仅根据条件判断
                    # 条件已在前面检查过，这里直接执行
                    
                    self.log_message.emit(f"✅ 触发成功: {config['name']}")
                    self.match_found.emit({
                        'config': config,
                        'index': i,
                        'time': datetime.now().strftime("%H:%M:%S")
                    })

                    # 执行预设动作
                    self._execute_actions(config['actions'])
                    config['last_executed'] = current_time

                time.sleep(self.check_interval)

            except Exception as e:
                self.log_message.emit(f"监控错误: {str(e)}")
                time.sleep(1)

    def _get_region_image(self, screenshot, region):
        """获取区域图像（处理坐标转换）"""
        if not region:
            return screenshot

        try:
            x, y, w, h = region

            # 获取设备分辨率
            device_width, device_height = self.controller.get_device_resolution()
            window_width, window_height = screenshot.size

            # 判断方向
            window_aspect = window_width / window_height
            if window_aspect > 1.3:  # 横屏
                actual_width = max(device_width, device_height)
                actual_height = min(device_width, device_height)
            else:  # 竖屏
                actual_width = min(device_width, device_height)
                actual_height = max(device_width, device_height)

            # 转换坐标（设备坐标到窗口坐标）
            scale_x = window_width / actual_width
            scale_y = window_height / actual_height

            x = int(x * scale_x)
            y = int(y * scale_y)
            w = int(w * scale_x)
            h = int(h * scale_y)

            # 确保区域在范围内
            x = max(0, min(x, screenshot.width - 1))
            y = max(0, min(y, screenshot.height - 1))
            w = min(w, screenshot.width - x)
            h = min(h, screenshot.height - y)

            if w > 0 and h > 0:
                return screenshot.crop((x, y, x + w, y + h))
        except Exception as e:
            self.log_message.emit(f"处理区域失败: {str(e)}")

        return None

    def _match_template(self, screenshot, template, threshold):
        """模板匹配"""
        try:
            screenshot_cv = cv2.cvtColor(np.array(screenshot), cv2.COLOR_RGB2BGR)
            template_cv = cv2.cvtColor(np.array(template), cv2.COLOR_RGB2BGR)

            result = cv2.matchTemplate(screenshot_cv, template_cv, cv2.TM_CCOEFF_NORMED)
            min_val, max_val, min_loc, max_loc = cv2.minMaxLoc(result)

            return max_val >= threshold
        except Exception as e:
            self.log_message.emit(f"匹配错误: {str(e)}")
            return False

    def _execute_actions(self, actions):
        """执行动作序列 - 支持录制脚本和随机动作"""
        if not actions:
            return

        for action in actions:
            # 检查是否需要停止
            if not self.monitoring:
                break
                
            try:
                action_type = action.get('type')

                if action_type == 'random':
                    # 随机选择一个子动作执行
                    self._execute_random_action(action)

                elif action_type == 'set_variable':
                    # 设置或修改公共变量
                    var_name = action.get('variable', '')
                    var_value = action.get('value', 0)
                    operation = action.get('operation', 'set')
                    
                    if operation == 'set':
                        self.global_variables[var_name] = var_value
                        self.log_message.emit(f"  设置变量: {var_name} = {var_value}")
                    elif operation == 'add':
                        current = self.global_variables.get(var_name, 0)
                        self.global_variables[var_name] = current + var_value
                        self.log_message.emit(f"  变量增加: {var_name} += {var_value} (现在={self.global_variables[var_name]})")
                    elif operation == 'subtract':
                        current = self.global_variables.get(var_name, 0)
                        self.global_variables[var_name] = current - var_value
                        self.log_message.emit(f"  变量减少: {var_name} -= {var_value} (现在={self.global_variables[var_name]})")
                    elif operation == 'multiply':
                        current = self.global_variables.get(var_name, 1)
                        self.global_variables[var_name] = current * var_value
                        self.log_message.emit(f"  变量乘以: {var_name} *= {var_value} (现在={self.global_variables[var_name]})")
                    elif operation == 'divide':
                        current = self.global_variables.get(var_name, 1)
                        if var_value != 0:
                            self.global_variables[var_name] = current // var_value
                            self.log_message.emit(f"  变量除以: {var_name} /= {var_value} (现在={self.global_variables[var_name]})")

                elif action_type == 'click':
                    self.controller.click(action['x'], action['y'])
                    self.log_message.emit(f"  点击: ({action['x']}, {action['y']})")

                elif action_type == 'swipe':
                    self.controller.swipe(
                        action['x1'], action['y1'],
                        action['x2'], action['y2'],
                        action.get('duration', 300)
                    )
                    self.log_message.emit(
                        f"  滑动: ({action['x1']}, {action['y1']}) → ({action['x2']}, {action['y2']})")

                elif action_type == 'text':
                    self.controller.input_text(action['text'])
                    self.log_message.emit(f"  输入: {action['text']}")

                elif action_type == 'key':
                    self.adb.keyevent(action['keycode'])
                    self.log_message.emit(f"  按键: {action.get('key_name', action['keycode'])}")

                elif action_type == 'wait':
                    wait_time = action.get('duration', 1)
                    time.sleep(wait_time)
                    self.log_message.emit(f"  等待: {wait_time}秒")

                elif action_type == 'recording':
                    # 新增：执行录制脚本
                    self._execute_recording(action)

                time.sleep(action.get('delay', 0.1))

            except Exception as e:
                self.log_message.emit(f"  执行失败: {str(e)}")
    
    def _execute_random_action(self, random_action):
        """执行随机动作组"""
        import random
        
        sub_actions = random_action.get('sub_actions', [])
        if not sub_actions:
            return
            
        # 随机选择一个子动作
        selected = random.choice(sub_actions)
        selected_index = sub_actions.index(selected)
        
        self.log_message.emit(f"  随机选择动作 {selected_index + 1}/{len(sub_actions)}")
        
        # 执行选中的动作
        action_to_execute = selected.get('action', {})
        if action_to_execute:
            self._execute_actions([action_to_execute])
        
        # 设置对应的变量
        if 'set_variable' in selected:
            var_name = selected['set_variable'].get('variable', '')
            var_value = selected['set_variable'].get('value', 0)
            if var_name:
                self.global_variables[var_name] = var_value
                self.log_message.emit(f"    设置变量: {var_name} = {var_value}")
    
    def _check_conditions(self, conditions):
        """检查条件是否满足"""
        if not conditions:
            return True
            
        for condition in conditions:
            var_name = condition.get('variable', '')
            operator = condition.get('operator', '==')
            value = condition.get('value', 0)
            
            if var_name not in self.global_variables:
                continue
                
            current_value = self.global_variables[var_name]
            
            if operator == '==' and current_value != value:
                return False
            elif operator == '!=' and current_value == value:
                return False
            elif operator == '>' and current_value <= value:
                return False
            elif operator == '<' and current_value >= value:
                return False
            elif operator == '>=' and current_value < value:
                return False
            elif operator == '<=' and current_value > value:
                return False
                
        return True

    def _execute_recording(self, action):
        """执行录制脚本文件"""
        recording_file = action.get('recording_file', '')
        speed = action.get('speed', 1.0)
        use_random = action.get('use_random', False)

        if not recording_file or not os.path.exists(recording_file):
            self.log_message.emit(f"  录制文件不存在: {recording_file}")
            return

        try:
            # 加载录制文件
            with open(recording_file, 'r', encoding='utf-8') as f:
                recording_actions = json.load(f)

            self.log_message.emit(f"  执行录制脚本: {recording_file} ({len(recording_actions)}个动作)")

            # 执行录制的动作
            self.controller.play_recording(recording_actions, speed, use_random)

        except Exception as e:
            self.log_message.emit(f"  录制脚本执行失败: {str(e)}")

    def save_scheme(self, filename):
        """保存监控方案"""
        try:
            configs_to_save = []
            for config in self.monitor_configs:
                # 将图片转换为base64
                template = config['template']
                buffered = BytesIO()
                template.save(buffered, format="PNG")
                img_base64 = base64.b64encode(buffered.getvalue()).decode('utf-8')

                config_copy = config.copy()
                config_copy['template'] = img_base64
                config_copy.pop('last_executed', None)
                configs_to_save.append(config_copy)

            scheme = {
                'version': '1.0',
                'check_interval': self.check_interval,
                'configs': configs_to_save
            }

            with open(filename, 'w', encoding='utf-8') as f:
                json.dump(scheme, f, indent=2, ensure_ascii=False)

            self.log_message.emit(f"方案已保存: {filename}")
            return True
        except Exception as e:
            self.log_message.emit(f"保存失败: {str(e)}")
            return False

    def load_scheme(self, filename):
        """加载监控方案"""
        try:
            with open(filename, 'r', encoding='utf-8') as f:
                scheme = json.load(f)

            self.monitor_configs.clear()
            self.check_interval = scheme.get('check_interval', 0.5)

            for config in scheme.get('configs', []):
                # 将base64转换回图片
                img_data = base64.b64decode(config['template'])
                config['template'] = Image.open(BytesIO(img_data))
                config['last_executed'] = 0
                self.monitor_configs.append(config)

            self.log_message.emit(f"方案已加载: {filename}")
            return True
        except Exception as e:
            self.log_message.emit(f"加载失败: {str(e)}")
            return False

    def update_monitor_config(self, index, config):
        """更新监控配置"""
        if 0 <= index < len(self.monitor_configs):
            # 保留原有的last_executed时间
            last_executed = self.monitor_configs[index].get('last_executed', 0)
            self.monitor_configs[index] = config
            self.monitor_configs[index]['last_executed'] = last_executed
            self.log_message.emit(f"更新监控任务: {config.get('name', 'Unknown')}")
            return True
        return False

    def remove_monitor_config(self, index):
        """移除监控配置"""
        if 0 <= index < len(self.monitor_configs):
            name = self.monitor_configs[index]['name']
            del self.monitor_configs[index]
            self.log_message.emit(f"移除监控任务: {name}")
            return True
        return False

    def clear_monitor_configs(self):
        """清空所有监控配置"""
        self.monitor_configs.clear()
        self.log_message.emit("已清空所有监控任务")

    def get_monitor_config(self, index):
        """获取指定的监控配置"""
        if 0 <= index < len(self.monitor_configs):
            return self.monitor_configs[index].copy()
        return None

    def set_check_interval(self, interval):
        """设置检查间隔（秒）"""
        self.check_interval = max(0.05, min(interval, 10))  # 最小值改为0.05秒
        if self.check_interval < 0.1:
            self.log_message.emit(f"⚠️ 检查间隔设置为: {self.check_interval}秒 (过快可能影响性能)")
        else:
            self.log_message.emit(f"检查间隔设置为: {self.check_interval}秒")