"""右侧面板 - 坐标显示、日志和ADB命令"""
from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *
from utils.theme import theme


class RightPanel(QWidget):
    """右侧面板：坐标显示、操作日志、ADB命令"""
    
    # 信号定义
    adb_command_entered = pyqtSignal(str)
    copy_coords_clicked = pyqtSignal()
    root_mode_toggled = pyqtSignal(bool)  # Root 模式切换信号
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.parent = parent
        self.init_ui()
        
    def init_ui(self):
        """初始化UI"""
        layout = QVBoxLayout(self)
        layout.setSpacing(15)
        
        # 1. 坐标显示区域
        coord_widget = self.create_coord_widget()
        layout.addWidget(coord_widget)
        
        # 2. 系统快捷键区域
        action_widget = self.create_action_widget()
        layout.addWidget(action_widget)
        
        # 3. 日志区域（占主要空间）
        log_widget = self.create_log_widget()
        layout.addWidget(log_widget, 1)
        
        # 4. ADB命令输入区域
        adb_widget = self.create_adb_widget()
        layout.addWidget(adb_widget)
        
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
        self._apply_coord_theme()
        self._apply_log_theme()
        self._apply_adb_theme()

    def _apply_coord_theme(self):
        """坐标显示区域配色"""
        t = theme.colors
        self.screen_coord_label.setStyleSheet(f"""
            QLabel {{
                font-family: 'Consolas', 'Monaco', monospace;
                font-size: 18px;
                color: {t['text']};
                padding: 5px;
            }}
        """)
        self.device_coord_label.setStyleSheet(f"""
            QLabel {{
                font-family: 'Consolas', 'Monaco', monospace;
                font-size: 20px;
                font-weight: bold;
                color: {t['text_strong']};
                padding: 5px;
            }}
        """)
        self.window_status_label.setStyleSheet(f"""
            QLabel {{
                font-size: 14px;
                color: {t['text_secondary']};
                padding: 5px;
            }}
        """)

    def _apply_log_theme(self):
        """日志控制台配色（两种主题都保持深色终端风格）"""
        t = theme.colors
        self.log_text.setStyleSheet(f"""
            QTextEdit {{
                font-family: 'Consolas', 'Monaco', monospace;
                font-size: 12px;
                background-color: {t['log_bg']};
                color: {t['log_text']};
                border: 1px solid {t['log_border']};
                border-radius: 6px;
                padding: 10px;
            }}
        """)

    def _apply_adb_theme(self):
        """ADB 命令输入区域配色（Root 模式下使用警告色描边）"""
        t = theme.colors
        root_mode = self.root_check.isChecked()
        self.root_check.setStyleSheet(f"""
            QCheckBox {{
                font-size: 13px;
                font-weight: bold;
                color: {t['warning_text']};
                padding: 4px;
            }}
            QCheckBox:checked {{
                color: {t['warning_text']};
            }}
        """)
        if root_mode:
            self.adb_input.setStyleSheet(f"""
                QLineEdit {{
                    font-size: 14px;
                    padding: 10px;
                    border: 2px solid {t['warning']};
                    border-radius: 6px;
                    background-color: {t['warning_soft_bg']};
                    color: {t['text']};
                }}
                QLineEdit:focus {{
                    border-color: {t['warning_text']};
                    background-color: {t['warning_soft_bg_alt']};
                }}
            """)
        else:
            self.adb_input.setStyleSheet(f"""
                QLineEdit {{
                    font-size: 14px;
                    padding: 10px;
                    border: 2px solid {t['border_strong']};
                    border-radius: 6px;
                    background-color: {t['input_bg']};
                    color: {t['text']};
                }}
                QLineEdit:focus {{
                    border-color: {t['border_focus']};
                    background-color: {t['input_bg_focus']};
                }}
            """)
        self.update_root_status()
        
    def create_coord_widget(self):
        """创建坐标显示区域"""
        group = QGroupBox("📍 当前坐标")
        layout = QVBoxLayout()
        layout.setSpacing(10)
        
        # 屏幕坐标
        self.screen_coord_label = QLabel("屏幕: (0, 0)")
        
        # 设备坐标
        self.device_coord_label = QLabel("设备: (0, 0)")
        
        # 窗口状态
        self.window_status_label = QLabel("Scrcpy窗口: 未检测")
        
        # 复制坐标按钮
        self.copy_btn = QPushButton("📋 复制设备坐标")
        self.copy_btn.setMinimumHeight(40)
        self.copy_btn.clicked.connect(self.copy_coords_clicked.emit)
        
        layout.addWidget(self.screen_coord_label)
        layout.addWidget(self.device_coord_label)
        layout.addWidget(self.window_status_label)
        layout.addWidget(self.copy_btn)
        
        group.setLayout(layout)
        return group
        
    def create_action_widget(self):
        """创建系统快捷键区域"""
        group = QGroupBox("⚡ 系统快捷键")
        layout = QHBoxLayout()
        layout.setSpacing(8)
        
        # 返回按钮
        self.back_btn = QPushButton("◀")
        self.back_btn.setToolTip("返回")
        self.back_btn.setMinimumSize(50, 50)
        self.back_btn.setMaximumHeight(50)
        
        # 主页按钮
        self.home_btn = QPushButton("🏠")
        self.home_btn.setToolTip("主页")
        self.home_btn.setMinimumSize(50, 50)
        self.home_btn.setMaximumHeight(50)
        
        # 最近任务按钮
        self.recent_btn = QPushButton("▣")
        self.recent_btn.setToolTip("最近任务")
        self.recent_btn.setMinimumSize(50, 50)
        self.recent_btn.setMaximumHeight(50)
        
        # 截图按钮
        self.screenshot_btn = QPushButton("📷")
        self.screenshot_btn.setToolTip("截图")
        self.screenshot_btn.setMinimumSize(50, 50)
        self.screenshot_btn.setMaximumHeight(50)
        
        layout.addWidget(self.back_btn)
        layout.addWidget(self.home_btn)
        layout.addWidget(self.recent_btn)
        layout.addWidget(self.screenshot_btn)
        layout.addStretch()  # 添加弹性空间，按钮靠左
        
        group.setLayout(layout)
        return group
        
    def create_log_widget(self):
        """创建日志显示区域"""
        group = QGroupBox("📝 操作日志")
        layout = QVBoxLayout()
        
        # 日志文本框
        self.log_text = QTextEdit()
        self.log_text.setReadOnly(True)
        
        # 设置高亮样式
        self.setup_log_highlighting()
        
        # 清空按钮
        clear_btn_layout = QHBoxLayout()
        clear_btn_layout.addStretch()
        
        self.clear_log_btn = QPushButton("🗑 清空日志")
        self.clear_log_btn.setMinimumHeight(35)
        clear_btn_layout.addWidget(self.clear_log_btn)
        
        layout.addWidget(self.log_text)
        layout.addLayout(clear_btn_layout)
        
        group.setLayout(layout)
        return group
        
    def create_adb_widget(self):
        """创建ADB命令输入区域"""
        widget = QWidget()
        layout = QVBoxLayout(widget)
        layout.setSpacing(8)
        
        # Root 模式切换
        root_layout = QHBoxLayout()
        self.root_check = QCheckBox("🔓 Root 模式")
        self.root_check.setToolTip(
            "启用后，ADB 命令将以 su 权限执行\n"
            "需要设备已 Root 并在 Root 管理器中授权"
        )
        self.root_check.toggled.connect(self._on_root_check_toggled)
        
        # Root 状态指示
        self.root_status_label = QLabel("")
        self._root_status_success = None  # None=无状态 True=成功 False=失败
        
        root_layout.addWidget(self.root_check)
        root_layout.addWidget(self.root_status_label)
        root_layout.addStretch()
        
        # 命令输入框
        input_layout = QHBoxLayout()
        
        self.adb_input = QLineEdit()
        self.adb_input.setPlaceholderText("输入ADB Shell命令... (按Enter执行)")
        self.adb_input.setMinimumHeight(45)
        self.adb_input.returnPressed.connect(self.on_adb_command_entered)
        
        # 执行按钮
        self.execute_btn = QPushButton("▶ 执行")
        self.execute_btn.setMinimumHeight(45)
        self.execute_btn.setMinimumWidth(80)
        self.execute_btn.clicked.connect(self.on_adb_command_entered)
        
        input_layout.addWidget(self.adb_input)
        input_layout.addWidget(self.execute_btn)
        
        # 快捷命令按钮
        shortcut_layout = QHBoxLayout()
        
        self.activity_btn = QPushButton("📱 Activity")
        self.package_btn = QPushButton("📦 包名")
        self.screen_btn = QPushButton("📺 屏幕信息")
        self.root_detect_btn = QPushButton("🔓 Root检测")
        self.root_detect_btn.setToolTip("检测设备是否已 Root")
        
        for btn in [self.activity_btn, self.package_btn, self.screen_btn, self.root_detect_btn]:
            shortcut_layout.addWidget(btn)
        
        shortcut_layout.addStretch()
        
        layout.addLayout(root_layout)
        layout.addLayout(input_layout)
        layout.addLayout(shortcut_layout)
        
        return widget
    
    def _on_root_check_toggled(self, checked):
        """Root 复选框切换"""
        if checked:
            self.adb_input.setPlaceholderText("输入ADB Root Shell命令... (以su权限执行)")
            self._apply_adb_theme()
            self.execute_btn.setText("🔓 执行")
        else:
            self.adb_input.setPlaceholderText("输入ADB Shell命令... (按Enter执行)")
            self._apply_adb_theme()
            self.execute_btn.setText("▶ 执行")
        self.root_mode_toggled.emit(checked)
    
    def set_root_mode(self, enabled):
        """外部设置 Root 模式状态（不触发信号）"""
        self.root_check.blockSignals(True)
        self.root_check.setChecked(enabled)
        # 手动更新 UI 样式
        if enabled:
            self.adb_input.setPlaceholderText("输入ADB Root Shell命令... (以su权限执行)")
            self._apply_adb_theme()
            self.execute_btn.setText("🔓 执行")
        else:
            self.adb_input.setPlaceholderText("输入ADB Shell命令... (按Enter执行)")
            self._apply_adb_theme()
            self.execute_btn.setText("▶ 执行")
        self.root_check.blockSignals(False)
    
    def update_root_status(self, text=None, success=True):
        """更新 Root 状态显示（text 为 None 时只刷新配色）"""
        if text is not None:
            self._root_status_success = success
            prefix = "✓" if success else "✗"
            self.root_status_label.setText(f"{prefix} {text}")

        t = theme.colors
        state = self._root_status_success
        if state is None:
            color = t['text_muted']
        elif state:
            color = t['success_text']
        else:
            color = t['error_text']
        self.root_status_label.setStyleSheet(f"font-size: 11px; color: {color};")
        
    def on_adb_command_entered(self):
        """处理ADB命令输入"""
        command = self.adb_input.text().strip()
        if command:
            self.adb_command_entered.emit(command)
            self.adb_input.clear()
            
    def setup_log_highlighting(self):
        """设置日志高亮"""
        # 这里可以添加语法高亮逻辑
        pass
        
    def log(self, message, level="info"):
        """添加日志"""
        from datetime import datetime
        timestamp = datetime.now().strftime("%H:%M:%S")
        
        # 根据级别设置颜色
        t = theme.colors
        color_map = {
            "info": t['log_text'],
            "success": t['log_success'],
            "warning": t['log_warning'],
            "error": t['log_error']
        }
        
        color = color_map.get(level, t['log_text'])
        
        # 添加HTML格式的日志
        html = f'<span style="color: {t["log_time"]}">[{timestamp}]</span> '
        html += f'<span style="color: {color}">{message}</span>'
        
        cursor = self.log_text.textCursor()
        cursor.movePosition(cursor.MoveOperation.End)
        self.log_text.setTextCursor(cursor)
        self.log_text.insertHtml(html + "<br>")
        self.log_text.ensureCursorVisible()