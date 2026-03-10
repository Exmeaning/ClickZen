import time
import threading
from PIL import Image
import numpy as np
import cv2
from PyQt6.QtCore import QObject, pyqtSignal
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
        self.variable_server = None  # 变量服务器实例
        self.sync_variables = []  # 同步变量配置
        self.sync_interval = 1.0  # 同步间隔
        self.last_sync_time = 0  # 上次同步时间
        self.last_variable_values = {}  # 上次的变量值，用于检测变化

    def add_monitor_config(self, config):
        """添加监控配置"""
        config['last_executed'] = 0
        self.monitor_configs.append(config)
        self.log_message.emit(f"添加监控任务: {config['name']}")
        return len(self.monitor_configs) - 1

    def start_monitoring(self):
        """开始监控"""
        if self.monitoring:
            return False
            
        # 检查是否有监控配置
        if not self.monitor_configs:
            self.log_message.emit("警告: 没有监控任务配置")
            return False

        self.monitoring = True
        
        self.monitor_thread = threading.Thread(target=self._monitor_loop, daemon=True)
        self.monitor_thread.start()
        self.status_update.emit("监控中...")
        self.log_message.emit("开始自动监控")
        
        # 记录网络同步状态
        if self.variable_server:
            self.log_message.emit("✅ 变量服务器已运行")
        if self.sync_variables:
            self.log_message.emit(f"已配置 {len(self.sync_variables)} 个同步变量")
            for var in self.sync_variables:
                direction_map = {'both': '↔', 'send': '→', 'receive': '←'}
                arrow = direction_map.get(var.get('direction', 'both'), '↔')
                self.log_message.emit(f"  {arrow} {var.get('name')}")
        
        return True

    def stop_monitoring(self):
        """停止监控"""
        self.monitoring = False
        # 停止正在播放的动作
        if self.controller.playing:
            self.controller.stop_playing()
        if self.monitor_thread:
            self.monitor_thread.join(timeout=2)
        
        # 注意：不断开网络连接，因为可能需要继续同步变量
        # 网络连接由高级监控对话框管理
        
        # 清空所有公共变量
        self.global_variables.clear()
        self.log_message.emit("已清空所有变量")
        self.status_update.emit("已停止")

    def _monitor_loop(self):
        """监控循环 - 从Scrcpy窗口截图"""
        while self.monitoring:
            try:
                # 处理变量同步
                self._sync_network_variables()
                
                # 从控制器获取截图（支持Scrcpy和模拟器）
                screenshot = self.controller.screenshot()

                if not screenshot:
                    self.log_message.emit("无法获取屏幕截图(Scrcpy/模拟器)")
                    time.sleep(self.check_interval)
                    continue

                # 检查每个监控配置
                for i, config in enumerate(self.monitor_configs):
                    if not config.get('enabled', True):
                        continue

                    # 检查冷却时间
                    current_time = time.time()
                    if current_time - config.get('last_executed', 0) < config.get('cooldown', 5):
                        continue

                    # 获取任务模式
                    task_mode = config.get('task_mode')
                    
                    if task_mode == 'IF':
                        # IF模式：先检查统一条件，通过后再检查每个条件-动作对
                        unified_conditions = config.get('unified_conditions', [])
                        if unified_conditions:
                            if not self._check_unified_conditions(screenshot, unified_conditions, config.get('condition_logic', 'AND (全部满足)'), log_details=False):
                                continue
                        
                        self._execute_if_mode(config, screenshot, current_time, i)
                    elif task_mode == 'RANDOM':
                        # RANDOM模式：检查触发条件，然后随机执行动作序列
                        # 检查统一条件
                        unified_conditions = config.get('unified_conditions', [])
                        if unified_conditions:
                            if not self._check_unified_conditions(screenshot, unified_conditions, config.get('condition_logic', 'AND (全部满足)'), log_details=False):
                                continue
                        
                        self.log_message.emit(f"✅ RANDOM模式触发: {config['name']}")
                        self.match_found.emit({
                            'config': config,
                            'index': i,
                            'time': datetime.now().strftime("%H:%M:%S")
                        })
                        
                        # 随机选择并执行一个动作序列
                        self._execute_random_mode(config)
                        config['last_executed'] = current_time
                    else:
                        # 传统模式（兼容旧版本）
                        # 检查统一条件
                        unified_conditions = config.get('unified_conditions', [])
                        if unified_conditions:
                            if not self._check_unified_conditions(screenshot, unified_conditions, config.get('condition_logic', 'AND (全部满足)'), log_details=False):
                                continue
                        else:
                            # 兼容旧版本：没有统一条件时，尝试使用旧格式
                            # 检查旧版变量条件
                            if not self._check_conditions(config.get('conditions', [])):
                                continue
                            
                            # 检查旧版模板匹配
                            if config.get('template'):
                                region_img = self._get_region_image(screenshot, config.get('region'))
                                if not region_img:
                                    continue
                                if not self._match_template(region_img, config['template'], config.get('threshold', 0.85)):
                                    continue
                        
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
                import traceback
                self.log_message.emit(f"监控错误: {str(e)}")
                # 输出详细的错误信息到控制台，方便调试
                print(f"监控循环错误详情:")
                print(traceback.format_exc())
                time.sleep(1)
    
    def _sync_network_variables(self):
        """同步网络变量（双向）"""
        # 如果没有配置同步变量，直接返回
        if not self.sync_variables:
            return
            
        current_time = time.time()
        
        # 检查同步间隔
        if current_time - self.last_sync_time < self.sync_interval:
            return
        
        self.last_sync_time = current_time
        
        # 检查是否有服务器运行
        if not self.variable_server:
            return
        
        # 处理每个同步变量
        for var_config in self.sync_variables:
            var_name = var_config.get('name')
            direction = var_config.get('direction', 'both')
            
            if not var_name:
                continue
            
            # 发送本地变量（send 或 both）
            if direction in ['send', 'both']:
                if var_name in self.global_variables:
                    current_value = self.global_variables[var_name]
                    last_value = self.last_variable_values.get(var_name)
                    
                    # 检测变量是否改变
                    if current_value != last_value:
                        self.last_variable_values[var_name] = current_value
                        
                        # 广播给所有客户端
                        if self.variable_server:
                            self.variable_server.set_variable(var_name, current_value)
                            self.log_message.emit(f"📡 广播变量: {var_name} = {current_value}")
            
            # 注意：接收变量更新是通过variable_server的回调函数处理的
            # 当客户端发送set_variable请求时，服务器会触发variable_updated信号
    

    
    def _execute_if_mode(self, config, screenshot, current_time, config_index):
        """执行IF模式"""
        if_pairs = config.get('if_pairs', [])
        any_condition_met = False  # 记录是否有任何条件满足
        
        for pair_index, pair in enumerate(if_pairs):
            conditions = pair.get('conditions', [])
            logic = pair.get('logic', 'AND (全部满足)')
            
            # 检查这个条件组（不输出详细日志）
            if self._check_if_conditions(screenshot, conditions, logic, log_details=False):
                # 条件满足时才输出日志
                self.log_message.emit(f"✅ IF条件{pair_index + 1}满足: {config['name']}")
                
                # 执行对应的动作
                actions = pair.get('actions', [])
                if actions:
                    self.log_message.emit(f"  执行条件{pair_index + 1}的动作序列...")
                    self._execute_actions(actions)
                
                # 触发事件
                self.match_found.emit({
                    'config': config,
                    'index': config_index,
                    'pair_index': pair_index,
                    'time': datetime.now().strftime("%H:%M:%S")
                })
                
                any_condition_met = True
                # 继续检查其他条件，不break
        
        # 只要有任何条件满足，就更新执行时间
        if any_condition_met:
            config['last_executed'] = current_time
    
    def _execute_random_mode(self, config):
        """执行RANDOM模式"""
        import random
        
        sequences = config.get('random_sequences', [])
        if not sequences:
            return
        
        # 随机选择一个序列
        selected = random.choice(sequences)
        selected_index = sequences.index(selected)
        
        self.log_message.emit(f"  随机选择序列 {selected_index + 1}/{len(sequences)}: {selected.get('name', '未命名')}")
        
        # 执行选中的动作序列
        actions = selected.get('actions', [])
        if actions:
            self._execute_actions(actions)
    
    def _evaluate_single_condition(self, condition, screenshot):
        """评估单个条件（统一的条件评估方法）
        
        Args:
            condition: 条件配置字典
            screenshot: 当前截图
            
        Returns:
            bool: 条件是否满足
        """
        condition_type = condition.get('type')
        
        if condition_type == 'variable':
            return self._evaluate_variable_condition(condition)
        elif condition_type == 'image':
            return self._evaluate_image_condition(condition, screenshot)
        return False
    
    def _evaluate_variable_condition(self, condition):
        """评估变量条件"""
        var_name = condition.get('variable', '')
        operator = condition.get('operator', '==')
        value = condition.get('value', 0)
        
        if var_name not in self.global_variables:
            return False
        
        current_value = self.global_variables[var_name]
        
        ops = {
            '==': lambda a, b: a == b,
            '!=': lambda a, b: a != b,
            '>':  lambda a, b: a > b,
            '<':  lambda a, b: a < b,
            '>=': lambda a, b: a >= b,
            '<=': lambda a, b: a <= b,
        }
        op_func = ops.get(operator)
        return op_func(current_value, value) if op_func else False
    
    def _evaluate_image_condition(self, condition, screenshot):
        """评估图像条件"""
        region_img = self._get_region_image(screenshot, condition.get('region'))
        if not region_img:
            match_result = False
        else:
            template = condition.get('template')
            threshold = condition.get('threshold', 0.85)
            
            if template:
                # 模板尺寸校验
                tw, th = template.size
                sw, sh = region_img.size
                if tw > sw or th > sh:
                    self.log_message.emit(f"  ⚠️ 模板({tw}x{th})大于搜索区域({sw}x{sh})，跳过")
                    match_result = False
                else:
                    match_result = self._match_template(region_img, template, threshold)
            else:
                match_result = False
        
        expect_exist = condition.get('expect_exist', True)
        return match_result if expect_exist else not match_result
    
    def _evaluate_conditions(self, screenshot, conditions, logic, log_details=False):
        """统一的条件组评估方法（替代 _check_if_conditions 和 _check_unified_conditions）
        
        Args:
            screenshot: 当前截图
            conditions: 条件列表
            logic: 逻辑关系字符串 ('AND ...', 'OR ...', 'NOT ...')
            log_details: 是否输出详细日志
            
        Returns:
            bool: 条件组是否满足
        """
        if not conditions:
            return False
        
        # 单条件优化
        if len(conditions) == 1:
            result = self._evaluate_single_condition(conditions[0], screenshot)
            if result and log_details:
                cond = conditions[0]
                if cond.get('type') == 'variable':
                    self.log_message.emit(f"  [变量] {cond.get('variable')} {cond.get('operator')} {cond.get('value')} → 满足")
                elif cond.get('type') == 'image':
                    self.log_message.emit(f"  [图像] 检测到 → 满足")
            return result
        
        # 多条件评估
        results = [self._evaluate_single_condition(c, screenshot) for c in conditions]
        
        # 根据逻辑判断
        if "AND" in logic:
            final_result = all(results)
            if final_result and log_details:
                self.log_message.emit(f"  AND逻辑: {len(results)}/{len(results)} 满足 → 通过")
        elif "OR" in logic:
            final_result = any(results)
            if final_result and log_details:
                satisfied = sum(results)
                self.log_message.emit(f"  OR逻辑: {satisfied}/{len(results)} 满足 → 通过")
        elif "NOT" in logic:
            final_result = not any(results)
            if final_result and log_details:
                not_satisfied = sum(1 for r in results if not r)
                self.log_message.emit(f"  NOT逻辑: {not_satisfied}/{len(results)} 不满足 → 通过")
        else:
            final_result = False
        
        return final_result

    def _check_if_conditions(self, screenshot, conditions, logic, log_details=False):
        """检查IF模式的条件组（委托给统一方法）"""
        return self._evaluate_conditions(screenshot, conditions, logic, log_details)
    
    def _check_unified_conditions(self, screenshot, unified_conditions, logic, log_details=True):
        """检查统一条件（委托给统一方法）"""
        if not unified_conditions:
            return True
        return self._evaluate_conditions(screenshot, unified_conditions, logic, log_details)

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

                if action_type == 'set_variable':
                    # 设置或修改公共变量
                    var_name = action.get('variable', '')
                    operation = action.get('operation', 'set')
                    if self.variable_server and self.variable_server.running:
                        self.variable_server.set_variable(var_name, self.global_variables.get(var_name))
                    if operation == 'from_variable':
                        # 基于另一个变量的操作
                        source_var = action.get('source_variable', '')
                        calc_op = action.get('calc_operator', '+')
                        calc_value = action.get('calc_value', 0)
                        
                        if source_var in self.global_variables:
                            source_value = self.global_variables[source_var]
                            
                            # 执行运算（结果转为整数）
                            if calc_op == '+':
                                result = int(source_value + calc_value)
                            elif calc_op == '-':
                                result = int(source_value - calc_value)
                            elif calc_op == '*':
                                result = int(source_value * calc_value)
                            elif calc_op == '//':
                                if calc_value != 0:
                                    result = int(source_value // calc_value)
                                else:
                                    self.log_message.emit(f"  错误: 除数为0")
                                    continue
                            else:
                                result = int(source_value)
                            
                            self.global_variables[var_name] = result
                            self.log_message.emit(f"  变量计算: {var_name} = {source_var}({source_value}) {calc_op} {calc_value} = {result}")
                        else:
                            self.log_message.emit(f"  错误: 源变量 {source_var} 不存在")
                    else:
                        # 原有的操作
                        var_value = action.get('value', 0)
                        
                        if operation == 'set':
                            self.global_variables[var_name] = int(var_value)
                            self.log_message.emit(f"  设置变量: {var_name} = {var_value}")
                        elif operation == 'add':
                            current = self.global_variables.get(var_name, 0)
                            self.global_variables[var_name] = int(current + var_value)
                            self.log_message.emit(f"  变量增加: {var_name} += {var_value} (现在={self.global_variables[var_name]})")
                        elif operation == 'subtract':
                            current = self.global_variables.get(var_name, 0)
                            self.global_variables[var_name] = int(current - var_value)
                            self.log_message.emit(f"  变量减少: {var_name} -= {var_value} (现在={self.global_variables[var_name]})")
                        elif operation == 'multiply':
                            current = self.global_variables.get(var_name, 1)
                            self.global_variables[var_name] = int(current * var_value)
                            self.log_message.emit(f"  变量乘以: {var_name} *= {var_value} (现在={self.global_variables[var_name]})")
                        elif operation == 'divide':
                            current = self.global_variables.get(var_name, 1)
                            if var_value != 0:
                                self.global_variables[var_name] = int(current // var_value)
                                self.log_message.emit(f"  变量除以: {var_name} /= {var_value} (现在={self.global_variables[var_name]})")
                
                elif action_type == 'adb_command':
                    # 执行ADB命令
                    command = action.get('command', '')
                    if command:
                        result = self.adb.shell(command)
                        if result:
                            self.log_message.emit(f"  执行ADB: {command[:50]}")
                        else:
                            self.log_message.emit(f"  ADB命令失败: {command[:50]}")

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
                config_copy = config.copy()
                
                # 将图片转换为base64（如果存在）
                template = config.get('template')
                if template is not None:
                    buffered = BytesIO()
                    template.save(buffered, format="PNG")
                    img_base64 = base64.b64encode(buffered.getvalue()).decode('utf-8')
                    config_copy['template'] = img_base64
                else:
                    # 没有模板图片时保存为null
                    config_copy['template'] = None
                
                # 处理统一条件中的图片
                if 'unified_conditions' in config_copy:
                    unified_conditions_copy = []
                    for condition in config_copy['unified_conditions']:
                        cond_copy = condition.copy()
                        if condition.get('type') == 'image' and 'template' in condition:
                            template = condition.get('template')
                            if template is not None:
                                buffered = BytesIO()
                                template.save(buffered, format="PNG")
                                img_base64 = base64.b64encode(buffered.getvalue()).decode('utf-8')
                                cond_copy['template'] = img_base64
                            else:
                                cond_copy['template'] = None
                        unified_conditions_copy.append(cond_copy)
                    config_copy['unified_conditions'] = unified_conditions_copy
                
                # 处理IF模式的条件-动作对中的图片
                if 'if_pairs' in config_copy:
                    if_pairs_copy = []
                    for pair in config_copy['if_pairs']:
                        pair_copy = pair.copy()
                        if 'conditions' in pair_copy:
                            conditions_copy = []
                            for condition in pair_copy['conditions']:
                                cond_copy = condition.copy()
                                if condition.get('type') == 'image' and 'template' in condition:
                                    template = condition.get('template')
                                    if template is not None:
                                        buffered = BytesIO()
                                        template.save(buffered, format="PNG")
                                        img_base64 = base64.b64encode(buffered.getvalue()).decode('utf-8')
                                        cond_copy['template'] = img_base64
                                    else:
                                        cond_copy['template'] = None
                                conditions_copy.append(cond_copy)
                            pair_copy['conditions'] = conditions_copy
                        if_pairs_copy.append(pair_copy)
                    config_copy['if_pairs'] = if_pairs_copy
                
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
                # 将base64转换回图片（如果存在）
                template_data = config.get('template')
                if template_data is not None and template_data != '':
                    img_data = base64.b64decode(template_data)
                    config['template'] = Image.open(BytesIO(img_data))
                else:
                    # 没有模板图片
                    config['template'] = None
                
                # 处理统一条件中的图片
                if 'unified_conditions' in config:
                    for condition in config['unified_conditions']:
                        if condition.get('type') == 'image' and 'template' in condition:
                            template_data = condition.get('template')
                            if template_data is not None and template_data != '':
                                img_data = base64.b64decode(template_data)
                                condition['template'] = Image.open(BytesIO(img_data))
                            else:
                                condition['template'] = None
                
                # 处理IF模式的条件-动作对中的图片
                if 'if_pairs' in config:
                    for pair in config['if_pairs']:
                        if 'conditions' in pair:
                            for condition in pair['conditions']:
                                if condition.get('type') == 'image' and 'template' in condition:
                                    template_data = condition.get('template')
                                    if template_data is not None and template_data != '':
                                        img_data = base64.b64decode(template_data)
                                        condition['template'] = Image.open(BytesIO(img_data))
                                    else:
                                        condition['template'] = None
                
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