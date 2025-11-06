from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *
from PIL import Image
import numpy as np
from core.window_capture import WindowCapture
import json
import time
import os


class MonitorTaskDialog(QDialog):
    """监控任务配置对话框"""

    def __init__(self, controller, parent=None, task_config=None):
        super().__init__(parent)
        self.controller = controller
        self.task_config = task_config or {}
        self.template_image = None
        self.actions = self.task_config.get('actions', [])
        self.region = self.task_config.get('region', None)
        self.main_window = parent  # 保存主窗口引用

        self.initUI()
        self.load_config()

    def initUI(self):
        """初始化UI"""
        self.setWindowTitle("监控任务配置")
        self.setMinimumWidth(600)
        self.setMinimumHeight(700)  # 增加高度
        self.resize(650, 750)  # 设置初始大小

        # 创建滚动区域
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll_widget = QWidget()
        layout = QVBoxLayout(scroll_widget)

        # 基本信息
        info_group = QGroupBox("基本信息")
        info_layout = QFormLayout()

        self.name_input = QLineEdit()
        self.name_input.setPlaceholderText("输入任务名称...")
        info_layout.addRow("任务名称:", self.name_input)

        self.enabled_check = QCheckBox("启用任务")
        self.enabled_check.setChecked(True)
        info_layout.addRow("", self.enabled_check)

        info_group.setLayout(info_layout)

        # 监控区域
        region_group = QGroupBox("监控区域")
        region_layout = QVBoxLayout()

        region_button_layout = QHBoxLayout()
        self.select_region_btn = QPushButton("选择区域")
        self.select_region_btn.clicked.connect(self.select_region)
        self.clear_region_btn = QPushButton("全屏")
        self.clear_region_btn.clicked.connect(self.clear_region)
        region_button_layout.addWidget(self.select_region_btn)
        region_button_layout.addWidget(self.clear_region_btn)

        self.region_label = QLabel("监控全屏")

        region_layout.addLayout(region_button_layout)
        region_layout.addWidget(self.region_label)
        region_group.setLayout(region_layout)

        # 模板图片
        template_group = QGroupBox("模板图片")
        template_layout = QVBoxLayout()

        template_button_layout = QHBoxLayout()
        self.select_template_btn = QPushButton("选择图片")
        self.select_template_btn.clicked.connect(self.select_template)
        self.capture_template_btn = QPushButton("截取模板")
        self.capture_template_btn.clicked.connect(self.capture_template)
        template_button_layout.addWidget(self.select_template_btn)
        template_button_layout.addWidget(self.capture_template_btn)

        self.template_label = QLabel("未选择模板")
        self.template_label.setMinimumHeight(100)
        self.template_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.template_label.setStyleSheet("border: 1px solid #ccc;")

        template_layout.addLayout(template_button_layout)
        template_layout.addWidget(self.template_label)
        template_group.setLayout(template_layout)

        # 匹配参数
        param_group = QGroupBox("匹配参数")
        param_layout = QFormLayout()

        self.threshold_spin = QDoubleSpinBox()
        self.threshold_spin.setRange(0.5, 1.0)
        self.threshold_spin.setValue(0.85)
        self.threshold_spin.setSingleStep(0.01)
        param_layout.addRow("匹配阈值:", self.threshold_spin)

        self.cooldown_spin = QSpinBox()
        self.cooldown_spin.setRange(0, 300)
        self.cooldown_spin.setValue(5)
        self.cooldown_spin.setSuffix(" 秒")
        param_layout.addRow("冷却时间:", self.cooldown_spin)

        param_group.setLayout(param_layout)

        # 执行动作
        action_group = QGroupBox("执行动作")
        action_layout = QVBoxLayout()

        self.action_list = QListWidget()
        self.action_list.setMaximumHeight(150)

        action_button_layout = QHBoxLayout()
        self.add_action_btn = QPushButton("添加动作")
        self.add_action_btn.clicked.connect(self.add_action)
        self.edit_action_btn = QPushButton("编辑")
        self.edit_action_btn.clicked.connect(self.edit_action)
        self.remove_action_btn = QPushButton("删除")
        self.remove_action_btn.clicked.connect(self.remove_action)
        action_button_layout.addWidget(self.add_action_btn)
        action_button_layout.addWidget(self.edit_action_btn)
        action_button_layout.addWidget(self.remove_action_btn)

        action_layout.addWidget(self.action_list)
        action_layout.addLayout(action_button_layout)
        action_group.setLayout(action_layout)

        # 条件设置（高级功能）
        condition_group = QGroupBox("执行条件")
        condition_layout = QVBoxLayout()

        # 启用条件判断复选框
        self.enable_condition_check = QCheckBox("启用条件判断功能（高级）")
        self.enable_condition_check.toggled.connect(self.toggle_condition_panel)
        condition_layout.addWidget(self.enable_condition_check)

        # 条件面板（默认隐藏）
        self.condition_panel = QWidget()
        condition_panel_layout = QVBoxLayout(self.condition_panel)
        
        self.condition_list = QListWidget()
        self.condition_list.setMaximumHeight(80)

        condition_button_layout = QHBoxLayout()
        self.add_condition_btn = QPushButton("添加条件")
        self.add_condition_btn.clicked.connect(self.add_condition)
        self.remove_condition_btn = QPushButton("删除")
        self.remove_condition_btn.clicked.connect(self.remove_condition)
        condition_button_layout.addWidget(self.add_condition_btn)
        condition_button_layout.addWidget(self.remove_condition_btn)

        condition_panel_layout.addWidget(QLabel("基于公共变量的条件判断:"))
        condition_panel_layout.addWidget(self.condition_list)
        condition_panel_layout.addLayout(condition_button_layout)
        
        self.condition_panel.setVisible(False)  # 默认隐藏
        condition_layout.addWidget(self.condition_panel)
        condition_group.setLayout(condition_layout)

        # 按钮
        button_box = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        button_box.accepted.connect(self.accept)
        button_box.rejected.connect(self.reject)

        # 添加到主布局
        layout.addWidget(info_group)
        layout.addWidget(condition_group)
        layout.addWidget(region_group)
        layout.addWidget(template_group)
        layout.addWidget(param_group)
        layout.addWidget(action_group)
        layout.addStretch()
        
        scroll.setWidget(scroll_widget)
        
        # 主窗口布局
        main_layout = QVBoxLayout(self)
        main_layout.addWidget(scroll)
        main_layout.addWidget(button_box)

    def load_config(self):
        """加载配置"""
        if self.task_config:
            self.name_input.setText(self.task_config.get('name', ''))
            self.enabled_check.setChecked(self.task_config.get('enabled', True))
            self.threshold_spin.setValue(self.task_config.get('threshold', 0.85))
            self.cooldown_spin.setValue(self.task_config.get('cooldown', 5))

            # 安全处理region
            if 'region' in self.task_config and self.task_config['region']:
                self.region = self.task_config['region']
                if self.region and len(self.region) == 4:
                    x, y, w, h = self.region
                    self.region_label.setText(f"起始: ({x}, {y}) → 结束: ({x + w}, {y + h})")

            # 安全处理template
            if 'template' in self.task_config and self.task_config['template']:
                self.template_image = self.task_config['template']
                self.show_template_preview()

            self.actions = self.task_config.get('actions', [])
            self.refresh_action_list()
            
            # 加载条件
            self.conditions = self.task_config.get('conditions', [])
            if self.conditions:
                self.enable_condition_check.setChecked(True)
                self.condition_panel.setVisible(True)
            self.refresh_condition_list()

    def toggle_condition_panel(self, checked):
        """切换条件面板显示"""
        self.condition_panel.setVisible(checked)
        if not checked:
            # 清空条件列表
            self.conditions = []
            self.refresh_condition_list()
    
    def refresh_condition_list(self):
        """刷新条件列表"""
        self.condition_list.clear()
        for condition in getattr(self, 'conditions', []):
            var = condition.get('variable', '')
            op = condition.get('operator', '==')
            val = condition.get('value', 0)
            self.condition_list.addItem(f"{var} {op} {val}")
    
    def add_condition(self):
        """添加条件"""
        dialog = ConditionDialog(self)
        if dialog.exec():
            condition = dialog.get_condition()
            if not hasattr(self, 'conditions'):
                self.conditions = []
            self.conditions.append(condition)
            self.refresh_condition_list()
    
    def remove_condition(self):
        """删除条件"""
        current = self.condition_list.currentRow()
        if current >= 0 and hasattr(self, 'conditions'):
            del self.conditions[current]
            self.refresh_condition_list()

    def select_region(self):
        """选择监控区域"""
        dialog = RegionInputDialog(self, self.region)
        if dialog.exec():
            self.region = dialog.get_region()
            x, y, w, h = self.region
            self.region_label.setText(f"起始: ({x}, {y}) → 结束: ({x + w}, {y + h})")

    def clear_region(self):
        """清除区域（全屏监控）"""
        self.region = None
        self.region_label.setText("监控全屏")

    def select_template(self):
        """选择模板图片"""
        filename, _ = QFileDialog.getOpenFileName(
            self, "选择模板图片", "", "图片文件 (*.png *.jpg *.jpeg)"
        )
        if filename:
            self.template_image = Image.open(filename)
            self.show_template_preview()

    def capture_template(self):
        """截取模板 - 从Scrcpy窗口截取"""
        if self.main_window and hasattr(self.main_window, 'log'):
            self.main_window.log("正在从Scrcpy窗口截取模板...")

        # 使用"scrcpy"作为参数，会自动查找Scrcpy窗口
        screenshot = WindowCapture.capture_window_safe("scrcpy", client_only=True)

        if not screenshot:
            QMessageBox.warning(self, "警告", "无法找到Scrcpy窗口，请确保Scrcpy正在运行")
            return

        # 处理区域选择
        if self.region:
            x, y, w, h = self.region

            # 获取窗口和设备的尺寸信息
            window_width, window_height = screenshot.size
            device_width, device_height = self.controller.get_device_resolution()

            # 判断当前显示方向
            window_aspect = window_width / window_height

            if window_aspect > 1.3:  # 横屏显示
                actual_device_width = max(device_width, device_height)
                actual_device_height = min(device_width, device_height)
                scale_x = window_width / actual_device_width
                scale_y = window_height / actual_device_height
            else:  # 竖屏显示
                actual_device_width = min(device_width, device_height)
                actual_device_height = max(device_width, device_height)
                scale_x = window_width / actual_device_width
                scale_y = window_height / actual_device_height

            # 转换坐标
            window_x = int(x * scale_x)
            window_y = int(y * scale_y)
            window_w = int(w * scale_x)
            window_h = int(h * scale_y)

            # 确保坐标在有效范围内
            window_x = max(0, min(window_x, window_width - 1))
            window_y = max(0, min(window_y, window_height - 1))
            window_w = min(window_w, window_width - window_x)
            window_h = min(window_h, window_height - window_y)

            if window_w > 0 and window_h > 0:
                self.template_image = screenshot.crop((window_x, window_y,
                                                       window_x + window_w,
                                                       window_y + window_h))
                self.region_label.setText(f"起始: ({x}, {y}) → 结束: ({x + w}, {y + h})")
            else:
                QMessageBox.warning(self, "警告", "无效的截取区域")
                return
        else:
            # 弹出区域选择对话框
            dialog = RegionInputDialog(self)
            if dialog.exec():
                self.region = dialog.get_region()
                # 递归调用以处理区域
                self.capture_template()
                return

        # 显示预览
        self.show_template_preview()

    def show_template_preview(self):
        """显示模板预览"""
        if self.template_image:
            try:
                # 确保图像是RGB模式
                if self.template_image.mode != 'RGB':
                    self.template_image = self.template_image.convert('RGB')

                # 转换为QPixmap
                import numpy as np
                img_array = np.array(self.template_image)
                height, width = img_array.shape[:2]

                # 确保是3通道RGB
                if len(img_array.shape) == 2:  # 灰度图
                    img_array = np.stack([img_array] * 3, axis=-1)
                elif len(img_array.shape) == 3 and img_array.shape[2] == 4:  # RGBA
                    img_array = img_array[:, :, :3]

                # 创建QImage
                bytes_per_line = 3 * width
                if not img_array.flags['C_CONTIGUOUS']:
                    img_array = np.ascontiguousarray(img_array)

                qimg = QImage(
                    img_array.data,
                    width,
                    height,
                    bytes_per_line,
                    QImage.Format.Format_RGB888
                )

                # 转换为QPixmap并缩放
                pixmap = QPixmap.fromImage(qimg)
                max_width = 300
                max_height = 150
                if pixmap.width() > max_width or pixmap.height() > max_height:
                    pixmap = pixmap.scaled(
                        max_width,
                        max_height,
                        Qt.AspectRatioMode.KeepAspectRatio,
                        Qt.TransformationMode.SmoothTransformation
                    )

                self.template_label.setPixmap(pixmap)

            except Exception as e:
                self.template_label.setText(f"预览失败: {str(e)}")

    def add_action(self):
        """添加动作"""
        try:
            dialog = ActionEditDialog(self.controller, self)
            if dialog.exec():
                action = dialog.get_action()
                if action:
                    self.actions.append(action)
                    self.refresh_action_list()
        except Exception as e:
            QMessageBox.critical(self, "错误", f"添加动作失败: {str(e)}")

    def edit_action(self):
        """编辑动作"""
        current = self.action_list.currentRow()
        if current >= 0:
            dialog = ActionEditDialog(self.controller, self, self.actions[current])
            if dialog.exec():
                self.actions[current] = dialog.get_action()
                self.refresh_action_list()

    def remove_action(self):
        """删除动作"""
        current = self.action_list.currentRow()
        if current >= 0:
            del self.actions[current]
            self.refresh_action_list()

    def refresh_action_list(self):
        """刷新动作列表"""
        self.action_list.clear()
        for action in self.actions:
            text = self.format_action_text(action)
            self.action_list.addItem(text)

    def format_action_text(self, action):
        """格式化动作文本"""
        action_type = action.get('type')
        if action_type == 'click':
            return f"点击 ({action['x']}, {action['y']})"
        elif action_type == 'swipe':
            return f"滑动 ({action['x1']}, {action['y1']}) → ({action['x2']}, {action['y2']})"
        elif action_type == 'text':
            return f"输入文本: {action['text']}"
        elif action_type == 'key':
            return f"按键: {action.get('key_name', action['keycode'])}"
        elif action_type == 'wait':
            return f"等待 {action.get('duration', 1)} 秒"
        elif action_type == 'recording':
            filename = os.path.basename(action.get('recording_file', ''))
            return f"执行录制: {filename}"
        elif action_type == 'random':
            count = len(action.get('sub_actions', []))
            return f"随机执行 ({count}个动作之一)"
        elif action_type == 'set_variable':
            variable = action.get('variable', '')
            operation = action.get('operation', 'set')
            
            if operation == 'from_variable':
                # 基于变量的操作
                source_var = action.get('source_variable', '')
                calc_op = action.get('calc_operator', '+')
                calc_value = action.get('calc_value', 0)
                return f"变量 {variable} = {source_var} {calc_op} {calc_value}"
            else:
                # 普通操作
                value = action.get('value', 0)
                op_symbols = {
                    'set': '=',
                    'add': '+=',
                    'subtract': '-=',
                    'multiply': '*=',
                    'divide': '/='
                }
                op_symbol = op_symbols.get(operation, '=')
                return f"变量 {variable} {op_symbol} {value}"
        elif action_type == 'adb_command':
            command = action.get('command', '')
            # 截断长命令显示
            if len(command) > 30:
                command = command[:30] + '...'
            return f"ADB: {command}"
        return "未知动作"

    def get_config(self):
        """获取配置"""
        # 如果没有填写名称，自动生成
        task_name = self.name_input.text()
        if not task_name:
            from datetime import datetime
            task_name = f"监控任务_{datetime.now().strftime('%H%M%S')}"
            self.name_input.setText(task_name)

        # 如果没有模板图片但有条件判断，允许创建（纯条件触发）
        conditions = getattr(self, 'conditions', [])
        if not self.template_image and not conditions:
            QMessageBox.warning(self, "警告", "请选择模板图片或添加条件判断")
            return None

        return {
            'name': task_name,
            'enabled': self.enabled_check.isChecked(),
            'region': self.region,
            'template': self.template_image,
            'threshold': self.threshold_spin.value(),
            'cooldown': self.cooldown_spin.value(),
            'actions': self.actions,
            'conditions': getattr(self, 'conditions', []) if self.enable_condition_check.isChecked() else []
        }


class RegionInputDialog(QDialog):
    """区域输入对话框"""

    def __init__(self, parent=None, initial_region=None):
        super().__init__(parent)
        self.initial_region = initial_region
        self.current_device_coords = (0, 0)
        self.pipette_mode = False
        self.pipette_target = 'start'
        self.last_click_time = 0
        self.initUI()
        if initial_region:
            self.load_region(initial_region)
        # 启动坐标追踪
        self.setup_coordinate_tracker()

    def setup_coordinate_tracker(self):
        """设置坐标追踪器"""
        self.coord_timer = QTimer(self)
        self.coord_timer.timeout.connect(self.update_mouse_coordinates)
        self.coord_timer.start(50)  # 每50ms更新一次
        
        # 滴管模式
        self.pipette_mode = False
        self.pipette_target = 'start'
        self.last_click_time = 0

    def update_mouse_coordinates(self):
        """更新鼠标坐标显示"""
        try:
            import win32gui

            # 获取鼠标位置
            cursor_pos = win32gui.GetCursorPos()
            self.screen_coord_label.setText(f"屏幕: ({cursor_pos[0]}, {cursor_pos[1]})")

            # 使用WindowCapture查找Scrcpy窗口
            from core.window_capture import WindowCapture
            hwnd = WindowCapture.find_scrcpy_window()

            if hwnd:
                # 获取窗口客户区
                rect = win32gui.GetClientRect(hwnd)
                point = win32gui.ClientToScreen(hwnd, (0, 0))
                client_rect = (
                    point[0], point[1],
                    point[0] + rect[2], point[1] + rect[3]
                )

                # 检查鼠标是否在窗口内
                if (client_rect[0] <= cursor_pos[0] <= client_rect[2] and
                        client_rect[1] <= cursor_pos[1] <= client_rect[3]):

                    # 计算相对坐标
                    rel_x = cursor_pos[0] - client_rect[0]
                    rel_y = cursor_pos[1] - client_rect[1]

                    # 窗口大小
                    window_width = client_rect[2] - client_rect[0]
                    window_height = client_rect[3] - client_rect[1]

                    # 获取设备分辨率（需要传入controller）
                    # 尝试从父窗口获取controller
                    controller = None
                    p = self.parent()
                    while p:
                        if hasattr(p, 'controller'):
                            controller = p.controller
                            break
                        p = p.parent() if hasattr(p, 'parent') and callable(p.parent) else None
                    
                    if controller:
                        device_width, device_height = controller.get_device_resolution()

                        # 判断实际显示方向
                        window_aspect = window_width / window_height if window_height > 0 else 1

                        if window_aspect > 1.3:  # 横屏
                            actual_width = max(device_width, device_height)
                            actual_height = min(device_width, device_height)
                        else:  # 竖屏
                            actual_width = min(device_width, device_height)
                            actual_height = max(device_width, device_height)

                        # 转换为设备坐标
                        if window_width > 0 and window_height > 0:
                            device_x = int(rel_x * actual_width / window_width)
                            device_y = int(rel_y * actual_height / window_height)

                            # 确保坐标在有效范围内
                            device_x = max(0, min(device_x, actual_width - 1))
                            device_y = max(0, min(device_y, actual_height - 1))

                            self.current_device_coords = (device_x, device_y)
                            self.device_coord_label.setText(f"设备: ({device_x}, {device_y})")
                        else:
                            self.device_coord_label.setText(f"设备: (-, -)")
                    else:
                        self.device_coord_label.setText(f"设备: (-, -)")
                else:
                    self.device_coord_label.setText(f"设备: (-, -)")
            else:
                self.device_coord_label.setText(f"设备: (-, -)")

        except Exception:
            self.device_coord_label.setText(f"设备: (-, -)")
            
        # 滴管模式下检测点击
        if self.pipette_mode:
            self.check_pipette_click()
    
    def toggle_pipette_mode(self, target='start'):
        """切换滴管模式"""
        # 根据目标按钮决定状态
        if target == 'start':
            self.pipette_mode = self.pipette_start_btn.isChecked()
            # 如果开启，关闭另一个
            if self.pipette_mode:
                self.pipette_end_btn.setChecked(False)
            self.pipette_target = 'start'
        else:
            self.pipette_mode = self.pipette_end_btn.isChecked()
            # 如果开启，关闭另一个
            if self.pipette_mode:
                self.pipette_start_btn.setChecked(False)
            self.pipette_target = 'end'
        
        self.pipette_info.setVisible(self.pipette_mode)
        
        if self.pipette_mode:
            # 设置鼠标样式为十字
            QApplication.setOverrideCursor(Qt.CursorShape.CrossCursor)
        else:
            # 恢复默认鼠标样式
            QApplication.restoreOverrideCursor()
    
    def check_pipette_click(self):
        """检查滴管点击"""
        import win32api
        import win32con
        
        # 检测鼠标左键
        if win32api.GetAsyncKeyState(win32con.VK_LBUTTON) < 0:
            current_time = time.time()
            # 防止重复触发
            if current_time - self.last_click_time > 0.5:
                self.last_click_time = current_time
                # 自动填充坐标
                x, y = self.current_device_coords
                
                # 根据目标填充坐标
                if self.pipette_target == 'start':
                    self.x1_spin.setValue(x)
                    self.y1_spin.setValue(y)
                    # 关闭滴管模式
                    self.pipette_start_btn.setChecked(False)
                    self.toggle_pipette_mode('start')
                else:
                    self.x2_spin.setValue(x)
                    self.y2_spin.setValue(y)
                    # 关闭滴管模式
                    self.pipette_end_btn.setChecked(False)
                    self.toggle_pipette_mode('end')
                
                self.update_display()
                # 显示状态栏提示而非对话框
                self.statusBar().showMessage(f"已拾取坐标: ({x}, {y})", 2000) if hasattr(self, 'statusBar') else None

    def initUI(self):
        self.setWindowTitle("输入监控区域")
        self.setMinimumWidth(400)

        layout = QVBoxLayout(self)

        # 实时坐标显示（新增）
        coord_display_group = QGroupBox("实时坐标")
        coord_display_layout = QVBoxLayout()

        self.screen_coord_label = QLabel("屏幕: (-, -)")
        self.screen_coord_label.setStyleSheet("font-family: Consolas; font-size: 11px;")
        
        self.device_coord_label = QLabel("设备: (-, -)")
        self.device_coord_label.setStyleSheet("font-family: Consolas; font-size: 11px; color: blue;")

        # 滴管按钮组
        pipette_button_layout = QHBoxLayout()
        
        self.pipette_start_btn = QPushButton("🎯 拾取起始坐标")
        self.pipette_start_btn.setCheckable(True)
        self.pipette_start_btn.clicked.connect(lambda: self.toggle_pipette_mode('start'))
        
        self.pipette_end_btn = QPushButton("🎯 拾取结束坐标")
        self.pipette_end_btn.setCheckable(True)
        self.pipette_end_btn.clicked.connect(lambda: self.toggle_pipette_mode('end'))
        
        # 样式
        pipette_style = """
            QPushButton:checked {
                background-color: #4CAF50;
                color: white;
            }
        """
        self.pipette_start_btn.setStyleSheet(pipette_style)
        self.pipette_end_btn.setStyleSheet(pipette_style)
        
        pipette_button_layout.addWidget(self.pipette_start_btn)
        pipette_button_layout.addWidget(self.pipette_end_btn)

        coord_display_layout.addWidget(self.screen_coord_label)
        coord_display_layout.addWidget(self.device_coord_label)
        coord_display_layout.addLayout(pipette_button_layout)
        
        # 滴管提示
        self.pipette_info = QLabel("提示：点击上方按钮后，在Scrcpy窗口点击即可拾取坐标")
        self.pipette_info.setStyleSheet("color: green; font-size: 10px;")
        self.pipette_info.setVisible(False)
        coord_display_layout.addWidget(self.pipette_info)
        
        coord_display_group.setLayout(coord_display_layout)
        
        layout.addWidget(coord_display_group)

        # 说明文字
        info_label = QLabel("输入监控区域的起始和结束坐标：")
        info_label.setStyleSheet("color: gray; margin-bottom: 10px;")
        layout.addWidget(info_label)

        # 坐标输入区域
        coord_group = QGroupBox("坐标设置")
        coord_layout = QGridLayout()

        # 起始坐标
        coord_layout.addWidget(QLabel("起始坐标:"), 0, 0, 1, 2)
        coord_layout.addWidget(QLabel("X1:"), 1, 0)
        self.x1_spin = QSpinBox()
        self.x1_spin.setRange(0, 9999)
        self.x1_spin.valueChanged.connect(self.update_display)
        coord_layout.addWidget(self.x1_spin, 1, 1)

        coord_layout.addWidget(QLabel("Y1:"), 1, 2)
        self.y1_spin = QSpinBox()
        self.y1_spin.setRange(0, 9999)
        self.y1_spin.valueChanged.connect(self.update_display)
        coord_layout.addWidget(self.y1_spin, 1, 3)

        # 结束坐标
        coord_layout.addWidget(QLabel("结束坐标:"), 2, 0, 1, 2)
        coord_layout.addWidget(QLabel("X2:"), 3, 0)
        self.x2_spin = QSpinBox()
        self.x2_spin.setRange(0, 9999)
        self.x2_spin.setValue(100)
        self.x2_spin.valueChanged.connect(self.update_display)
        coord_layout.addWidget(self.x2_spin, 3, 1)

        coord_layout.addWidget(QLabel("Y2:"), 3, 2)
        self.y2_spin = QSpinBox()
        self.y2_spin.setRange(0, 9999)
        self.y2_spin.setValue(100)
        self.y2_spin.valueChanged.connect(self.update_display)
        coord_layout.addWidget(self.y2_spin, 3, 3)

        coord_group.setLayout(coord_layout)
        layout.addWidget(coord_group)

        # 显示区域
        display_group = QGroupBox("区域信息")
        display_layout = QVBoxLayout()

        self.coord_display = QLabel("起始: (0, 0) → 结束: (100, 100)")
        self.coord_display.setStyleSheet("font-family: Consolas; font-size: 11px; color: blue;")

        self.size_display = QLabel("大小: 100 × 100 像素")
        self.size_display.setStyleSheet("font-family: Consolas; font-size: 11px;")

        display_layout.addWidget(self.coord_display)
        display_layout.addWidget(self.size_display)
        display_group.setLayout(display_layout)
        layout.addWidget(display_group)

        # 按钮
        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self.validate_and_accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

        # 初始更新显示
        self.update_display()



    def load_region(self, region):
        """加载已有区域"""
        x, y, w, h = region
        self.x1_spin.setValue(x)
        self.y1_spin.setValue(y)
        self.x2_spin.setValue(x + w)
        self.y2_spin.setValue(y + h)

    def update_display(self):
        """更新显示信息"""
        x1, y1 = self.x1_spin.value(), self.y1_spin.value()
        x2, y2 = self.x2_spin.value(), self.y2_spin.value()

        self.coord_display.setText(f"起始: ({x1}, {y1}) → 结束: ({x2}, {y2})")

        width = abs(x2 - x1)
        height = abs(y2 - y1)
        self.size_display.setText(f"大小: {width} × {height} 像素")

    def validate_and_accept(self):
        """验证并接受"""
        if self.x2_spin.value() <= self.x1_spin.value():
            QMessageBox.warning(self, "警告", "X2必须大于X1")
            return
        if self.y2_spin.value() <= self.y1_spin.value():
            QMessageBox.warning(self, "警告", "Y2必须大于Y1")
            return
        self.accept()

    def closeEvent(self, event):
        """关闭事件处理"""
        # 确保滴管模式关闭
        if hasattr(self, 'pipette_mode') and self.pipette_mode:
            QApplication.restoreOverrideCursor()
        # 停止坐标追踪
        if hasattr(self, 'coord_timer'):
            self.coord_timer.stop()
        super().closeEvent(event)
    
    def get_region(self):
        """获取区域"""
        x = min(self.x1_spin.value(), self.x2_spin.value())
        y = min(self.y1_spin.value(), self.y2_spin.value())
        width = abs(self.x2_spin.value() - self.x1_spin.value())
        height = abs(self.y2_spin.value() - self.y1_spin.value())
        return (x, y, width, height)


class ActionEditDialog(QDialog):
    """动作编辑对话框"""

    def __init__(self, controller, parent=None, action=None):
        super().__init__(parent)
        self.controller = controller
        self.action = action or {}
        self.main_window = None
        self.random_actions = []

        # 查找主窗口
        p = parent
        while p:
            if hasattr(p, 'log'):
                self.main_window = p
                break
            p = p.parent() if hasattr(p, 'parent') and callable(p.parent) else None

        self.initUI()
        self.load_action()

    def initUI(self):
        """初始化UI"""
        self.setWindowTitle("编辑动作")
        self.setModal(True)
        self.setMinimumWidth(500)

        layout = QVBoxLayout(self)

        # 动作类型
        type_layout = QHBoxLayout()
        type_layout.addWidget(QLabel("动作类型:"))
        self.type_combo = QComboBox()
        self.type_combo.addItems(["点击", "滑动", "输入文本", "按键", "等待", "执行录制", "随机动作", "设置变量", "ADB命令"])
        self.type_combo.currentIndexChanged.connect(self.on_type_changed)
        type_layout.addWidget(self.type_combo)
        layout.addLayout(type_layout)

        # 参数面板
        self.param_stack = QStackedWidget()

        # 创建各种参数widget
        self.create_click_widget()
        self.create_swipe_widget()
        self.create_text_widget()
        self.create_key_widget()
        self.create_wait_widget()
        self.create_recording_widget()
        self.create_random_widget()
        self.create_variable_widget()
        self.create_adb_widget()

        layout.addWidget(self.param_stack)

        # 按钮
        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    def create_click_widget(self):
        """创建点击参数widget"""
        widget = QWidget()
        layout = QFormLayout(widget)

        self.click_x = QSpinBox()
        self.click_x.setRange(0, 9999)
        self.click_y = QSpinBox()
        self.click_y.setRange(0, 9999)

        layout.addRow("X坐标:", self.click_x)
        layout.addRow("Y坐标:", self.click_y)

        self.param_stack.addWidget(widget)

    def create_swipe_widget(self):
        """创建滑动参数widget"""
        widget = QWidget()
        layout = QFormLayout(widget)

        self.swipe_x1 = QSpinBox()
        self.swipe_x1.setRange(0, 9999)
        self.swipe_y1 = QSpinBox()
        self.swipe_y1.setRange(0, 9999)
        self.swipe_x2 = QSpinBox()
        self.swipe_x2.setRange(0, 9999)
        self.swipe_y2 = QSpinBox()
        self.swipe_y2.setRange(0, 9999)
        self.swipe_duration = QSpinBox()
        self.swipe_duration.setRange(100, 5000)
        self.swipe_duration.setValue(300)
        self.swipe_duration.setSuffix(" ms")

        layout.addRow("起始X:", self.swipe_x1)
        layout.addRow("起始Y:", self.swipe_y1)
        layout.addRow("结束X:", self.swipe_x2)
        layout.addRow("结束Y:", self.swipe_y2)
        layout.addRow("持续时间:", self.swipe_duration)

        self.param_stack.addWidget(widget)

    def create_text_widget(self):
        """创建文本参数widget"""
        widget = QWidget()
        layout = QFormLayout(widget)

        self.text_input = QLineEdit()
        layout.addRow("文本内容:", self.text_input)

        self.param_stack.addWidget(widget)

    def create_key_widget(self):
        """创建按键参数widget"""
        widget = QWidget()
        layout = QFormLayout(widget)

        self.key_combo = QComboBox()
        self.key_combo.addItems([
            "返回 (BACK)",
            "主页 (HOME)",
            "最近任务 (RECENT)",
            "音量+ (VOLUME_UP)",
            "音量- (VOLUME_DOWN)",
            "电源 (POWER)"
        ])
        layout.addRow("按键:", self.key_combo)

        self.param_stack.addWidget(widget)

    def create_wait_widget(self):
        """创建等待参数widget"""
        widget = QWidget()
        layout = QFormLayout(widget)

        self.wait_duration = QDoubleSpinBox()
        self.wait_duration.setRange(0.1, 60)
        self.wait_duration.setValue(1)
        self.wait_duration.setSingleStep(0.5)
        self.wait_duration.setSuffix(" 秒")

        layout.addRow("等待时间:", self.wait_duration)

        self.param_stack.addWidget(widget)

    def create_recording_widget(self):
        """创建录制脚本参数widget"""
        widget = QWidget()
        layout = QFormLayout(widget)

        # 文件选择
        file_layout = QHBoxLayout()
        self.recording_file_input = QLineEdit()
        self.recording_file_input.setPlaceholderText("选择录制文件(.json)...")
        self.recording_browse_btn = QPushButton("浏览...")
        self.recording_browse_btn.clicked.connect(self.browse_recording)
        file_layout.addWidget(self.recording_file_input)
        file_layout.addWidget(self.recording_browse_btn)

        # 播放参数
        self.recording_speed_spin = QDoubleSpinBox()
        self.recording_speed_spin.setRange(0.1, 5.0)
        self.recording_speed_spin.setValue(1.0)
        self.recording_speed_spin.setSuffix("x")

        self.recording_random_check = QCheckBox("启用随机化")

        layout.addRow("录制文件:", file_layout)
        layout.addRow("播放速度:", self.recording_speed_spin)
        layout.addRow("", self.recording_random_check)

        self.param_stack.addWidget(widget)
    
    def create_random_widget(self):
        """创建随机动作widget"""
        widget = QWidget()
        layout = QVBoxLayout(widget)
        
        # 动作列表
        self.random_action_list = QListWidget()
        self.random_action_list.setMaximumHeight(120)
        
        # 按钮
        btn_layout = QHBoxLayout()
        add_btn = QPushButton("添加动作选项")
        add_btn.clicked.connect(self.add_random_action)
        remove_btn = QPushButton("删除")
        remove_btn.clicked.connect(self.remove_random_action)
        btn_layout.addWidget(add_btn)
        btn_layout.addWidget(remove_btn)
        
        layout.addWidget(QLabel("随机执行以下动作之一:"))
        layout.addWidget(self.random_action_list)
        layout.addLayout(btn_layout)
        
        self.param_stack.addWidget(widget)
    
    def create_variable_widget(self):
        """创建变量设置widget"""
        widget = QWidget()
        layout = QFormLayout(widget)
        
        self.variable_name_input = QLineEdit()
        self.variable_name_input.setPlaceholderText("例如: counter")
        
        # 操作类型选择
        self.variable_operation = QComboBox()
        self.variable_operation.addItems(["设置", "增加", "减少", "乘以", "除以", "基于变量"])
        self.variable_operation.currentIndexChanged.connect(self.on_variable_operation_changed)
        
        # 值输入（可以是数字或变量名）
        value_layout = QHBoxLayout()
        self.variable_value_spin = QSpinBox()
        self.variable_value_spin.setRange(-9999, 9999)
        self.variable_value_spin.setValue(1)
        
        self.variable_from_input = QLineEdit()
        self.variable_from_input.setPlaceholderText("源变量名")
        self.variable_from_input.setVisible(False)
        
        self.variable_calc_op = QComboBox()
        self.variable_calc_op.addItems(["+", "-", "*", "÷(整除)"])
        self.variable_calc_op.setVisible(False)
        
        self.variable_calc_value = QSpinBox()
        self.variable_calc_value.setRange(-9999, 9999)
        self.variable_calc_value.setValue(1)
        self.variable_calc_value.setVisible(False)
        
        value_layout.addWidget(self.variable_value_spin)
        value_layout.addWidget(self.variable_from_input)
        value_layout.addWidget(self.variable_calc_op)
        value_layout.addWidget(self.variable_calc_value)
        
        layout.addRow("变量名:", self.variable_name_input)
        layout.addRow("操作:", self.variable_operation)
        layout.addRow("值:", value_layout)
        
        # 说明文字
        self.variable_hint = QLabel("提示: 所有变量运算结果都将转换为整数")
        self.variable_hint.setStyleSheet("color: gray; font-size: 10px;")
        layout.addRow("", self.variable_hint)
        
        self.param_stack.addWidget(widget)
        
        # 初始化后缀显示
        self.on_variable_operation_changed(0)
    
    def on_variable_operation_changed(self, index):
        """变量操作类型改变时更新提示"""
        if index == 5:  # 基于变量
            self.variable_value_spin.setVisible(False)
            self.variable_from_input.setVisible(True)
            self.variable_calc_op.setVisible(True)
            self.variable_calc_value.setVisible(True)
            self.variable_hint.setText("提示: arc = brc + 10 形式，结果自动转为整数")
        else:
            self.variable_value_spin.setVisible(True)
            self.variable_from_input.setVisible(False)
            self.variable_calc_op.setVisible(False)
            self.variable_calc_value.setVisible(False)
            self.variable_hint.setText("提示: 所有变量运算结果都将转换为整数")
            
            if index == 0:  # 设置
                self.variable_value_spin.setSuffix("")
            elif index in [1, 2]:  # 增加/减少
                self.variable_value_spin.setSuffix(" (单位)")
            elif index in [3, 4]:  # 乘以/除以
                self.variable_value_spin.setSuffix(" (倍数)")
    
    def create_adb_widget(self):
        """创建ADB命令widget"""
        widget = QWidget()
        layout = QVBoxLayout(widget)
        
        layout.addWidget(QLabel("ADB Shell命令:"))
        
        self.adb_command_input = QTextEdit()
        self.adb_command_input.setPlaceholderText("输入ADB shell命令...\n例如: input keyevent 4\n      am start -n com.example/.MainActivity")
        self.adb_command_input.setMaximumHeight(100)
        
        # 常用命令快速插入
        quick_layout = QHBoxLayout()
        quick_label = QLabel("快速插入:")
        quick_combo = QComboBox()
        quick_combo.addItems([
            "选择常用命令...",
            "input keyevent 4  # 返回键",
            "input keyevent 3  # HOME键",
            "input keyevent 26  # 电源键",
            "am force-stop <包名>  # 强制停止应用",
            "am start -n <包名/活动名>  # 启动应用",
            "settings put system screen_brightness 255  # 设置亮度最大",
            "svc wifi enable  # 开启WiFi",
            "svc wifi disable  # 关闭WiFi"
        ])
        quick_combo.currentTextChanged.connect(self.insert_adb_template)
        
        quick_layout.addWidget(quick_label)
        quick_layout.addWidget(quick_combo)
        quick_layout.addStretch()
        
        layout.addLayout(quick_layout)
        layout.addWidget(self.adb_command_input)
        
        self.param_stack.addWidget(widget)
    
    def insert_adb_template(self, text):
        """插入ADB命令模板"""
        if text and not text.startswith("选择"):
            # 移除注释部分
            command = text.split('#')[0].strip()
            self.adb_command_input.setText(command)
    
    def add_random_action(self):
        """添加随机动作选项"""
        dialog = RandomActionDialog(self.controller, self)
        if dialog.exec():
            action_data = dialog.get_action_data()
            if action_data:
                self.random_actions.append(action_data)
                self.refresh_random_list()
    
    def remove_random_action(self):
        """删除随机动作选项"""
        current = self.random_action_list.currentRow()
        if current >= 0:
            del self.random_actions[current]
            self.refresh_random_list()
    
    def refresh_random_list(self):
        """刷新随机动作列表"""
        self.random_action_list.clear()
        for i, action_data in enumerate(self.random_actions, 1):
            action_type = action_data['action'].get('type', 'unknown')
            var_setting = action_data.get('set_variable', {})
            text = f"选项{i}: {action_type}"
            if var_setting.get('variable'):
                text += f" (设置{var_setting['variable']}={var_setting.get('value', 0)})"
            self.random_action_list.addItem(text)

    def browse_recording(self):
        """浏览选择录制文件"""
        filename, _ = QFileDialog.getOpenFileName(
            self, "选择录制文件", "", "JSON文件 (*.json)"
        )
        if filename:
            self.recording_file_input.setText(filename)

    def on_type_changed(self, index):
        """动作类型改变"""
        self.param_stack.setCurrentIndex(index)

    def load_action(self):
        """加载动作"""
        if not self.action:
            return

        action_type = self.action.get('type')

        if action_type == 'click':
            self.type_combo.setCurrentIndex(0)
            self.click_x.setValue(self.action.get('x', 0))
            self.click_y.setValue(self.action.get('y', 0))

        elif action_type == 'swipe':
            self.type_combo.setCurrentIndex(1)
            self.swipe_x1.setValue(self.action.get('x1', 0))
            self.swipe_y1.setValue(self.action.get('y1', 0))
            self.swipe_x2.setValue(self.action.get('x2', 0))
            self.swipe_y2.setValue(self.action.get('y2', 0))
            self.swipe_duration.setValue(self.action.get('duration', 300))

        elif action_type == 'text':
            self.type_combo.setCurrentIndex(2)
            self.text_input.setText(self.action.get('text', ''))

        elif action_type == 'key':
            self.type_combo.setCurrentIndex(3)

        elif action_type == 'wait':
            self.type_combo.setCurrentIndex(4)
            self.wait_duration.setValue(self.action.get('duration', 1))

        elif action_type == 'recording':
            self.type_combo.setCurrentIndex(5)
            self.recording_file_input.setText(self.action.get('recording_file', ''))
            self.recording_speed_spin.setValue(self.action.get('speed', 1.0))
            self.recording_random_check.setChecked(self.action.get('use_random', False))
        
        elif action_type == 'random':
            self.type_combo.setCurrentIndex(6)
            self.random_actions = self.action.get('sub_actions', [])
            self.refresh_random_list()
        
        elif action_type == 'set_variable':
            self.type_combo.setCurrentIndex(7)
            self.variable_name_input.setText(self.action.get('variable', ''))
            
            operation = self.action.get('operation', 'set')
            if operation == 'from_variable':
                self.variable_operation.setCurrentIndex(5)
                self.variable_from_input.setText(self.action.get('source_variable', ''))
                calc_ops = ['+', '-', '*', '//']
                calc_op = self.action.get('calc_operator', '+')
                if calc_op in calc_ops:
                    self.variable_calc_op.setCurrentIndex(calc_ops.index(calc_op))
                self.variable_calc_value.setValue(self.action.get('calc_value', 0))
            else:
                self.variable_value_spin.setValue(self.action.get('value', 0))
                operations = ["set", "add", "subtract", "multiply", "divide"]
                if operation in operations:
                    self.variable_operation.setCurrentIndex(operations.index(operation))
        
        elif action_type == 'adb_command':
            self.type_combo.setCurrentIndex(8)
            self.adb_command_input.setText(self.action.get('command', ''))

    def get_action(self):
        """获取动作"""
        index = self.type_combo.currentIndex()

        if index == 0:  # 点击
            return {
                'type': 'click',
                'x': self.click_x.value(),
                'y': self.click_y.value()
            }
        elif index == 1:  # 滑动
            return {
                'type': 'swipe',
                'x1': self.swipe_x1.value(),
                'y1': self.swipe_y1.value(),
                'x2': self.swipe_x2.value(),
                'y2': self.swipe_y2.value(),
                'duration': self.swipe_duration.value()
            }
        elif index == 2:  # 文本
            return {
                'type': 'text',
                'text': self.text_input.text()
            }
        elif index == 3:  # 按键
            key_map = {
                "返回 (BACK)": (4, "BACK"),
                "主页 (HOME)": (3, "HOME"),
                "最近任务 (RECENT)": (187, "RECENT"),
                "音量+ (VOLUME_UP)": (24, "VOLUME_UP"),
                "音量- (VOLUME_DOWN)": (25, "VOLUME_DOWN"),
                "电源 (POWER)": (26, "POWER")
            }
            selected = self.key_combo.currentText()
            keycode, key_name = key_map.get(selected, (4, "BACK"))
            return {
                'type': 'key',
                'keycode': keycode,
                'key_name': key_name
            }
        elif index == 4:  # 等待
            return {
                'type': 'wait',
                'duration': self.wait_duration.value()
            }
        elif index == 5:  # 执行录制
            return {
                'type': 'recording',
                'recording_file': self.recording_file_input.text(),
                'speed': self.recording_speed_spin.value(),
                'use_random': self.recording_random_check.isChecked()
            }
        elif index == 6:  # 随机动作
            return {
                'type': 'random',
                'sub_actions': self.random_actions
            }
        elif index == 7:  # 设置变量
            operations = ["set", "add", "subtract", "multiply", "divide", "from_variable"]
            op_index = self.variable_operation.currentIndex()
            # 确保索引有效
            if op_index < 0 or op_index >= len(operations):
                op_index = 0
            
            if op_index == 5:  # 基于变量
                calc_ops = ['+', '-', '*', '//']  # 整除
                return {
                    'type': 'set_variable',
                    'variable': self.variable_name_input.text(),
                    'operation': 'from_variable',
                    'source_variable': self.variable_from_input.text(),
                    'calc_operator': calc_ops[self.variable_calc_op.currentIndex()],
                    'calc_value': self.variable_calc_value.value()
                }
            else:
                return {
                    'type': 'set_variable',
                    'variable': self.variable_name_input.text(),
                    'operation': operations[op_index],
                    'value': self.variable_value_spin.value()
                }
        elif index == 8:  # ADB命令
            return {
                'type': 'adb_command',
                'command': self.adb_command_input.toPlainText()
            }


class ConditionDialog(QDialog):
    """条件编辑对话框"""
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("添加条件")
        self.setModal(True)
        
        layout = QFormLayout(self)
        
        self.variable_input = QLineEdit()
        self.variable_input.setPlaceholderText("例如: song")
        
        self.operator_combo = QComboBox()
        self.operator_combo.addItems(['==', '!=', '>', '<', '>=', '<='])
        
        self.value_spin = QSpinBox()
        self.value_spin.setRange(-9999, 9999)
        
        layout.addRow("变量名:", self.variable_input)
        layout.addRow("比较:", self.operator_combo)
        layout.addRow("值:", self.value_spin)
        
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
    
    def get_condition(self):
        return {
            'variable': self.variable_input.text(),
            'operator': self.operator_combo.currentText(),
            'value': self.value_spin.value()
        }


class RandomActionDialog(QDialog):
    """随机动作选项对话框"""
    
    def __init__(self, controller, parent=None):
        super().__init__(parent)
        self.controller = controller
        self.setWindowTitle("配置动作选项")
        self.setModal(True)
        self.setMinimumWidth(400)
        
        layout = QVBoxLayout(self)
        
        # 动作配置
        action_group = QGroupBox("动作")
        action_layout = QFormLayout()
        
        self.action_type_combo = QComboBox()
        self.action_type_combo.addItems(["点击", "滑动", "等待"])
        self.action_type_combo.currentIndexChanged.connect(self.on_action_type_changed)
        action_layout.addRow("类型:", self.action_type_combo)
        
        # 动作参数容器
        self.action_widget_stack = QStackedWidget()
        
        # 点击参数
        click_widget = QWidget()
        click_layout = QFormLayout(click_widget)
        self.click_x = QSpinBox()
        self.click_x.setRange(0, 9999)
        self.click_y = QSpinBox()
        self.click_y.setRange(0, 9999)
        click_layout.addRow("X:", self.click_x)
        click_layout.addRow("Y:", self.click_y)
        self.action_widget_stack.addWidget(click_widget)
        
        # 滑动参数
        swipe_widget = QWidget()
        swipe_layout = QFormLayout(swipe_widget)
        self.swipe_x1 = QSpinBox()
        self.swipe_x1.setRange(0, 9999)
        self.swipe_y1 = QSpinBox()
        self.swipe_y1.setRange(0, 9999)
        self.swipe_x2 = QSpinBox()
        self.swipe_x2.setRange(0, 9999)
        self.swipe_y2 = QSpinBox()
        self.swipe_y2.setRange(0, 9999)
        swipe_layout.addRow("起始X:", self.swipe_x1)
        swipe_layout.addRow("起始Y:", self.swipe_y1)
        swipe_layout.addRow("结束X:", self.swipe_x2)
        swipe_layout.addRow("结束Y:", self.swipe_y2)
        self.action_widget_stack.addWidget(swipe_widget)
        
        # 等待参数
        wait_widget = QWidget()
        wait_layout = QFormLayout(wait_widget)
        self.wait_duration = QDoubleSpinBox()
        self.wait_duration.setRange(0.1, 10)
        self.wait_duration.setValue(1)
        self.wait_duration.setSuffix(" 秒")
        wait_layout.addRow("时长:", self.wait_duration)
        self.action_widget_stack.addWidget(wait_widget)
        
        action_layout.addRow(self.action_widget_stack)
        action_group.setLayout(action_layout)
        
        # 变量设置（可选）
        variable_group = QGroupBox("执行后设置变量（可选）")
        variable_layout = QFormLayout()
        
        self.set_variable_check = QCheckBox("设置变量")
        self.variable_name = QLineEdit()
        self.variable_value = QSpinBox()
        self.variable_value.setRange(-9999, 9999)
        
        variable_layout.addRow(self.set_variable_check)
        variable_layout.addRow("变量名:", self.variable_name)
        variable_layout.addRow("值:", self.variable_value)
        variable_group.setLayout(variable_layout)
        
        layout.addWidget(action_group)
        layout.addWidget(variable_group)
        
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
    
    def on_action_type_changed(self, index):
        self.action_widget_stack.setCurrentIndex(index)
    
    def get_action_data(self):
        index = self.action_type_combo.currentIndex()
        
        # 构建动作
        if index == 0:  # 点击
            action = {
                'type': 'click',
                'x': self.click_x.value(),
                'y': self.click_y.value()
            }
        elif index == 1:  # 滑动
            action = {
                'type': 'swipe',
                'x1': self.swipe_x1.value(),
                'y1': self.swipe_y1.value(),
                'x2': self.swipe_x2.value(),
                'y2': self.swipe_y2.value(),
                'duration': 300
            }
        else:  # 等待
            action = {
                'type': 'wait',
                'duration': self.wait_duration.value()
            }
        
        result = {'action': action}
        
        # 添加变量设置
        if self.set_variable_check.isChecked() and self.variable_name.text():
            result['set_variable'] = {
                'variable': self.variable_name.text(),
                'value': self.variable_value.value()
            }
        
        return result