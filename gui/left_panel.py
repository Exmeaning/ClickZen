"""左侧面板 - 设备管理和Scrcpy控制"""
from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *
from utils.config import VERSION


class LeftPanel(QWidget):
    """左侧面板：设备连接、Scrcpy控制、模拟器模式"""
    
    # 信号定义
    start_scrcpy_clicked = pyqtSignal()
    stop_scrcpy_clicked = pyqtSignal()
    refresh_devices_clicked = pyqtSignal()
    # 模拟器模式信号
    simulator_window_selected = pyqtSignal(int, tuple, str)  # hwnd, crop_rect, window_title
    simulator_mode_changed = pyqtSignal(bool)  # is_simulator_mode
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.parent = parent
        # 模拟器模式状态
        self.current_mode = 'device'  # 'device' 或 'simulator'
        self.simulator_hwnd = None
        self.simulator_crop_rect = None
        self.simulator_window_title = None
        self.init_ui()
        
    def init_ui(self):
        """初始化UI"""
        layout = QVBoxLayout(self)
        layout.setSpacing(15)
        
        # 1. 顶部标题区域
        title_widget = self.create_title_widget()
        layout.addWidget(title_widget)
        
        # 2. 设备管理区域
        self.device_widget = self.create_device_widget()
        layout.addWidget(self.device_widget)
        
        # 3. 无线连接区域
        self.wireless_widget = self.create_wireless_widget()
        layout.addWidget(self.wireless_widget)
        
        # 弹性空间
        layout.addStretch()
        
        # 5. 模式选择器
        mode_widget = self.create_mode_selector()
        layout.addWidget(mode_widget)
        
        # 6. 底部大按钮
        self.scrcpy_btn = self.create_scrcpy_button()
        layout.addWidget(self.scrcpy_btn)
        
        # 7. 模拟器模式状态显示
        self.simulator_status_widget = self.create_simulator_status_widget()
        self.simulator_status_widget.setVisible(False)
        layout.addWidget(self.simulator_status_widget)
        
        # 8. 自动重启选项
        auto_restart_widget = self.create_auto_restart_widget()
        layout.addWidget(auto_restart_widget)
        
        # 设置样式
        self.setStyleSheet("""
            QGroupBox {
                font-size: 13px;
                font-weight: bold;
                border: 2px solid #e0e0e0;
                border-radius: 8px;
                margin-top: 10px;
                padding-top: 10px;
            }
            QGroupBox::title {
                subcontrol-origin: margin;
                left: 10px;
                padding: 0 10px 0 10px;
            }
        """)
        
    def create_title_widget(self):
        """创建标题区域"""
        widget = QWidget()
        layout = QVBoxLayout(widget)
        layout.setSpacing(8)
        
        # ClickZen大标题
        title_label = QLabel("ClickZen")
        title_label.setStyleSheet("""
            QLabel {
                font-size: 36px;
                font-weight: bold;
                color: #424242;
                padding: 10px 0;
            }
        """)
        title_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        
        # 版本信息
        version_widget = QWidget()
        version_layout = QVBoxLayout(version_widget)
        version_layout.setSpacing(5)
        
        # Scrcpy版本
        self.scrcpy_version_label = QLabel(f"Scrcpy v3.3.3")
        self.scrcpy_version_label.setStyleSheet("color: #666; font-size: 12px;")
        self.scrcpy_version_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        
        # ClickZen版本
        self.clickzen_version_label = QLabel(f"ClickZen v{VERSION}")
        self.clickzen_version_label.setStyleSheet("color: #666; font-size: 12px;")
        self.clickzen_version_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        
        # GitHub链接
        self.github_label = QLabel(
            '<a href="https://github.com/Exmeaning/ClickZen" style="color: #757575;">🔗 GitHub</a>'
        )
        self.github_label.setOpenExternalLinks(True)
        self.github_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.github_label.setStyleSheet("font-size: 12px;")
        
        # 版本检测标签
        self.version_check_label = QLabel("检查更新中...")
        self.version_check_label.setStyleSheet("color: #FF9800; font-size: 11px;")
        self.version_check_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        
        version_layout.addWidget(self.scrcpy_version_label)
        version_layout.addWidget(self.clickzen_version_label)
        version_layout.addWidget(self.github_label)
        version_layout.addWidget(self.version_check_label)
        
        layout.addWidget(title_label)
        layout.addWidget(version_widget)
        
        # 分隔线
        line = QFrame()
        line.setFrameShape(QFrame.Shape.HLine)
        line.setStyleSheet("background-color: #e0e0e0;")
        layout.addWidget(line)
        
        return widget
        
    def create_device_widget(self):
        """创建设备管理区域"""
        group = QGroupBox("📱 设备管理")
        layout = QVBoxLayout()
        layout.setSpacing(10)
        
        # 设备选择下拉框
        self.device_combo = QComboBox()
        self.device_combo.setMinimumHeight(40)
        self.device_combo.setStyleSheet("""
            QComboBox {
                font-size: 14px;
                padding: 8px;
                border: 2px solid #9E9E9E;
                border-radius: 6px;
            }
            QComboBox:hover {
                border-color: #757575;
            }
        """)
        
        # 刷新按钮
        self.refresh_btn = QPushButton("🔄 刷新设备列表")
        self.refresh_btn.setMinimumHeight(45)
        self.refresh_btn.clicked.connect(self.refresh_devices_clicked.emit)
        
        # USB提示
        tip_label = QLabel("💡 USB连接更稳定，推荐优先使用")
        tip_label.setStyleSheet("color: #666; font-size: 11px; padding: 5px;")
        tip_label.setWordWrap(True)
        
        layout.addWidget(QLabel("选择设备:"))
        layout.addWidget(self.device_combo)
        layout.addWidget(self.refresh_btn)
        layout.addWidget(tip_label)
        
        group.setLayout(layout)
        return group
        
    def create_wireless_widget(self):
        """创建无线连接区域"""
        group = QGroupBox("📡 无线连接")
        layout = QVBoxLayout()
        layout.setSpacing(10)
        
        # 常见模拟器端口
        self.emulators = [
            ("选择模拟器快速连接...", ""),
            ("网易MuMu模拟器", "7555"),
            ("夜神安卓模拟器", "62001"),
            ("逍遥安卓模拟器", "21503"),
            ("蓝叠安卓模拟器", "5555"),
            ("雷电安卓模拟器", "5555"),
            ("天天安卓模拟器", "5037"),
            ("安卓模拟器大师", "54001"),
            ("腾讯手游助手", "5555"),
        ]
        
        # 模拟器快速连接
        self.emulator_combo = QComboBox()
        self.emulator_combo.setMinimumHeight(35)
        for name, port in self.emulators:
            self.emulator_combo.addItem(name, port)
        self.emulator_combo.currentIndexChanged.connect(self.on_emulator_selected)
        
        # 快速连接
        self.saved_devices_combo = QComboBox()
        self.saved_devices_combo.setMinimumHeight(35)
        self.saved_devices_combo.addItem("选择已保存设备...")
        
        # 连接按钮
        btn_layout = QHBoxLayout()
        self.connect_btn = QPushButton("连接")
        self.connect_btn.setMinimumHeight(35)
        
        self.disconnect_btn = QPushButton("断开")
        self.disconnect_btn.setMinimumHeight(35)
        
        btn_layout.addWidget(self.connect_btn)
        btn_layout.addWidget(self.disconnect_btn)
        
        # 手动输入
        self.ip_input = QLineEdit()
        self.ip_input.setPlaceholderText("输入IP:端口 (如: 192.168.1.100:5555)")
        self.ip_input.setMinimumHeight(35)
        
        # 配对按钮
        self.pair_btn = QPushButton("🔐 配对新设备 (Android 11+)")
        self.pair_btn.setMinimumHeight(40)
        
        layout.addWidget(self.emulator_combo)
        layout.addWidget(self.saved_devices_combo)
        layout.addLayout(btn_layout)
        layout.addWidget(self.ip_input)
        layout.addWidget(self.pair_btn)
        
        group.setLayout(layout)
        return group

    def on_emulator_selected(self, index):
        """模拟器选择改变"""
        if index > 0:
            port = self.emulator_combo.currentData()
            if port:
                self.ip_input.setText(f"127.0.0.1:{port}")
                # 提示用户
                QToolTip.showText(
                    self.emulator_combo.mapToGlobal(QPoint(0, 0)),
                    f"已自动填入端口 {port}，请点击[连接]",
                    self.emulator_combo
                )
        
    def create_scrcpy_button(self):
        """创建Scrcpy控制大按钮"""
        btn = QPushButton("🚀 启动 Scrcpy")
        btn.setMinimumHeight(80)
        btn.setCheckable(True)
        btn.setStyleSheet("""
            QPushButton {
                font-size: 24px;
                font-weight: bold;
                color: white;
                background-color: #4CAF50;
                border: none;
                border-radius: 10px;
            }
            QPushButton:hover {
                background-color: #45a049;
            }
            QPushButton:checked {
                background-color: #f44336;
            }
            QPushButton:checked:hover {
                background-color: #da190b;
            }
        """)
        
        # 连接信号
        btn.toggled.connect(self.on_scrcpy_toggled)
        
        return btn
        
    def create_auto_restart_widget(self):
        """创建自动重启选项"""
        widget = QWidget()
        layout = QHBoxLayout(widget)
        layout.setContentsMargins(0, 5, 0, 0)
        
        self.auto_restart_check = QCheckBox("🔄 断连自动重启")
        self.auto_restart_check.setChecked(True)
        self.auto_restart_check.setToolTip("检测到Scrcpy断连时自动尝试重启")
        
        layout.addWidget(self.auto_restart_check)
        layout.addStretch()
        
        return widget
        
    def on_scrcpy_toggled(self, checked):
        """Scrcpy按钮切换"""
        if self.current_mode == 'simulator':
            # 模拟器模式下点击按钮
            if checked:
                self.scrcpy_btn.setChecked(False)  # 取消选中状态
                self.open_window_selector()
        else:
            # 设备模式
            if checked:
                self.scrcpy_btn.setText("⏹ 停止 Scrcpy")
                self.start_scrcpy_clicked.emit()
            else:
                self.scrcpy_btn.setText("🚀 启动 Scrcpy")
                self.stop_scrcpy_clicked.emit()
    
    def create_mode_selector(self):
        """创建模式选择器"""
        group = QGroupBox("🎮 操作模式")
        layout = QVBoxLayout()
        layout.setSpacing(8)
        
        self.mode_combo = QComboBox()
        self.mode_combo.setMinimumHeight(40)
        self.mode_combo.addItem("📱 设备模式 (Scrcpy)", "device")
        self.mode_combo.addItem("🖥️ 模拟器模式", "simulator")
        self.mode_combo.setStyleSheet("""
            QComboBox {
                font-size: 14px;
                padding: 8px;
                border: 2px solid #9E9E9E;
                border-radius: 6px;
            }
            QComboBox:hover {
                border-color: #757575;
            }
        """)
        self.mode_combo.currentIndexChanged.connect(self.on_mode_changed)
        
        tip_label = QLabel("💡 模拟器模式可捕获任意窗口")
        tip_label.setStyleSheet("color: #666; font-size: 11px;")
        tip_label.setWordWrap(True)
        
        layout.addWidget(self.mode_combo)
        layout.addWidget(tip_label)
        group.setLayout(layout)
        return group
    
    def create_simulator_status_widget(self):
        """创建模拟器状态显示"""
        widget = QWidget()
        layout = QVBoxLayout(widget)
        layout.setContentsMargins(0, 5, 0, 0)
        layout.setSpacing(5)
        
        self.simulator_status_label = QLabel("未选择窗口")
        self.simulator_status_label.setStyleSheet("""
            QLabel {
                color: #666;
                font-size: 12px;
                padding: 8px;
                background-color: #f0f0f0;
                border-radius: 4px;
            }
        """)
        self.simulator_status_label.setWordWrap(True)
        
        # 重新设置按钮
        reset_btn = QPushButton("🔄 重新选择窗口")
        reset_btn.setMinimumHeight(35)
        reset_btn.clicked.connect(self.open_window_selector)
        
        layout.addWidget(self.simulator_status_label)
        layout.addWidget(reset_btn)
        
        return widget
    
    def on_mode_changed(self, index):
        """模式切换"""
        mode = self.mode_combo.currentData()
        self.current_mode = mode
        
        if mode == 'simulator':
            # 模拟器模式
            self.scrcpy_btn.setText("🖥️ 选择窗口")
            self.scrcpy_btn.setChecked(False)
            self.scrcpy_btn.setStyleSheet("""
                QPushButton {
                    font-size: 24px;
                    font-weight: bold;
                    color: white;
                    background-color: #2196F3;
                    border: none;
                    border-radius: 10px;
                }
                QPushButton:hover {
                    background-color: #1976D2;
                }
            """)
            self.simulator_status_widget.setVisible(True)
            self.auto_restart_check.setVisible(False)
            
            # 隐藏设备相关控件
            if hasattr(self, 'device_widget'):
                self.device_widget.setVisible(False)
            # if hasattr(self, 'wireless_widget'):
            #     self.wireless_widget.setVisible(False)
                
            self.simulator_mode_changed.emit(True)
        else:
            # 设备模式
            self.scrcpy_btn.setText("🚀 启动 Scrcpy")
            self.scrcpy_btn.setChecked(False)
            self.scrcpy_btn.setStyleSheet("""
                QPushButton {
                    font-size: 24px;
                    font-weight: bold;
                    color: white;
                    background-color: #4CAF50;
                    border: none;
                    border-radius: 10px;
                }
                QPushButton:hover {
                    background-color: #45a049;
                }
                QPushButton:checked {
                    background-color: #f44336;
                }
                QPushButton:checked:hover {
                    background-color: #da190b;
                }
            """)
            self.simulator_status_widget.setVisible(False)
            self.auto_restart_check.setVisible(True)
            
            # 显示设备相关控件
            if hasattr(self, 'device_widget'):
                self.device_widget.setVisible(True)
            if hasattr(self, 'wireless_widget'):
                self.wireless_widget.setVisible(True)
                
            self.simulator_mode_changed.emit(False)
    
    def open_window_selector(self):
        """打开窗口选择器"""
        from gui.window_selector_dialog import WindowSelectorDialog
        from gui.crop_dialog import CropDialog
        
        # 窗口选择
        selector = WindowSelectorDialog(self)
        if selector.exec():
            hwnd, title = selector.get_selected_window()
            if hwnd:
                # 裁剪设置
                crop_dialog = CropDialog(hwnd, title, self)
                if crop_dialog.exec():
                    crop_rect = crop_dialog.get_crop_rect()
                    if crop_rect:
                        self.simulator_hwnd = hwnd
                        self.simulator_crop_rect = crop_rect
                        self.simulator_window_title = title
                        
                        # 更新状态显示
                        x, y, w, h = crop_rect
                        self.simulator_status_label.setText(
                            f"✓ 窗口: {title[:30]}...\n"
                            f"裁剪区域: ({x}, {y}) {w}x{h}"
                        )
                        self.simulator_status_label.setStyleSheet("""
                            QLabel {
                                color: #2E7D32;
                                font-size: 12px;
                                padding: 8px;
                                background-color: #E8F5E9;
                                border: 1px solid #4CAF50;
                                border-radius: 4px;
                            }
                        """)
                        
                        # 发射信号
                        self.simulator_window_selected.emit(hwnd, crop_rect, title)