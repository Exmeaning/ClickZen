from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *
from PIL import Image
import numpy as np
from core.window_capture import WindowCapture
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

        # 触发参数
        param_group = QGroupBox("触发参数")
        param_layout = QFormLayout()

        self.cooldown_spin = QSpinBox()
        self.cooldown_spin.setRange(0, 300)
        self.cooldown_spin.setValue(5)
        self.cooldown_spin.setSuffix(" 秒")
        param_layout.addRow("冷却时间:", self.cooldown_spin)

        param_group.setLayout(param_layout)

        # 监控任务模式
        mode_group = QGroupBox("监控任务模式")
        mode_layout = QVBoxLayout()
        
        mode_select_layout = QHBoxLayout()
        self.mode_check = QCheckBox("启用模式选择")
        self.mode_check.setChecked(False)
        self.mode_check.toggled.connect(self.on_mode_check_changed)
        
        self.mode_combo = QComboBox()
        self.mode_combo.addItems(["IF模式 (条件触发)", "RANDOM模式 (随机执行)"])
        self.mode_combo.setEnabled(False)
        self.mode_combo.currentIndexChanged.connect(self.on_mode_changed)
        
        mode_select_layout.addWidget(self.mode_check)
        mode_select_layout.addWidget(self.mode_combo)
        mode_select_layout.addStretch()
        
        mode_layout.addLayout(mode_select_layout)
        mode_group.setLayout(mode_layout)
        
        # 执行动作（用于兼容旧版本和基本动作）
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
        
        # IF模式配置（条件-动作对）
        self.if_group = QGroupBox("IF模式配置")
        if_layout = QVBoxLayout()
        
        self.if_pairs_list = QListWidget()
        self.if_pairs_list.setMaximumHeight(150)
        
        if_button_layout = QHBoxLayout()
        self.add_if_pair_btn = QPushButton("添加条件-动作对")
        self.add_if_pair_btn.clicked.connect(self.add_if_pair)
        self.edit_if_pair_btn = QPushButton("编辑")
        self.edit_if_pair_btn.clicked.connect(self.edit_if_pair)
        self.remove_if_pair_btn = QPushButton("删除")
        self.remove_if_pair_btn.clicked.connect(self.remove_if_pair)
        
        if_button_layout.addWidget(self.add_if_pair_btn)
        if_button_layout.addWidget(self.edit_if_pair_btn)
        if_button_layout.addWidget(self.remove_if_pair_btn)
        
        if_layout.addWidget(QLabel("条件-动作配置列表:"))
        if_layout.addWidget(self.if_pairs_list)
        if_layout.addLayout(if_button_layout)
        
        if_help = QLabel(
            "说明：配置多个条件-动作对，当条件满足时执行对应动作序列\n"
            "每个条件可以包含多个子条件（AND/OR逻辑）"
        )
        if_help.setStyleSheet("color: gray; font-size: 10px;")
        if_layout.addWidget(if_help)
        
        self.if_group.setLayout(if_layout)
        self.if_group.setVisible(False)
        
        # RANDOM模式配置
        self.random_group = QGroupBox("RANDOM模式配置")
        random_layout = QVBoxLayout()
        
        self.random_actions_list = QListWidget()
        self.random_actions_list.setMaximumHeight(150)
        
        random_button_layout = QHBoxLayout()
        self.add_random_action_btn = QPushButton("添加动作序列")
        self.add_random_action_btn.clicked.connect(self.add_random_action_sequence)
        self.edit_random_action_btn = QPushButton("编辑")
        self.edit_random_action_btn.clicked.connect(self.edit_random_action_sequence)
        self.remove_random_action_btn = QPushButton("删除")
        self.remove_random_action_btn.clicked.connect(self.remove_random_action_sequence)
        
        random_button_layout.addWidget(self.add_random_action_btn)
        random_button_layout.addWidget(self.edit_random_action_btn)
        random_button_layout.addWidget(self.remove_random_action_btn)
        
        random_layout.addWidget(QLabel("随机执行以下动作序列之一:"))
        random_layout.addWidget(self.random_actions_list)
        random_layout.addLayout(random_button_layout)
        
        random_help = QLabel(
            "说明：配置多个动作序列，触发时随机选择一个执行\n"
            "每个序列可以包含多个动作步骤"
        )
        random_help.setStyleSheet("color: gray; font-size: 10px;")
        random_layout.addWidget(random_help)
        
        self.random_group.setLayout(random_layout)
        self.random_group.setVisible(False)
        
        # 初始化模式数据
        self.if_pairs = []
        self.random_action_sequences = []

        # 条件检测组
        condition_group = QGroupBox("触发条件")
        condition_layout = QVBoxLayout()
        
        # 条件逻辑选择
        mode_layout = QHBoxLayout()
        mode_layout.addWidget(QLabel("条件逻辑:"))
        self.condition_logic_combo = QComboBox()
        self.condition_logic_combo.addItems(["AND (全部满足)", "OR (任一满足)", "NOT (全部不满足)"])
        mode_layout.addWidget(self.condition_logic_combo)
        mode_layout.addStretch()
        condition_layout.addLayout(mode_layout)
        
        # 统一的条件列表
        self.unified_condition_list = QListWidget()
        self.unified_condition_list.setMaximumHeight(150)
        
        # 按钮布局
        condition_button_layout = QHBoxLayout()
        
        # 添加按钮（带菜单）
        self.add_condition_menu_btn = QPushButton("添加条件")
        add_menu = QMenu()
        add_menu.addAction("添加变量条件", self.add_variable_condition)
        add_menu.addAction("添加图像检测", self.add_image_condition)
        self.add_condition_menu_btn.setMenu(add_menu)
        
        self.edit_condition_btn = QPushButton("编辑")
        self.edit_condition_btn.clicked.connect(self.edit_unified_condition)
        self.remove_condition_btn = QPushButton("删除")
        self.remove_condition_btn.clicked.connect(self.remove_unified_condition)
        
        condition_button_layout.addWidget(self.add_condition_menu_btn)
        condition_button_layout.addWidget(self.edit_condition_btn)
        condition_button_layout.addWidget(self.remove_condition_btn)
        
        condition_layout.addWidget(QLabel("检测条件列表:"))
        condition_layout.addWidget(self.unified_condition_list)
        condition_layout.addLayout(condition_button_layout)
        
        help_text = QLabel(
            "说明：\n"
            "• 变量条件：基于公共变量值判断\n"
            "• 图像检测：检测指定区域的图像是否存在\n"
            "• AND：所有条件都满足时触发\n"
            "• OR：任一条件满足时触发\n"
            "• NOT：所有条件都不满足时触发"
        )
        help_text.setStyleSheet("color: gray; font-size: 10px;")
        condition_layout.addWidget(help_text)
        
        condition_group.setLayout(condition_layout)
        
        # 初始化统一条件列表
        self.unified_conditions = []

        # 按钮
        button_box = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        button_box.accepted.connect(self.accept)
        button_box.rejected.connect(self.reject)

        # 添加到主布局
        layout.addWidget(info_group)
        layout.addWidget(condition_group)
        layout.addWidget(param_group)
        layout.addWidget(mode_group)
        
        # 基本动作组（只在传统模式下显示）
        self.traditional_action_group = action_group
        layout.addWidget(self.traditional_action_group)
        
        layout.addWidget(self.if_group)
        layout.addWidget(self.random_group)
        layout.addStretch()
        
        scroll.setWidget(scroll_widget)
        
        # 主窗口布局
        main_layout = QVBoxLayout(self)
        main_layout.addWidget(scroll)
        main_layout.addWidget(button_box)

    def on_mode_check_changed(self, checked):
        """模式选择复选框状态改变"""
        self.mode_combo.setEnabled(checked)
        if checked:
            self.on_mode_changed(self.mode_combo.currentIndex())
        else:
            # 传统模式：显示基本动作配置，隐藏特殊配置
            self.if_group.setVisible(False)
            self.random_group.setVisible(False)
            self.traditional_action_group.setVisible(True)
    
    def on_mode_changed(self, index):
        """模式改变时更新界面"""
        if not self.mode_check.isChecked():
            return
            
        # 隐藏传统动作组（IF/RANDOM模式不使用）
        self.traditional_action_group.setVisible(False)
        
        if index == 0:  # IF模式
            self.if_group.setVisible(True)
            self.random_group.setVisible(False)
        else:  # RANDOM模式
            self.if_group.setVisible(False)
            self.random_group.setVisible(True)
    
    def add_if_pair(self):
        """添加IF条件-动作对"""
        dialog = IFPairDialog(self.controller, self)
        if dialog.exec():
            pair = dialog.get_if_pair()
            if pair:
                self.if_pairs.append(pair)
                self.refresh_if_pairs_list()
    
    def edit_if_pair(self):
        """编辑IF条件-动作对"""
        current = self.if_pairs_list.currentRow()
        if current >= 0 and current < len(self.if_pairs):
            dialog = IFPairDialog(self.controller, self, self.if_pairs[current])
            if dialog.exec():
                self.if_pairs[current] = dialog.get_if_pair()
                self.refresh_if_pairs_list()
    
    def remove_if_pair(self):
        """删除IF条件-动作对"""
        current = self.if_pairs_list.currentRow()
        if current >= 0:
            del self.if_pairs[current]
            self.refresh_if_pairs_list()
    
    def refresh_if_pairs_list(self):
        """刷新IF条件-动作对列表"""
        self.if_pairs_list.clear()
        for i, pair in enumerate(self.if_pairs, 1):
            conditions_count = len(pair.get('conditions', []))
            actions_count = len(pair.get('actions', []))
            logic = pair.get('logic', 'AND')
            text = f"条件组{i}: {conditions_count}个条件({logic}) → {actions_count}个动作"
            self.if_pairs_list.addItem(text)
    
    def add_random_action_sequence(self):
        """添加RANDOM动作序列"""
        dialog = ActionSequenceDialog(self.controller, self)
        if dialog.exec():
            sequence = dialog.get_action_sequence()
            if sequence:
                self.random_action_sequences.append(sequence)
                self.refresh_random_actions_list()
    
    def edit_random_action_sequence(self):
        """编辑RANDOM动作序列"""
        current = self.random_actions_list.currentRow()
        if current >= 0 and current < len(self.random_action_sequences):
            dialog = ActionSequenceDialog(self.controller, self, self.random_action_sequences[current])
            if dialog.exec():
                self.random_action_sequences[current] = dialog.get_action_sequence()
                self.refresh_random_actions_list()
    
    def remove_random_action_sequence(self):
        """删除RANDOM动作序列"""
        current = self.random_actions_list.currentRow()
        if current >= 0:
            del self.random_action_sequences[current]
            self.refresh_random_actions_list()
    
    def refresh_random_actions_list(self):
        """刷新RANDOM动作序列列表"""
        self.random_actions_list.clear()
        for i, sequence in enumerate(self.random_action_sequences, 1):
            actions_count = len(sequence.get('actions', []))
            name = sequence.get('name', f'序列{i}')
            text = f"{name} ({actions_count}个动作)"
            self.random_actions_list.addItem(text)
    
    def load_config(self):
        """加载配置"""
        if self.task_config:
            self.name_input.setText(self.task_config.get('name', ''))
            self.enabled_check.setChecked(self.task_config.get('enabled', True))
            self.cooldown_spin.setValue(self.task_config.get('cooldown', 5))

            # 加载任务模式
            task_mode = self.task_config.get('task_mode')
            if task_mode:
                self.mode_check.setChecked(True)
                self.traditional_action_group.setVisible(False)
                if task_mode == 'IF':
                    self.mode_combo.setCurrentIndex(0)
                    self.if_group.setVisible(True)
                    self.random_group.setVisible(False)
                    self.if_pairs = self.task_config.get('if_pairs', [])
                    self.refresh_if_pairs_list()
                elif task_mode == 'RANDOM':
                    self.mode_combo.setCurrentIndex(1)
                    self.if_group.setVisible(False)
                    self.random_group.setVisible(True)
                    self.random_action_sequences = self.task_config.get('random_sequences', [])
                    self.refresh_random_actions_list()
            else:
                # 兼容旧版本 - 传统模式
                self.mode_check.setChecked(False)
                self.traditional_action_group.setVisible(True)
                self.if_group.setVisible(False)
                self.random_group.setVisible(False)
                self.actions = self.task_config.get('actions', [])
                self.refresh_action_list()
            
            # 加载统一条件
            self.unified_conditions = self.task_config.get('unified_conditions', [])
            
            # 兼容旧版本 - 自动转换
            if not self.unified_conditions:
                # 转换旧的单一模板
                if 'template' in self.task_config and self.task_config['template']:
                    self.unified_conditions.append({
                        'type': 'image',
                        'region': self.task_config.get('region'),
                        'template': self.task_config['template'],
                        'expect_exist': True,
                        'threshold': self.task_config.get('threshold', 0.85)
                    })
                
                # 转换旧的变量条件
                old_conditions = self.task_config.get('conditions', [])
                for cond in old_conditions:
                    self.unified_conditions.append({
                        'type': 'variable',
                        'variable': cond.get('variable'),
                        'operator': cond.get('operator'),
                        'value': cond.get('value')
                    })
                
                # 转换旧的多条件
                old_multi = self.task_config.get('multi_conditions', [])
                for cond in old_multi:
                    self.unified_conditions.append({
                        'type': 'image',
                        'region': cond.get('region'),
                        'template': cond.get('template'),
                        'expect_exist': cond.get('expect_exist', True),
                        'threshold': cond.get('threshold', 0.85)
                    })
            
            # 加载条件逻辑
            logic = self.task_config.get('condition_logic')
            if not logic:
                # 兼容旧版本
                logic = self.task_config.get('condition_mode', 'AND (全部满足)')
            self.condition_logic_combo.setCurrentText(logic)
            
            self.refresh_unified_condition_list()
    
    def refresh_unified_condition_list(self):
        """刷新统一条件列表"""
        self.unified_condition_list.clear()
        for i, condition in enumerate(self.unified_conditions, 1):
            if condition.get('type') == 'variable':
                var = condition.get('variable', '')
                op = condition.get('operator', '==')
                val = condition.get('value', 0)
                text = f"[变量] {var} {op} {val}"
            else:  # image
                region = condition.get('region')
                region_text = "全屏"
                if region and len(region) == 4:
                    x, y, w, h = region
                    region_text = f"({x},{y},{w},{h})"
                
                expect = "✔存在" if condition.get('expect_exist', True) else "❌不存在"
                text = f"[图像] 区域{region_text} - 期望{expect}"
            
            self.unified_condition_list.addItem(text)
    
    def add_variable_condition(self):
        """添加变量条件"""
        dialog = ConditionDialog(self)
        if dialog.exec():
            condition = dialog.get_condition()
            condition['type'] = 'variable'
            self.unified_conditions.append(condition)
            self.refresh_unified_condition_list()
    
    def add_image_condition(self):
        """添加图像检测条件"""
        dialog = MultiConditionDialog(self.controller, self)
        if dialog.exec():
            condition = dialog.get_condition()
            if condition:
                condition['type'] = 'image'
                self.unified_conditions.append(condition)
                self.refresh_unified_condition_list()
    
    def edit_unified_condition(self):
        """编辑条件"""
        current = self.unified_condition_list.currentRow()
        if current >= 0 and current < len(self.unified_conditions):
            condition = self.unified_conditions[current]
            
            if condition.get('type') == 'variable':
                dialog = ConditionDialog(self, condition)
                if dialog.exec():
                    new_condition = dialog.get_condition()
                    new_condition['type'] = 'variable'
                    self.unified_conditions[current] = new_condition
            else:
                dialog = MultiConditionDialog(self.controller, self, condition)
                if dialog.exec():
                    new_condition = dialog.get_condition()
                    if new_condition:
                        new_condition['type'] = 'image'
                        self.unified_conditions[current] = new_condition
            
            self.refresh_unified_condition_list()
    
    def remove_unified_condition(self):
        """删除条件"""
        current = self.unified_condition_list.currentRow()
        if current >= 0:
            del self.unified_conditions[current]
            self.refresh_unified_condition_list()
    
    def add_condition(self):
        """兼容旧方法"""
        self.add_variable_condition()
    
    def remove_condition(self):
        """兼容旧方法"""
        self.remove_unified_condition()
    
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

        config = {
            'name': task_name,
            'enabled': self.enabled_check.isChecked(),
            'cooldown': self.cooldown_spin.value(),
            'unified_conditions': self.unified_conditions,
            'condition_logic': self.condition_logic_combo.currentText()
        }
        
        # 根据模式保存不同的配置
        if self.mode_check.isChecked():
            mode_index = self.mode_combo.currentIndex()
            if mode_index == 0:  # IF模式
                if not self.if_pairs:
                    QMessageBox.warning(self, "警告", "请添加至少一个条件-动作对")
                    return None
                config['task_mode'] = 'IF'
                config['if_pairs'] = self.if_pairs
                config['actions'] = []  # IF模式不使用基本动作
            else:  # RANDOM模式
                if not self.random_action_sequences:
                    QMessageBox.warning(self, "警告", "请添加至少一个动作序列")
                    return None
                config['task_mode'] = 'RANDOM'
                config['random_sequences'] = self.random_action_sequences
                config['actions'] = []  # RANDOM模式不使用基本动作
        else:
            # 传统模式，检查基本配置
            if not self.unified_conditions:
                QMessageBox.warning(self, "警告", "请添加至少一个检测条件")
                return None
            if not self.actions:
                QMessageBox.warning(self, "警告", "请添加至少一个执行动作")
                return None
            config['actions'] = self.actions
            
        return config


from gui.coordinate_picker_dialog import CoordinatePickerDialog

class RegionInputDialog(QDialog):
    """区域输入对话框"""

    def __init__(self, parent=None, initial_region=None):
        super().__init__(parent)
        self.initial_region = initial_region
        self.pipette_target = 'start'
        
        # 获取controller和main_window
        self.controller = None
        self.main_window = None
        p = parent
        while p:
            if hasattr(p, 'controller'):
                self.controller = p.controller
            if hasattr(p, 'log'):
                self.main_window = p
            if self.controller and self.main_window:
                break
            p = p.parent() if hasattr(p, 'parent') and callable(p.parent) else None
        
        self.initUI()
        if initial_region:
            self.load_region(initial_region)
            
    def start_pipette(self, target='start'):
        """启动截图拾取"""
        if not self.controller:
            QMessageBox.warning(self, "错误", "无法获取控制器")
            return
            
        self.pipette_target = target
        
        # 1. 获取截图
        try:
            # 检查是否是模拟器模式
            is_simulator = False
            hwnd = None
            crop_rect = None
            
            if hasattr(self.controller, 'simulator_hwnd') and self.controller.simulator_hwnd:
                is_simulator = True
                hwnd = self.controller.simulator_hwnd
                crop_rect = self.controller.simulator_crop_rect
            
            if is_simulator:
                # 模拟器模式：截取整个窗口，然后裁剪
                screenshot = WindowCapture.capture_window_by_hwnd(hwnd)
                if not screenshot:
                    QMessageBox.warning(self, "错误", "无法截取模拟器窗口")
                    return
                
                # 执行裁剪
                if crop_rect:
                    cx, cy, cw, ch = crop_rect
                    # 确保裁剪区域有效
                    w, h = screenshot.size
                    if 0 <= cx < w and 0 <= cy < h:
                        screenshot = screenshot.crop((cx, cy, cx + cw, cy + ch))
            else:
                # 设备模式：截取Scrcpy
                screenshot = WindowCapture.capture_window_safe("scrcpy", client_only=True)
                if not screenshot:
                    QMessageBox.warning(self, "错误", "无法找到Scrcpy窗口或截图失败")
                    return
            
            # 2. 获取设备分辨率
            device_res = self.controller.get_device_resolution()
            
            # 3. 打开拾取对话框
            dialog = CoordinatePickerDialog(screenshot, device_res, self)
            if dialog.exec():
                coord = dialog.get_result()
                if coord:
                    x, y = coord
                    if self.pipette_target == 'start':
                        self.x1_spin.setValue(x)
                        self.y1_spin.setValue(y)
                    else:
                        self.x2_spin.setValue(x)
                        self.y2_spin.setValue(y)
                    self.update_display()
                    
        except Exception as e:
            QMessageBox.critical(self, "错误", f"拾取失败: {str(e)}")
            import traceback
            traceback.print_exc()

    def initUI(self):
        self.setWindowTitle("输入监控区域")
        self.setMinimumWidth(400)

        layout = QVBoxLayout(self)

        # 滴管按钮组
        pipette_button_layout = QHBoxLayout()
        
        self.pipette_start_btn = QPushButton("🎯 截图拾取起始坐标")
        self.pipette_start_btn.clicked.connect(lambda: self.start_pipette('start'))
        
        self.pipette_end_btn = QPushButton("🎯 截图拾取结束坐标")
        self.pipette_end_btn.clicked.connect(lambda: self.start_pipette('end'))
        
        pipette_button_layout.addWidget(self.pipette_start_btn)
        pipette_button_layout.addWidget(self.pipette_end_btn)
        
        layout.addLayout(pipette_button_layout)
        
        # 说明文字
        info_label = QLabel("提示: 点击上方按钮截取当前画面并选择坐标")
        info_label.setStyleSheet("color: green; font-size: 10px; margin-bottom: 5px;")
        layout.addWidget(info_label)

        # 说明文字2
        info_label2 = QLabel("输入监控区域的起始和结束坐标：")
        info_label2.setStyleSheet("color: gray; margin-bottom: 10px;")
        layout.addWidget(info_label2)

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
        # 停止滴管
        if hasattr(self, 'eyedropper'):
            self.eyedropper.stop()
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
        self.type_combo.addItems(["点击", "滑动", "输入文本", "按键", "等待", "执行录制", "设置变量", "ADB命令"])
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
        
        elif action_type == 'set_variable':
            self.type_combo.setCurrentIndex(6)
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
            self.type_combo.setCurrentIndex(7)
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
        elif index == 6:  # 设置变量
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
        elif index == 7:  # ADB命令
            return {
                'type': 'adb_command',
                'command': self.adb_command_input.toPlainText()
            }


class ConditionDialog(QDialog):
    """条件编辑对话框"""
    
    def __init__(self, parent=None, condition=None):
        super().__init__(parent)
        self.condition = condition or {}
        self.setWindowTitle("变量条件")
        self.setModal(True)
        
        layout = QFormLayout(self)
        
        self.variable_input = QLineEdit()
        self.variable_input.setPlaceholderText("例如: song")
        
        self.operator_combo = QComboBox()
        self.operator_combo.addItems(['==', '!=', '>', '<', '>=', '<='])
        
        self.value_spin = QSpinBox()
        self.value_spin.setRange(-9999, 9999)
        
        # 加载已有数据
        if self.condition:
            self.variable_input.setText(self.condition.get('variable', ''))
            op = self.condition.get('operator', '==')
            index = self.operator_combo.findText(op)
            if index >= 0:
                self.operator_combo.setCurrentIndex(index)
            self.value_spin.setValue(self.condition.get('value', 0))
        
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


class MultiConditionDialog(QDialog):
    """多条件检测对话框"""
    
    def __init__(self, controller, parent=None, condition=None):
        super().__init__(parent)
        self.controller = controller
        self.condition = condition or {}
        self.template_image = None
        self.region = None
        
        self.setWindowTitle("配置检测条件")
        self.setModal(True)
        self.setMinimumWidth(500)
        
        self.initUI()
        self.load_condition()
    
    def initUI(self):
        layout = QVBoxLayout(self)
        
        # 检测区域
        region_group = QGroupBox("检测区域")
        region_layout = QVBoxLayout()
        
        region_button_layout = QHBoxLayout()
        self.select_region_btn = QPushButton("选择区域")
        self.select_region_btn.clicked.connect(self.select_region)
        self.clear_region_btn = QPushButton("全屏")
        self.clear_region_btn.clicked.connect(self.clear_region)
        region_button_layout.addWidget(self.select_region_btn)
        region_button_layout.addWidget(self.clear_region_btn)
        
        self.region_label = QLabel("检测全屏")
        region_layout.addLayout(region_button_layout)
        region_layout.addWidget(self.region_label)
        region_group.setLayout(region_layout)
        
        # 模板图片
        template_group = QGroupBox("模板图片")
        template_layout = QVBoxLayout()
        
        template_button_layout = QHBoxLayout()
        self.capture_template_btn = QPushButton("截取模板")
        self.capture_template_btn.clicked.connect(self.capture_template)
        template_button_layout.addWidget(self.capture_template_btn)
        
        self.template_label = QLabel("未选择模板")
        self.template_label.setMinimumHeight(100)
        self.template_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.template_label.setStyleSheet("border: 1px solid #ccc;")
        
        template_layout.addLayout(template_button_layout)
        template_layout.addWidget(self.template_label)
        template_group.setLayout(template_layout)
        
        # 期望结果
        expect_group = QGroupBox("期望结果")
        expect_layout = QHBoxLayout()
        
        self.expect_exist_radio = QRadioButton("✔ 存在（找到匹配）")
        self.expect_exist_radio.setChecked(True)
        self.expect_not_exist_radio = QRadioButton("❌ 不存在（找不到匹配）")
        
        expect_layout.addWidget(self.expect_exist_radio)
        expect_layout.addWidget(self.expect_not_exist_radio)
        expect_group.setLayout(expect_layout)
        
        # 匹配阈值
        threshold_layout = QHBoxLayout()
        threshold_layout.addWidget(QLabel("匹配阈值:"))
        self.threshold_spin = QDoubleSpinBox()
        self.threshold_spin.setRange(0.5, 1.0)
        self.threshold_spin.setValue(0.85)
        self.threshold_spin.setSingleStep(0.01)
        threshold_layout.addWidget(self.threshold_spin)
        threshold_layout.addStretch()
        
        layout.addWidget(region_group)
        layout.addWidget(template_group)
        layout.addWidget(expect_group)
        layout.addLayout(threshold_layout)
        
        # 按钮
        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self.validate_and_accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
    
    def load_condition(self):
        """加载条件"""
        if self.condition:
            self.region = self.condition.get('region')
            if self.region and len(self.region) == 4:
                x, y, w, h = self.region
                self.region_label.setText(f"起始: ({x}, {y}) → 结束: ({x + w}, {y + h})")
            
            self.template_image = self.condition.get('template')
            if self.template_image:
                self.show_template_preview()
            
            expect_exist = self.condition.get('expect_exist', True)
            if expect_exist:
                self.expect_exist_radio.setChecked(True)
            else:
                self.expect_not_exist_radio.setChecked(True)
            
            self.threshold_spin.setValue(self.condition.get('threshold', 0.85))
    
    def select_region(self):
        """选择检测区域"""
        dialog = RegionInputDialog(self, self.region)
        if dialog.exec():
            self.region = dialog.get_region()
            x, y, w, h = self.region
            self.region_label.setText(f"起始: ({x}, {y}) → 结束: ({x + w}, {y + h})")
    
    def clear_region(self):
        """清除区域"""
        self.region = None
        self.region_label.setText("检测全屏")
    
    def capture_template(self):
        """截取模板"""
        screenshot = None
        
        # 1. 尝试获取截图
        try:
            # 检查是否是模拟器模式
            is_simulator = False
            hwnd = None
            crop_rect = None
            
            if hasattr(self.controller, 'simulator_hwnd') and self.controller.simulator_hwnd:
                is_simulator = True
                hwnd = self.controller.simulator_hwnd
                crop_rect = self.controller.simulator_crop_rect
            
            if is_simulator:
                # 模拟器模式
                full_screenshot = WindowCapture.capture_window_by_hwnd(hwnd)
                if full_screenshot:
                    if crop_rect:
                        cx, cy, cw, ch = crop_rect
                        w, h = full_screenshot.size
                        if 0 <= cx < w and 0 <= cy < h:
                            screenshot = full_screenshot.crop((cx, cy, cx + cw, cy + ch))
                        else:
                            screenshot = full_screenshot
                    else:
                        screenshot = full_screenshot
            else:
                # 设备模式
                screenshot = WindowCapture.capture_window_safe("scrcpy", client_only=True)
                
        except Exception as e:
            print(f"截图失败: {e}")
            
        if not screenshot:
            msg = "无法截取模拟器窗口" if is_simulator else "无法找到Scrcpy窗口"
            QMessageBox.warning(self, "警告", msg)
            return
        
        if self.region:
            x, y, w, h = self.region
            
            window_width, window_height = screenshot.size
            device_width, device_height = self.controller.get_device_resolution()
            
            window_aspect = window_width / window_height
            
            if window_aspect > 1.3:  # 横屏
                actual_device_width = max(device_width, device_height)
                actual_device_height = min(device_width, device_height)
            else:  # 竖屏
                actual_device_width = min(device_width, device_height)
                actual_device_height = max(device_width, device_height)
            
            scale_x = window_width / actual_device_width
            scale_y = window_height / actual_device_height
            
            window_x = int(x * scale_x)
            window_y = int(y * scale_y)
            window_w = int(w * scale_x)
            window_h = int(h * scale_y)
            
            window_x = max(0, min(window_x, window_width - 1))
            window_y = max(0, min(window_y, window_height - 1))
            window_w = min(window_w, window_width - window_x)
            window_h = min(window_h, window_height - window_y)
            
            if window_w > 0 and window_h > 0:
                self.template_image = screenshot.crop((window_x, window_y,
                                                       window_x + window_w,
                                                       window_y + window_h))
        else:
            # 提示选择区域
            dialog = RegionInputDialog(self)
            if dialog.exec():
                self.region = dialog.get_region()
                self.capture_template()
                return
        
        self.show_template_preview()
    
    def show_template_preview(self):
        """显示模板预览"""
        if self.template_image:
            try:
                if self.template_image.mode != 'RGB':
                    self.template_image = self.template_image.convert('RGB')
                
                img_array = np.array(self.template_image)
                height, width = img_array.shape[:2]
                
                if len(img_array.shape) == 2:
                    img_array = np.stack([img_array] * 3, axis=-1)
                elif len(img_array.shape) == 3 and img_array.shape[2] == 4:
                    img_array = img_array[:, :, :3]
                
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
    
    def validate_and_accept(self):
        """验证并接受"""
        if not self.template_image:
            QMessageBox.warning(self, "警告", "请截取模板图片")
            return
        self.accept()
    
    def get_condition(self):
        """获取条件配置"""
        if not self.template_image:
            return None
        
        return {
            'region': self.region,
            'template': self.template_image,
            'expect_exist': self.expect_exist_radio.isChecked(),
            'threshold': self.threshold_spin.value()
        }


class IFPairDialog(QDialog):
    """IF条件-动作对配置对话框"""
    
    def __init__(self, controller, parent=None, pair=None):
        super().__init__(parent)
        self.controller = controller
        self.pair = pair or {}
        self.conditions = self.pair.get('conditions', [])
        self.actions = self.pair.get('actions', [])
        
        self.setWindowTitle("配置条件-动作对")
        self.setModal(True)
        self.setMinimumSize(600, 500)
        
        self.initUI()
        self.load_pair()
    
    def initUI(self):
        layout = QVBoxLayout(self)
        
        # 条件配置
        condition_group = QGroupBox("触发条件")
        condition_layout = QVBoxLayout()
        
        # 条件逻辑
        logic_layout = QHBoxLayout()
        logic_layout.addWidget(QLabel("条件逻辑:"))
        self.logic_combo = QComboBox()
        self.logic_combo.addItems(["AND (全部满足)", "OR (任一满足)"])
        logic_layout.addWidget(self.logic_combo)
        logic_layout.addStretch()
        condition_layout.addLayout(logic_layout)
        
        # 条件列表
        self.condition_list = QListWidget()
        self.condition_list.setMaximumHeight(120)
        
        # 条件按钮
        cond_btn_layout = QHBoxLayout()
        self.add_cond_menu_btn = QPushButton("添加条件")
        cond_menu = QMenu()
        cond_menu.addAction("变量条件", self.add_variable_condition)
        cond_menu.addAction("图像检测", self.add_image_condition)
        self.add_cond_menu_btn.setMenu(cond_menu)
        
        self.edit_cond_btn = QPushButton("编辑")
        self.edit_cond_btn.clicked.connect(self.edit_condition)
        self.remove_cond_btn = QPushButton("删除")
        self.remove_cond_btn.clicked.connect(self.remove_condition)
        
        cond_btn_layout.addWidget(self.add_cond_menu_btn)
        cond_btn_layout.addWidget(self.edit_cond_btn)
        cond_btn_layout.addWidget(self.remove_cond_btn)
        
        condition_layout.addWidget(self.condition_list)
        condition_layout.addLayout(cond_btn_layout)
        condition_group.setLayout(condition_layout)
        
        # 动作配置
        action_group = QGroupBox("执行动作")
        action_layout = QVBoxLayout()
        
        self.action_list = QListWidget()
        self.action_list.setMaximumHeight(120)
        
        # 动作按钮
        action_btn_layout = QHBoxLayout()
        self.add_action_btn = QPushButton("添加动作")
        self.add_action_btn.clicked.connect(self.add_action)
        self.edit_action_btn = QPushButton("编辑")
        self.edit_action_btn.clicked.connect(self.edit_action)
        self.remove_action_btn = QPushButton("删除")
        self.remove_action_btn.clicked.connect(self.remove_action)
        
        action_btn_layout.addWidget(self.add_action_btn)
        action_btn_layout.addWidget(self.edit_action_btn)
        action_btn_layout.addWidget(self.remove_action_btn)
        
        action_layout.addWidget(self.action_list)
        action_layout.addLayout(action_btn_layout)
        action_group.setLayout(action_layout)
        
        layout.addWidget(condition_group)
        layout.addWidget(action_group)
        
        # 按钮
        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self.validate_and_accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
    
    def load_pair(self):
        """加载条件-动作对"""
        if self.pair:
            logic = self.pair.get('logic', 'AND')
            if 'AND' in logic:
                self.logic_combo.setCurrentIndex(0)
            else:
                self.logic_combo.setCurrentIndex(1)
            
            self.refresh_condition_list()
            self.refresh_action_list()
    
    def add_variable_condition(self):
        """添加变量条件"""
        dialog = ConditionDialog(self)
        if dialog.exec():
            condition = dialog.get_condition()
            condition['type'] = 'variable'
            self.conditions.append(condition)
            self.refresh_condition_list()
    
    def add_image_condition(self):
        """添加图像条件"""
        dialog = MultiConditionDialog(self.controller, self)
        if dialog.exec():
            condition = dialog.get_condition()
            if condition:
                condition['type'] = 'image'
                self.conditions.append(condition)
                self.refresh_condition_list()
    
    def edit_condition(self):
        """编辑条件"""
        current = self.condition_list.currentRow()
        if current >= 0 and current < len(self.conditions):
            condition = self.conditions[current]
            if condition.get('type') == 'variable':
                dialog = ConditionDialog(self, condition)
                if dialog.exec():
                    new_condition = dialog.get_condition()
                    new_condition['type'] = 'variable'
                    self.conditions[current] = new_condition
            else:
                dialog = MultiConditionDialog(self.controller, self, condition)
                if dialog.exec():
                    new_condition = dialog.get_condition()
                    if new_condition:
                        new_condition['type'] = 'image'
                        self.conditions[current] = new_condition
            self.refresh_condition_list()
    
    def remove_condition(self):
        """删除条件"""
        current = self.condition_list.currentRow()
        if current >= 0:
            del self.conditions[current]
            self.refresh_condition_list()
    
    def refresh_condition_list(self):
        """刷新条件列表"""
        self.condition_list.clear()
        for condition in self.conditions:
            if condition.get('type') == 'variable':
                var = condition.get('variable', '')
                op = condition.get('operator', '==')
                val = condition.get('value', 0)
                text = f"[变量] {var} {op} {val}"
            else:
                region = condition.get('region')
                region_text = "全屏" if not region else f"区域"
                expect = "存在" if condition.get('expect_exist', True) else "不存在"
                text = f"[图像] {region_text} - 期望{expect}"
            self.condition_list.addItem(text)
    
    def add_action(self):
        """添加动作"""
        dialog = ActionEditDialog(self.controller, self)
        if dialog.exec():
            action = dialog.get_action()
            if action:
                self.actions.append(action)
                self.refresh_action_list()
    
    def edit_action(self):
        """编辑动作"""
        current = self.action_list.currentRow()
        if current >= 0 and current < len(self.actions):
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
            # 使用父窗口的format_action_text方法
            if hasattr(self.parent(), 'format_action_text'):
                text = self.parent().format_action_text(action)
            else:
                text = str(action.get('type', 'unknown'))
            self.action_list.addItem(text)
    
    def validate_and_accept(self):
        """验证并接受"""
        if not self.conditions:
            QMessageBox.warning(self, "警告", "请添加至少一个条件")
            return
        if not self.actions:
            QMessageBox.warning(self, "警告", "请添加至少一个动作")
            return
        self.accept()
    
    def get_if_pair(self):
        """获取条件-动作对"""
        return {
            'logic': self.logic_combo.currentText(),
            'conditions': self.conditions,
            'actions': self.actions
        }


class ActionSequenceDialog(QDialog):
    """动作序列配置对话框"""
    
    def __init__(self, controller, parent=None, sequence=None):
        super().__init__(parent)
        self.controller = controller
        self.sequence = sequence or {}
        self.actions = self.sequence.get('actions', [])
        
        self.setWindowTitle("配置动作序列")
        self.setModal(True)
        self.setMinimumWidth(500)
        
        self.initUI()
        self.load_sequence()
    
    def initUI(self):
        layout = QVBoxLayout(self)
        
        # 序列名称
        name_layout = QHBoxLayout()
        name_layout.addWidget(QLabel("序列名称:"))
        self.name_input = QLineEdit()
        self.name_input.setPlaceholderText("输入序列名称...")
        name_layout.addWidget(self.name_input)
        layout.addLayout(name_layout)
        
        # 动作列表
        action_group = QGroupBox("动作步骤")
        action_layout = QVBoxLayout()
        
        self.action_list = QListWidget()
        self.action_list.setMaximumHeight(200)
        
        # 动作按钮
        action_btn_layout = QHBoxLayout()
        self.add_action_btn = QPushButton("添加动作")
        self.add_action_btn.clicked.connect(self.add_action)
        self.edit_action_btn = QPushButton("编辑")
        self.edit_action_btn.clicked.connect(self.edit_action)
        self.remove_action_btn = QPushButton("删除")
        self.remove_action_btn.clicked.connect(self.remove_action)
        self.move_up_btn = QPushButton("上移")
        self.move_up_btn.clicked.connect(self.move_action_up)
        self.move_down_btn = QPushButton("下移")
        self.move_down_btn.clicked.connect(self.move_action_down)
        
        action_btn_layout.addWidget(self.add_action_btn)
        action_btn_layout.addWidget(self.edit_action_btn)
        action_btn_layout.addWidget(self.remove_action_btn)
        action_btn_layout.addWidget(self.move_up_btn)
        action_btn_layout.addWidget(self.move_down_btn)
        
        action_layout.addWidget(self.action_list)
        action_layout.addLayout(action_btn_layout)
        action_group.setLayout(action_layout)
        
        layout.addWidget(action_group)
        
        # 按钮
        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self.validate_and_accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
    
    def load_sequence(self):
        """加载序列"""
        if self.sequence:
            self.name_input.setText(self.sequence.get('name', ''))
            self.refresh_action_list()
    
    def add_action(self):
        """添加动作"""
        dialog = ActionEditDialog(self.controller, self)
        if dialog.exec():
            action = dialog.get_action()
            if action:
                self.actions.append(action)
                self.refresh_action_list()
    
    def edit_action(self):
        """编辑动作"""
        current = self.action_list.currentRow()
        if current >= 0 and current < len(self.actions):
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
    
    def move_action_up(self):
        """上移动作"""
        current = self.action_list.currentRow()
        if current > 0:
            self.actions[current], self.actions[current-1] = self.actions[current-1], self.actions[current]
            self.refresh_action_list()
            self.action_list.setCurrentRow(current - 1)
    
    def move_action_down(self):
        """下移动作"""
        current = self.action_list.currentRow()
        if current >= 0 and current < len(self.actions) - 1:
            self.actions[current], self.actions[current+1] = self.actions[current+1], self.actions[current]
            self.refresh_action_list()
            self.action_list.setCurrentRow(current + 1)
    
    def refresh_action_list(self):
        """刷新动作列表"""
        self.action_list.clear()
        for i, action in enumerate(self.actions, 1):
            # 使用父窗口的format_action_text方法
            if hasattr(self.parent(), 'format_action_text'):
                text = f"{i}. {self.parent().format_action_text(action)}"
            else:
                text = f"{i}. {action.get('type', 'unknown')}"
            self.action_list.addItem(text)
    
    def validate_and_accept(self):
        """验证并接受"""
        if not self.name_input.text():
            QMessageBox.warning(self, "警告", "请输入序列名称")
            return
        if not self.actions:
            QMessageBox.warning(self, "警告", "请添加至少一个动作")
            return
        self.accept()
    
    def get_action_sequence(self):
        """获取动作序列"""
        return {
            'name': self.name_input.text(),
            'actions': self.actions
        }


