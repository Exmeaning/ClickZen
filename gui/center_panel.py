"""中间面板 - 操作录制和智能监控"""
from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *
from utils.theme import theme


class CenterPanel(QWidget):
    """中间面板：操作录制、智能监控"""
    
    # 信号定义
    recording_toggled = pyqtSignal(bool)
    play_recording = pyqtSignal()
    stop_playing = pyqtSignal()
    monitor_toggled = pyqtSignal(bool)
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.parent = parent
        self.init_ui()
        
    def init_ui(self):
        """初始化UI"""
        layout = QVBoxLayout(self)
        layout.setSpacing(10)
        
        # 1. 精简的操作录制区域
        record_widget = self.create_record_widget()
        layout.addWidget(record_widget)
        
        # 2. 智能监控区域（合并后的两栏布局）
        monitor_widget = self.create_monitor_widget()
        layout.addWidget(monitor_widget, 1)  # 让监控区域占据主要空间
        
        # 应用主题（主题切换时会自动重新调用）
        theme.register(self.apply_theme)

    def apply_theme(self):
        """按当前主题刷新本面板的所有样式"""
        t = theme.colors
        self.setStyleSheet(f"""
            QGroupBox {{
                font-size: 13px;
                font-weight: bold;
                border: 2px solid {t['border']};
                border-radius: 8px;
                margin-top: 10px;
                padding-top: 10px;
            }}
            QGroupBox::title {{
                subcontrol-origin: margin;
                left: 10px;
                padding: 0 10px 0 10px;
            }}
        """)
        self._apply_record_theme()
        self._apply_monitor_theme()

    def set_monitor_status(self, text=None, tone="idle"):
        """更新监控状态文字

        Args:
            text: 新的状态文字，None 表示不改变
            tone: idle（普通灰）/ running（成功绿）
        """
        if text is not None:
            self.monitor_status_label.setText(text)
        self._monitor_tone = tone
        t = theme.colors
        color = t['success_text'] if tone == "running" else t['text_secondary']
        self.monitor_status_label.setStyleSheet(
            f"font-size: 12px; color: {color}; padding: 4px;"
        )

    def _apply_record_theme(self):
        """操作录制区域配色"""
        t = theme.colors
        self.record_info_label.setStyleSheet(
            f"font-size: 12px; color: {t['text_secondary']}; padding: 4px;"
        )
        self.action_list.setStyleSheet(f"""
            QListWidget {{
                font-size: 11px;
                border: 1px solid {t['border']};
                border-radius: 4px;
                background-color: {t['surface_alt']};
                color: {t['text']};
            }}
            QListWidget::item {{
                padding: 3px;
                border-bottom: 1px solid {t['border_subtle']};
            }}
            QListWidget::item:selected {{
                background-color: {t['selection_bg']};
                color: {t['selection_text']};
            }}
        """)
        self.record_mode_combo.setStyleSheet(f"""
            QComboBox {{
                font-size: 12px;
                padding: 4px;
                border: 1px solid {t['accent']};
                border-radius: 4px;
                background-color: {t['input_bg']};
                color: {t['text']};
            }}
        """)
        self.record_btn.setStyleSheet(f"""
            QPushButton {{
                font-size: 13px;
                font-weight: bold;
                color: {t['accent_text']};
                background-color: {t['neutral']};
                border: none;
                border-radius: 5px;
            }}
            QPushButton:hover {{
                background-color: {t['neutral_hover']};
            }}
            QPushButton:checked {{
                background-color: {t['neutral_checked']};
            }}
        """)

    def _apply_monitor_theme(self):
        """智能监控区域配色"""
        t = theme.colors
        self.monitor_btn.setStyleSheet(f"""
            QPushButton {{
                font-size: 14px;
                font-weight: bold;
                color: {t['accent_text']};
                background-color: {t['neutral']};
                border: none;
                border-radius: 6px;
            }}
            QPushButton:hover {{
                background-color: {t['neutral_hover']};
            }}
            QPushButton:checked {{
                background-color: {t['neutral_checked']};
            }}
        """)
        self.interval_spin.setStyleSheet(f"""
            QDoubleSpinBox {{
                font-size: 12px;
                padding: 4px;
                border: 1px solid {t['border_strong']};
                border-radius: 4px;
                background-color: {t['input_bg']};
                color: {t['text']};
            }}
        """)
        self.task_list_label.setStyleSheet(
            f"font-size: 12px; color: {t['text_secondary']}; padding: 4px;"
        )
        self.task_label.setStyleSheet(
            f"font-size: 12px; font-weight: bold; color: {t['text_secondary']}; margin-top: 8px;"
        )
        self.scheme_label.setStyleSheet(
            f"font-size: 12px; font-weight: bold; color: {t['text_secondary']}; margin-top: 8px;"
        )
        self.monitor_task_list.setStyleSheet(f"""
            QListWidget {{
                font-size: 12px;
                border: 1px solid {t['border']};
                border-radius: 4px;
                padding: 5px;
                background-color: {t['surface_alt']};
            }}
            QListWidget::item {{
                padding: 8px;
                border-bottom: 1px solid {t['border_subtle']};
                background-color: {t['surface_bg']};
                color: {t['text']};
                margin: 2px;
                border-radius: 4px;
            }}
            QListWidget::item:selected {{
                background-color: {t['selection_bg']};
                color: {t['selection_text']};
                border: 1px solid {t['selection_bg']};
            }}
            QListWidget::item:hover {{
                background-color: {t['hover_bg']};
                color: {t['text']};
            }}
        """)
        # 状态文字保持当前色调
        self.set_monitor_status(None, getattr(self, "_monitor_tone", "idle"))
        
    def create_record_widget(self):
        """创建操作录制区域（两栏布局）"""
        group = QGroupBox("🎬 操作录制")
        main_layout = QHBoxLayout()  # 改为水平布局
        main_layout.setSpacing(10)
        
        # 左侧：录制日志
        left_widget = QWidget()
        left_layout = QVBoxLayout(left_widget)
        left_layout.setSpacing(4)
        left_layout.setContentsMargins(0, 0, 5, 0)
        
        # 录制信息
        self.record_info_label = QLabel("未录制")
        
        # 操作列表
        self.action_list = QListWidget()
        
        left_layout.addWidget(self.record_info_label)
        left_layout.addWidget(self.action_list)
        
        # 右侧：录制控制
        right_widget = QWidget()
        right_layout = QVBoxLayout(right_widget)
        right_layout.setSpacing(8)
        right_layout.setContentsMargins(5, 0, 0, 0)
        
        # 录制模式
        mode_layout = QHBoxLayout()
        mode_label = QLabel("模式:")
        self.record_mode_combo = QComboBox()
        self.record_mode_combo.addItems(["窗口录制", "设备录制"])
        self.record_mode_combo.setMinimumHeight(28)
        mode_layout.addWidget(mode_label)
        mode_layout.addWidget(self.record_mode_combo)
        
        # 播放控制
        play_row = QHBoxLayout()
        
        self.play_btn = QPushButton("▶ 播放")
        self.play_btn.setMinimumHeight(28)
        self.play_btn.setEnabled(False)
        
        self.stop_btn = QPushButton("⏹ 停止")
        self.stop_btn.setMinimumHeight(28)
        self.stop_btn.setEnabled(False)
        
        play_row.addWidget(self.play_btn)
        play_row.addWidget(self.stop_btn)
        
        # 速度控制
        speed_row = QHBoxLayout()
        speed_label = QLabel("速度:")
        self.speed_spin = QDoubleSpinBox()
        self.speed_spin.setRange(0.1, 5.0)
        self.speed_spin.setValue(1.0)
        self.speed_spin.setSingleStep(0.1)
        self.speed_spin.setSuffix("x")
        self.speed_spin.setMinimumHeight(26)
        self.speed_spin.setMaximumWidth(70)
        speed_row.addWidget(speed_label)
        speed_row.addWidget(self.speed_spin)
        
        # 文件操作
        file_row = QHBoxLayout()
        self.save_btn = QPushButton("💾 保存")
        self.load_btn = QPushButton("📁 加载")
        for btn in [self.save_btn, self.load_btn]:
            btn.setMinimumHeight(28)
        file_row.addWidget(self.save_btn)
        file_row.addWidget(self.load_btn)
        
        # 随机化设置
        random_group = QGroupBox("随机化")
        random_layout = QVBoxLayout()
        random_layout.setSpacing(4)
        
        self.random_check = QCheckBox("启用")
        self.random_check.setToolTip("使操作更自然")
        
        # 随机参数（紧凑布局）
        param_grid = QGridLayout()
        param_grid.setSpacing(4)
        
        self.position_spin = QDoubleSpinBox()
        self.position_spin.setRange(0, 10)
        self.position_spin.setValue(1.0)
        self.position_spin.setSuffix("%")
        self.position_spin.setMaximumWidth(55)
        
        self.delay_spin = QDoubleSpinBox()
        self.delay_spin.setRange(0, 50)
        self.delay_spin.setValue(20)
        self.delay_spin.setSuffix("%")
        self.delay_spin.setMaximumWidth(55)
        
        self.longpress_spin = QDoubleSpinBox()
        self.longpress_spin.setRange(0, 30)
        self.longpress_spin.setValue(15)
        self.longpress_spin.setSuffix("%")
        self.longpress_spin.setMaximumWidth(55)
        
        param_grid.addWidget(QLabel("位置:"), 0, 0)
        param_grid.addWidget(self.position_spin, 0, 1)
        param_grid.addWidget(QLabel("延迟:"), 1, 0)
        param_grid.addWidget(self.delay_spin, 1, 1)
        param_grid.addWidget(QLabel("长按:"), 2, 0)
        param_grid.addWidget(self.longpress_spin, 2, 1)
        
        random_layout.addWidget(self.random_check)
        random_layout.addLayout(param_grid)
        random_group.setLayout(random_layout)
        
        # 添加到右侧布局
        right_layout.addLayout(mode_layout)
        right_layout.addLayout(play_row)
        right_layout.addLayout(speed_row)
        right_layout.addLayout(file_row)
        right_layout.addWidget(random_group)
        right_layout.addStretch()
        
        # 录制按钮放在最底部
        self.record_btn = QPushButton("⏺ 开始录制")
        self.record_btn.setMinimumHeight(32)
        self.record_btn.setCheckable(True)
        self.record_btn.toggled.connect(self.on_record_toggled)
        right_layout.addWidget(self.record_btn)
        
        # 添加分隔线
        splitter = QSplitter(Qt.Orientation.Horizontal)
        splitter.addWidget(left_widget)
        splitter.addWidget(right_widget)
        splitter.setStretchFactor(0, 1)  # 左侧比例
        splitter.setStretchFactor(1, 1)  # 右侧比例
        
        main_layout.addWidget(splitter)
        
        group.setLayout(main_layout)
        return group
        

        
    def create_monitor_widget(self):
        """创建智能监控区域（两栏布局）"""
        group = QGroupBox("🤖 智能监控")
        main_layout = QHBoxLayout()  # 改为水平布局
        main_layout.setSpacing(10)
        
        # 左侧：监控任务列表
        left_widget = QWidget()
        left_layout = QVBoxLayout(left_widget)
        left_layout.setSpacing(4)
        left_layout.setContentsMargins(0, 0, 5, 0)
        
        # 监控控制按钮
        self.monitor_btn = QPushButton("▶ 开始监控")
        self.monitor_btn.setMinimumHeight(40)
        self.monitor_btn.setCheckable(True)
        self.monitor_btn.toggled.connect(self.on_monitor_toggled)
        
        # 检查间隔
        interval_layout = QHBoxLayout()
        interval_label = QLabel("间隔:")
        self.interval_spin = QDoubleSpinBox()
        self.interval_spin.setRange(0.05, 10)
        self.interval_spin.setValue(0.5)
        self.interval_spin.setSingleStep(0.05)
        self.interval_spin.setSuffix(" 秒")
        self.interval_spin.setMinimumHeight(28)
        self.interval_spin.setMaximumWidth(80)
        interval_layout.addWidget(interval_label)
        interval_layout.addWidget(self.interval_spin)
        interval_layout.addStretch()
        
        # 监控状态
        self.monitor_status_label = QLabel("状态: 已停止")
        
        # 分隔线
        separator1 = QFrame()
        separator1.setFrameShape(QFrame.Shape.HLine)
        separator1.setFrameShadow(QFrame.Shadow.Sunken)
        
        # 任务列表标题
        self.task_list_label = QLabel("监控任务列表:")
        
        # 监控任务列表
        self.monitor_task_list = QListWidget()
        
        left_layout.addWidget(self.task_list_label)
        left_layout.addWidget(self.monitor_task_list)
        
        # 右侧：监控控制
        right_widget = QWidget()
        right_layout = QVBoxLayout(right_widget)
        right_layout.setSpacing(8)
        right_layout.setContentsMargins(5, 0, 0, 0)
        
        # 检查间隔
        interval_layout = QHBoxLayout()
        interval_label = QLabel("间隔:")
        self.interval_spin = QDoubleSpinBox()
        self.interval_spin.setRange(0.05, 10)
        self.interval_spin.setValue(0.5)
        self.interval_spin.setSingleStep(0.05)
        self.interval_spin.setSuffix(" 秒")
        self.interval_spin.setMinimumHeight(28)
        self.interval_spin.setMaximumWidth(80)
        interval_layout.addWidget(interval_label)
        interval_layout.addWidget(self.interval_spin)
        interval_layout.addStretch()
        
        # 监控状态
        self.monitor_status_label = QLabel("状态: 已停止")
        
        # 分隔线
        separator1 = QFrame()
        separator1.setFrameShape(QFrame.Shape.HLine)
        separator1.setFrameShadow(QFrame.Shadow.Sunken)
        
        # 任务管理按钮
        self.task_label = QLabel("任务管理:")
        
        task_btn_layout = QVBoxLayout()
        task_btn_layout.setSpacing(4)

        self.add_task_btn = QPushButton("➕ 添加任务")
        self.edit_task_btn = QPushButton("✏ 编辑任务")
        self.copy_task_btn = QPushButton("📋 复制任务")
        self.remove_task_btn = QPushButton("❌ 删除任务")

        for btn in [self.add_task_btn, self.edit_task_btn, self.copy_task_btn, self.remove_task_btn]:
            btn.setMinimumHeight(28)

        task_btn_layout.addWidget(self.add_task_btn)
        task_btn_layout.addWidget(self.edit_task_btn)
        task_btn_layout.addWidget(self.copy_task_btn)
        task_btn_layout.addWidget(self.remove_task_btn)
        
        # 分隔线
        separator2 = QFrame()
        separator2.setFrameShape(QFrame.Shape.HLine)
        separator2.setFrameShadow(QFrame.Shadow.Sunken)
        
        # 方案管理
        self.scheme_label = QLabel("方案管理:")
        
        scheme_btn_layout = QVBoxLayout()
        scheme_btn_layout.setSpacing(4)
        
        self.save_scheme_btn = QPushButton("💾 保存方案")
        self.load_scheme_btn = QPushButton("📂 加载方案")
        
        for btn in [self.save_scheme_btn, self.load_scheme_btn]:
            btn.setMinimumHeight(28)
        
        scheme_btn_layout.addWidget(self.save_scheme_btn)
        scheme_btn_layout.addWidget(self.load_scheme_btn)
        
        # 添加到右侧布局
        right_layout.addLayout(interval_layout)
        right_layout.addWidget(self.monitor_status_label)
        right_layout.addWidget(separator1)
        right_layout.addWidget(self.task_label)
        right_layout.addLayout(task_btn_layout)
        right_layout.addWidget(separator2)
        right_layout.addWidget(self.scheme_label)
        right_layout.addLayout(scheme_btn_layout)
        right_layout.addStretch()
        
        # 监控控制按钮
        self.monitor_btn = QPushButton("▶ 开始监控")
        self.monitor_btn.setMinimumHeight(40)
        self.monitor_btn.setCheckable(True)
        self.monitor_btn.toggled.connect(self.on_monitor_toggled)
        right_layout.addWidget(self.monitor_btn)
        
        # 添加分隔器使面板可调整大小
        splitter = QSplitter(Qt.Orientation.Horizontal)
        splitter.addWidget(left_widget)
        splitter.addWidget(right_widget)
        splitter.setStretchFactor(0, 2)  # 左侧比例（任务列表更宽）
        splitter.setStretchFactor(1, 1)  # 右侧比例
        
        main_layout.addWidget(splitter)
        
        group.setLayout(main_layout)
        return group
        

        
    def on_record_toggled(self, checked):
        """录制按钮切换"""
        if checked:
            self.record_btn.setText("⏸ 停止录制")
        else:
            self.record_btn.setText("⏺ 开始录制")
        self.recording_toggled.emit(checked)
        
    def on_monitor_toggled(self, checked):
        """监控按钮切换"""
        if checked:
            self.monitor_btn.setText("⏹ 停止监控")
        else:
            self.monitor_btn.setText("▶ 开始监控")
        self.monitor_toggled.emit(checked)