from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *
import sys
import json
from datetime import datetime
import time
from core.auto_monitor import AutoMonitor
from gui.monitor_dialog import MonitorTaskDialog


class MainWindow(QMainWindow):

    def __init__(self, config, adb_manager, scrcpy_manager, controller):
        super().__init__()
        self.config = config
        self.adb = adb_manager
        self.scrcpy = scrcpy_manager
        self.controller = controller
        self.is_recording = False
        # 创建自动监控器
        self.auto_monitor = AutoMonitor(adb_manager, controller)
        self.auto_monitor.match_found.connect(self.on_auto_match_found)
        self.auto_monitor.status_update.connect(self.on_monitor_status_update)
        self.auto_monitor.log_message.connect(self.log)
        self.initUI()
        self.setup_shortcuts()
        self.current_device_coords = (0, 0)
        self.setup_coordinate_tracker()
        self.on_randomization_changed()

    def setup_coordinate_tracker(self):
        """设置坐标追踪器"""
        self.coord_timer = QTimer(self)
        self.coord_timer.timeout.connect(self.update_mouse_coordinates)
        self.coord_timer.start(50)  # 每50ms更新一次

    def save_monitor_scheme(self):
        """保存监控方案"""
        if not self.auto_monitor.monitor_configs:
            QMessageBox.information(self, "提示", "没有监控任务可保存")
            return

        filename, _ = QFileDialog.getSaveFileName(
            self, "保存监控方案", "", "JSON文件 (*.json)"
        )

        if filename:
            if self.auto_monitor.save_scheme(filename):
                QMessageBox.information(self, "成功", "监控方案已保存")

    def load_monitor_scheme(self):
        """加载监控方案"""
        filename, _ = QFileDialog.getOpenFileName(
            self, "加载监控方案", "", "JSON文件 (*.json)"
        )

        if filename:
            if self.auto_monitor.load_scheme(filename):
                self.refresh_monitor_task_list()
                QMessageBox.information(self, "成功", "监控方案已加载")

    def update_mouse_coordinates(self):
        """更新鼠标坐标显示 - 修复设备坐标"""
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

                    # 获取设备分辨率
                    device_width, device_height = self.controller.get_device_resolution()

                    # 判断实际显示方向
                    window_aspect = window_width / window_height if window_height > 0 else 1

                    if window_aspect > 1.3:  # 横屏
                        actual_width = max(device_width, device_height)
                        actual_height = min(device_width, device_height)
                        orientation = "横屏"
                    else:  # 竖屏
                        actual_width = min(device_width, device_height)
                        actual_height = max(device_width, device_height)
                        orientation = "竖屏"

                    # 转换为设备坐标
                    if window_width > 0 and window_height > 0:
                        device_x = int(rel_x * actual_width / window_width)
                        device_y = int(rel_y * actual_height / window_height)

                        # 确保坐标在有效范围内
                        device_x = max(0, min(device_x, actual_width - 1))
                        device_y = max(0, min(device_y, actual_height - 1))

                        self.current_device_coords = (device_x, device_y)
                        self.device_coord_label.setText(f"设备: ({device_x}, {device_y})")
                        self.window_status_label.setText(f"Scrcpy: {orientation} ({actual_width}x{actual_height})")
                    else:
                        self.device_coord_label.setText(f"设备: (-, -)")
                        self.window_status_label.setText(f"Scrcpy: 计算错误")
                else:
                    self.device_coord_label.setText(f"设备: (-, -)")
                    self.window_status_label.setText(f"Scrcpy: 鼠标在窗口外")
            else:
                self.device_coord_label.setText(f"设备: (-, -)")
                self.window_status_label.setText(f"Scrcpy: 未找到窗口")

        except Exception as e:
            self.device_coord_label.setText(f"设备: (-, -)")
            self.window_status_label.setText(f"错误: {str(e)[:30]}")

    def copy_device_coordinates(self):
        """复制设备坐标到剪贴板"""
        from PyQt6.QtWidgets import QApplication
        clipboard = QApplication.clipboard()
        clipboard.setText(f"{self.current_device_coords[0]}, {self.current_device_coords[1]}")
        self.statusBar().showMessage(f"已复制坐标: {self.current_device_coords[0]}, {self.current_device_coords[1]}",
                                     2000)
    def initUI(self):
        self.setWindowTitle("Phone Controller - 手机控制器")
        self.setGeometry(100, 100, 900, 700)
        
        # 设置窗口图标（可选）
        self.setWindowIcon(QIcon())

        # 创建中心部件
        central_widget = QWidget()
        self.setCentralWidget(central_widget)

        # 主布局
        main_layout = QHBoxLayout(central_widget)

        # 左侧控制面板
        left_panel = self.create_left_panel()
        main_layout.addWidget(left_panel, 1)

        # 右侧信息面板
        right_panel = self.create_right_panel()
        main_layout.addWidget(right_panel, 2)

        # 状态栏
        status_bar = self.statusBar()
        status_bar.showMessage("就绪")
        
        # 添加GitHub链接到状态栏
        github_label = QLabel('<a href="https://github.com/Exmeaning/ClickZen">GitHub: ClickZen</a>')
        github_label.setOpenExternalLinks(True)
        github_label.setStyleSheet("margin-right: 10px;")
        status_bar.addPermanentWidget(github_label)

        # 连接信号
        self.scrcpy.started.connect(lambda: self.statusBar().showMessage("Scrcpy已启动"))
        self.scrcpy.stopped.connect(lambda: self.statusBar().showMessage("Scrcpy已停止"))
        self.scrcpy.error.connect(lambda msg: self.statusBar().showMessage(f"错误: {msg}"))
        self.scrcpy.log.connect(self.log)

        # 连接控制器信号
        self.controller.action_recorded.connect(self.on_action_recorded)

    def setup_shortcuts(self):
        """设置快捷键 - 已禁用全局快捷键功能"""
        pass
    def select_template(self):
        filename, _ = QFileDialog.getOpenFileName(self, "选择模板", "", "图片 (*.png *.jpg *.jpeg)")
        if filename:
            self.template_input.setText(filename)

    def on_method_changed(self, method):
        self.controller.matcher.set_method(method)

    def search_template(self):
        template = self.template_input.text()
        if not template:
            QMessageBox.warning(self, "提示", "请先选择模板")
            return

        threshold = self.threshold_spin.value()
        self.search_btn.setText("搜索中...")
        self.search_btn.setEnabled(False)

        # 新线程搜索，避免卡顿
        from threading import Thread
        def search():
            start = time.time()
            result = self.controller.find_template(template, threshold)
            elapsed = time.time() - start

            if result:
                x, y, conf = result
                self.match_result.setText(f"✅ 找到位置: ({x}, {y}) 置信度: {conf:.2%}")
                self.x_input.setValue(x)
                self.y_input.setValue(y)
            else:
                self.match_result.setText("❌ 未找到匹配")
            self.search_btn.setText("🔍 搜索")
            self.search_btn.setEnabled(True)
            self.log(f"搜索耗时: {elapsed:.2f}s")

        Thread(target=search, daemon=True).start()
    def create_left_panel(self):
        panel = QWidget()
        layout = QVBoxLayout(panel)

        # 设备选择
        device_group = QGroupBox("设备管理")
        device_layout = QVBoxLayout()

        self.device_combo = QComboBox()
        self.refresh_btn = QPushButton("刷新设备")
        self.refresh_btn.clicked.connect(self.refresh_devices)

        device_layout.addWidget(QLabel("选择设备:"))
        device_layout.addWidget(self.device_combo)
        device_layout.addWidget(self.refresh_btn)
        device_group.setLayout(device_layout)

        # Scrcpy控制
        scrcpy_group = QGroupBox("Scrcpy控制")
        scrcpy_layout = QVBoxLayout()

        self.start_scrcpy_btn = QPushButton("启动Scrcpy")
        self.start_scrcpy_btn.clicked.connect(self.start_scrcpy)

        self.stop_scrcpy_btn = QPushButton("停止Scrcpy")
        self.stop_scrcpy_btn.clicked.connect(self.stop_scrcpy)
        self.stop_scrcpy_btn.setEnabled(False)
        
        # 版本信息标签
        version = self.config.get("scrcpy_version", "未知")
        self.scrcpy_version_label = QLabel(f"版本: v{version}")
        self.scrcpy_version_label.setStyleSheet("color: gray; font-size: 10px;")

        scrcpy_layout.addWidget(self.start_scrcpy_btn)
        scrcpy_layout.addWidget(self.stop_scrcpy_btn)
        scrcpy_layout.addWidget(self.scrcpy_version_label)
        scrcpy_group.setLayout(scrcpy_layout)
        # 录制控制
        record_group = QGroupBox("操作录制")
        record_layout = QVBoxLayout()
        # 快捷操作
        action_group = QGroupBox("快捷操作")
        action_layout = QGridLayout()

        self.back_btn = QPushButton("返回")
        self.back_btn.clicked.connect(self.controller.press_back)

        self.home_btn = QPushButton("主页")
        self.home_btn.clicked.connect(self.controller.press_home)

        self.recent_btn = QPushButton("最近任务")
        self.recent_btn.clicked.connect(self.controller.press_recent)

        self.screenshot_btn = QPushButton("截图")
        self.screenshot_btn.clicked.connect(self.take_screenshot)

        action_layout.addWidget(self.back_btn, 0, 0)
        action_layout.addWidget(self.home_btn, 0, 1)
        action_layout.addWidget(self.recent_btn, 1, 0)
        action_layout.addWidget(self.screenshot_btn, 1, 1)
        action_group.setLayout(action_layout)
        play_control_layout = QHBoxLayout()

        self.play_btn = QPushButton("播放录制")
        self.play_btn.clicked.connect(self.play_recording)
        self.play_btn.setEnabled(False)

        # 添加停止播放按钮
        self.stop_play_btn = QPushButton("停止播放")
        self.stop_play_btn.clicked.connect(self.stop_playing)
        self.stop_play_btn.setEnabled(False)
        self.stop_play_btn.setStyleSheet("""
            QPushButton:enabled {
                background-color: #ff4444;
                color: white;
            }
        """)

        play_control_layout.addWidget(self.play_btn)
        play_control_layout.addWidget(self.stop_play_btn)
        # 随机化设置组（新增）
        random_group = QGroupBox("随机化设置")
        random_layout = QVBoxLayout()

        # 启用随机化
        self.random_enabled_check = QCheckBox("启用随机化")
        self.random_enabled_check.setChecked(False)
        self.random_enabled_check.toggled.connect(self.on_randomization_changed)

        # 随机化参数
        param_layout = QFormLayout()

        # 位置随机
        self.position_random_spin = QDoubleSpinBox()
        self.position_random_spin.setRange(0, 10)
        self.position_random_spin.setValue(1.0)
        self.position_random_spin.setSingleStep(0.1)
        self.position_random_spin.setSuffix("%")
        self.position_random_spin.valueChanged.connect(self.on_randomization_changed)
        param_layout.addRow("位置偏移:", self.position_random_spin)

        # 延迟随机
        self.delay_random_spin = QDoubleSpinBox()
        self.delay_random_spin.setRange(0, 50)
        self.delay_random_spin.setValue(20)
        self.delay_random_spin.setSingleStep(1)
        self.delay_random_spin.setSuffix("%")
        self.delay_random_spin.valueChanged.connect(self.on_randomization_changed)
        param_layout.addRow("延迟波动:", self.delay_random_spin)

        # 长按随机
        self.longpress_random_spin = QDoubleSpinBox()
        self.longpress_random_spin.setRange(0, 30)
        self.longpress_random_spin.setValue(15)
        self.longpress_random_spin.setSingleStep(1)
        self.longpress_random_spin.setSuffix("%")
        self.longpress_random_spin.valueChanged.connect(self.on_randomization_changed)
        param_layout.addRow("长按波动:", self.longpress_random_spin)

        # 说明文字
        info_label = QLabel("随机化可使操作更自然，避免被检测")
        info_label.setStyleSheet("color: gray; font-size: 10px; margin-top: 5px;")

        random_layout.addWidget(self.random_enabled_check)
        random_layout.addLayout(param_layout)
        random_layout.addWidget(info_label)
        random_group.setLayout(random_layout)

        # 录制控制
        record_group = QGroupBox("操作录制")
        record_layout = QVBoxLayout()

        self.record_btn = QPushButton("开始录制")
        self.record_btn.setCheckable(True)
        self.record_btn.toggled.connect(self.toggle_recording)
        self.record_btn.setStyleSheet("""
            QPushButton:checked {
                background-color: #ff4444;
                color: white;
            }
        """)

        # 播放速度控制
        speed_layout = QHBoxLayout()
        speed_layout.addWidget(QLabel("播放速度:"))
        self.speed_spin = QDoubleSpinBox()
        self.speed_spin.setRange(0.1, 5.0)
        self.speed_spin.setValue(1.0)
        self.speed_spin.setSingleStep(0.1)
        self.speed_spin.setSuffix("x")
        speed_layout.addWidget(self.speed_spin)

        # 保存/加载按钮
        file_layout = QHBoxLayout()
        self.save_btn = QPushButton("保存")
        self.save_btn.clicked.connect(self.save_recording)
        self.load_btn = QPushButton("加载")
        self.load_btn.clicked.connect(self.load_recording)
        file_layout.addWidget(self.save_btn)
        file_layout.addWidget(self.load_btn)

        record_layout.addWidget(self.record_btn)
        record_layout.addLayout(play_control_layout)  # 使用新的布局
        record_layout.addLayout(speed_layout)
        record_layout.addLayout(file_layout)
        record_group.setLayout(record_layout)

        # 添加到主布局
        layout.addWidget(device_group)
        layout.addWidget(scrcpy_group)
        layout.addWidget(action_group)
        layout.addWidget(random_group)
        layout.addWidget(record_group)
        layout.addStretch()

        return panel

    def create_right_panel(self):
        panel = QWidget()
        layout = QVBoxLayout(panel)

        # 实时坐标显示（新增）
        coord_display_group = QGroupBox("实时坐标")
        coord_display_layout = QGridLayout()

        # 屏幕坐标
        self.screen_coord_label = QLabel("屏幕: (-, -)")
        self.screen_coord_label.setStyleSheet("font-family: Consolas; font-size: 11px;")

        # 设备坐标
        self.device_coord_label = QLabel("设备: (-, -)")
        self.device_coord_label.setStyleSheet("font-family: Consolas; font-size: 11px; color: blue;")

        # 窗口状态
        self.window_status_label = QLabel("Scrcpy窗口: 未检测")
        self.window_status_label.setStyleSheet("font-size: 10px; color: gray;")

        # 复制坐标按钮
        copy_layout = QHBoxLayout()
        self.copy_device_coord_btn = QPushButton("复制设备坐标")
        self.copy_device_coord_btn.clicked.connect(self.copy_device_coordinates)
        self.copy_device_coord_btn.setMaximumHeight(25)
        copy_layout.addWidget(self.copy_device_coord_btn)

        coord_display_layout.addWidget(self.screen_coord_label, 0, 0)
        coord_display_layout.addWidget(self.device_coord_label, 1, 0)
        coord_display_layout.addWidget(self.window_status_label, 2, 0)
        coord_display_layout.addLayout(copy_layout, 3, 0)
        coord_display_group.setLayout(coord_display_layout)

        layout.addWidget(coord_display_group)  # 添加到最顶部
        # 录制信息
        record_info_group = QGroupBox("录制信息")
        record_info_layout = QVBoxLayout()

        self.record_info_label = QLabel("未录制")
        self.record_info_label.setStyleSheet("font-size: 12px;")

        self.action_list = QListWidget()
        self.action_list.setMaximumHeight(150)

        record_info_layout.addWidget(self.record_info_label)
        record_info_layout.addWidget(self.action_list)
        record_info_group.setLayout(record_info_layout)

        # 坐标输入
        coord_group = QGroupBox("坐标控制")
        coord_layout = QGridLayout()

        self.x_input = QSpinBox()
        self.x_input.setRange(0, 9999)
        self.x_input.setValue(500)

        self.y_input = QSpinBox()
        self.y_input.setRange(0, 9999)
        self.y_input.setValue(500)

        self.click_coord_btn = QPushButton("点击坐标")
        self.click_coord_btn.clicked.connect(self.click_coordinate)

        coord_layout.addWidget(QLabel("X:"), 0, 0)
        coord_layout.addWidget(self.x_input, 0, 1)
        coord_layout.addWidget(QLabel("Y:"), 0, 2)
        coord_layout.addWidget(self.y_input, 0, 3)
        coord_layout.addWidget(self.click_coord_btn, 1, 0, 1, 4)
        coord_group.setLayout(coord_layout)

        # 文本输入
        text_group = QGroupBox("文本输入")
        text_layout = QVBoxLayout()

        self.text_input = QLineEdit()
        self.text_input.setPlaceholderText("输入要发送的文本...")
        self.text_input.returnPressed.connect(self.send_text)

        self.send_text_btn = QPushButton("发送文本")
        self.send_text_btn.clicked.connect(self.send_text)

        text_layout.addWidget(self.text_input)
        text_layout.addWidget(self.send_text_btn)
        text_group.setLayout(text_layout)
        # 图像识别组
        image_group = QGroupBox("图像识别")
        image_layout = QVBoxLayout()

        # 模板选择
        template_layout = QHBoxLayout()
        self.template_input = QLineEdit()
        self.template_input.setPlaceholderText("选择模板图片...")
        template_btn = QPushButton("选择模板")
        template_btn.clicked.connect(self.select_template)
        template_layout.addWidget(self.template_input)
        template_layout.addWidget(template_btn)

        # 参数
        param_layout = QHBoxLayout()
        self.threshold_spin = QDoubleSpinBox()
        self.threshold_spin.setRange(0.50, 1.00)
        self.threshold_spin.setValue(0.85)
        self.threshold_spin.setSingleStep(0.01)
        self.threshold_spin.setSuffix("")
        param_layout.addWidget(QLabel("容差:"))
        param_layout.addWidget(self.threshold_spin)
        param_layout.addStretch()

        # 方法选择
        method_layout = QHBoxLayout()
        self.method_combo = QComboBox()
        self.method_combo.addItems(["CCOEFF_NORMED (推荐)", "CCORR_NORMED", "SQDIFF_NORMED"])
        self.method_combo.currentTextChanged.connect(self.on_method_changed)
        method_layout.addWidget(QLabel("算法:"))
        method_layout.addWidget(self.method_combo)

        # 搜索按钮
        self.search_btn = QPushButton("🔍 搜索")
        self.search_btn.clicked.connect(self.search_template)

        # 结果显示
        self.match_result = QLabel("未搜索")
        self.match_result.setStyleSheet("color: green; font-weight: bold;")

        image_layout.addLayout(template_layout)
        image_layout.addLayout(param_layout)
        image_layout.addLayout(method_layout)
        image_layout.addWidget(self.search_btn)
        image_layout.addWidget(self.match_result)
        image_group.setLayout(image_layout)
        # 自动监控组（在录制控制组之后添加）
        monitor_group = QGroupBox("自动监控 (类Klickr)")
        monitor_layout = QVBoxLayout()

        # 监控任务列表
        self.monitor_task_list = QListWidget()
        self.monitor_task_list.setMaximumHeight(100)

        # 任务管理按钮
        task_button_layout = QHBoxLayout()
        self.add_task_btn = QPushButton("添加任务")
        self.add_task_btn.clicked.connect(self.add_monitor_task)
        self.edit_task_btn = QPushButton("编辑")
        self.edit_task_btn.clicked.connect(self.edit_monitor_task)
        self.remove_task_btn = QPushButton("删除")
        self.remove_task_btn.clicked.connect(self.remove_monitor_task)
        task_button_layout.addWidget(self.add_task_btn)
        task_button_layout.addWidget(self.edit_task_btn)
        task_button_layout.addWidget(self.remove_task_btn)
        scheme_button_layout = QHBoxLayout()
        self.save_scheme_btn = QPushButton("保存方案")
        self.save_scheme_btn.clicked.connect(self.save_monitor_scheme)
        self.load_scheme_btn = QPushButton("加载方案")
        self.load_scheme_btn.clicked.connect(self.load_monitor_scheme)
        scheme_button_layout.addWidget(self.save_scheme_btn)
        scheme_button_layout.addWidget(self.load_scheme_btn)

        monitor_layout.addLayout(scheme_button_layout)
        # 监控控制
        control_layout = QHBoxLayout()
        self.monitor_start_btn = QPushButton("▶ 开始监控")
        self.monitor_start_btn.setCheckable(True)
        self.monitor_start_btn.toggled.connect(self.toggle_monitoring)
        self.monitor_start_btn.setStyleSheet("""
               QPushButton:checked {
                   background-color: #4CAF50;
                   color: white;
               }
           """)

        # 检查间隔
        interval_layout = QHBoxLayout()
        interval_label = QLabel("检查间隔:")
        interval_label.setToolTip("最小间隔为0.05秒，过小可能影响性能")
        interval_layout.addWidget(interval_label)
        self.interval_spin = QDoubleSpinBox()
        self.interval_spin.setRange(0.05, 10)  # 最小值改为0.05秒
        self.interval_spin.setValue(0.5)
        self.interval_spin.setSingleStep(0.05)
        self.interval_spin.setSuffix(" 秒")
        self.interval_spin.setToolTip("建议不低于0.1秒")
        self.interval_spin.valueChanged.connect(self.on_interval_changed)
        interval_layout.addWidget(self.interval_spin)
        
        # 添加提示标签
        min_interval_label = QLabel("(最小: 0.05秒 过低可能影响性能)")
        min_interval_label.setStyleSheet("color: gray; font-size: 10px;")
        interval_layout.addWidget(min_interval_label)

        # 监控状态
        self.monitor_status_label = QLabel("状态: 已停止")
        self.monitor_status_label.setStyleSheet("color: gray; font-size: 10px;")

        monitor_layout.addWidget(QLabel("监控任务:"))
        monitor_layout.addWidget(self.monitor_task_list)
        monitor_layout.addLayout(task_button_layout)
        monitor_layout.addWidget(self.monitor_start_btn)
        monitor_layout.addLayout(interval_layout)
        monitor_layout.addWidget(self.monitor_status_label)
        monitor_group.setLayout(monitor_layout)

        # 添加到主布局（在record_group之后）
        layout.addWidget(monitor_group)

        # 日志显示
        log_group = QGroupBox("操作日志")
        log_layout = QVBoxLayout()

        self.log_text = QTextEdit()
        self.log_text.setReadOnly(True)

        # 清空日志按钮
        clear_log_btn = QPushButton("清空日志")
        clear_log_btn.clicked.connect(self.log_text.clear)

        log_layout.addWidget(self.log_text)
        log_layout.addWidget(clear_log_btn)
        log_group.setLayout(log_layout)

        # 添加到主布局
        layout.addWidget(record_info_group)
        layout.addWidget(coord_group)
        layout.addWidget(text_group)
        layout.addWidget(log_group, 1)

        return panel

    def add_monitor_task(self):
        """添加监控任务"""
        dialog = MonitorTaskDialog(self.controller, self)
        if dialog.exec():
            config = dialog.get_config()
            if config:
                index = self.auto_monitor.add_monitor_config(config)
                self.refresh_monitor_task_list()
                self.log(f"添加监控任务: {config['name']}")

    def edit_monitor_task(self):
        """编辑监控任务"""
        current = self.monitor_task_list.currentRow()
        if current >= 0 and current < len(self.auto_monitor.monitor_configs):
            config = self.auto_monitor.monitor_configs[current]
            dialog = MonitorTaskDialog(self.controller, self, config)
            if dialog.exec():
                new_config = dialog.get_config()
                if new_config:
                    self.auto_monitor.update_monitor_config(current, new_config)
                    self.refresh_monitor_task_list()
                    self.log(f"更新监控任务: {new_config['name']}")

    def remove_monitor_task(self):
        """删除监控任务"""
        current = self.monitor_task_list.currentRow()
        if current >= 0:
            reply = QMessageBox.question(
                self, "确认", "确定要删除这个监控任务吗？",
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
            )
            if reply == QMessageBox.StandardButton.Yes:
                self.auto_monitor.remove_monitor_config(current)
                self.refresh_monitor_task_list()

    def refresh_monitor_task_list(self):
        """刷新监控任务列表"""
        self.monitor_task_list.clear()
        for config in self.auto_monitor.monitor_configs:
            status = "✓" if config.get('enabled', True) else "✗"
            item_text = f"[{status}] {config['name']}"
            self.monitor_task_list.addItem(item_text)

    def toggle_monitoring(self, checked):
        """切换自动监控状态"""
        if checked:
            if self.auto_monitor.start_monitoring():
                self.monitor_start_btn.setText("■ 停止监控")
                self.log("开始自动监控")
            else:
                self.monitor_start_btn.setChecked(False)
                QMessageBox.warning(self, "警告", "无法启动监控，请检查是否有配置任务")
        else:
            self.auto_monitor.stop_monitoring()
            self.monitor_start_btn.setText("▶ 开始监控")
            self.log("停止自动监控")

    def on_interval_changed(self, value):
        """检查间隔改变"""
        self.auto_monitor.set_check_interval(value)

    def on_auto_match_found(self, match_info):
        """自动匹配找到"""
        config = match_info['config']
        time_str = match_info['time']
        self.log(f"[{time_str}] ✅ 触发任务: {config['name']}")
        # 可以在这里添加声音或其他提示

    def on_monitor_status_update(self, status):
        """监控状态更新"""
        self.monitor_status_label.setText(f"状态: {status}")

    def on_action_recorded(self, action):
        """处理录制的操作"""
        # 更新操作列表
        action_text = ""
        if action['type'] == 'click':
            action_text = f"点击 ({action['x']}, {action['y']})"
        elif action['type'] == 'long_click':
            duration = action.get('duration', 1000)
            action_text = f"长按 ({action['x']}, {action['y']}) {duration}ms"
        elif action['type'] == 'swipe':
            duration = action.get('duration', 300)
            action_text = f"滑动 ({action['x1']}, {action['y1']}) → ({action['x2']}, {action['y2']}) {duration}ms"
        elif action['type'] == 'key':
            action_text = f"按键 {action.get('key_name', action['keycode'])}"
        elif action['type'] == 'text':
            action_text = f"输入文本: {action['text']}"

        if action_text:
            self.action_list.addItem(action_text)
            # 自动滚动到底部
            self.action_list.scrollToBottom()

        # 更新录制信息
        count = len(self.controller.recorded_actions)
        self.record_info_label.setText(f"已录制 {count} 个操作")

    def refresh_devices(self):
        """刷新设备列表"""
        self.log("正在刷新设备列表...")
        devices = self.adb.get_devices()

        self.device_combo.clear()
        for serial, info in devices:
            self.device_combo.addItem(f"{info} ({serial})", serial)

        if devices:
            self.log(f"发现 {len(devices)} 个设备")
        else:
            self.log("未发现设备，请检查USB连接")

    def start_scrcpy(self):
        """启动Scrcpy"""
        if self.device_combo.count() == 0:
            QMessageBox.warning(self, "警告", "请先刷新并选择设备")
            return

        serial = self.device_combo.currentData()
        if not serial:
            QMessageBox.warning(self, "警告", "请先选择设备")
            return

        self.log(f"正在启动Scrcpy...")

        if self.adb.connect_device(serial):
            if self.scrcpy.start(serial):
                self.start_scrcpy_btn.setEnabled(False)
                self.stop_scrcpy_btn.setEnabled(True)
            else:
                QMessageBox.critical(self, "错误", "Scrcpy启动失败")
        else:
            self.log("设备连接失败")

    def stop_scrcpy(self):
        """停止Scrcpy"""
        self.scrcpy.stop()
        self.start_scrcpy_btn.setEnabled(True)
        self.stop_scrcpy_btn.setEnabled(False)
        self.log("Scrcpy已停止")

    def toggle_recording(self, checked=None):
        """切换录制状态"""
        if checked is None:
            checked = not self.is_recording

        if checked:
            # 开始录制
            if self.controller.start_recording():
                self.is_recording = True
                self.record_btn.setChecked(True)
                self.record_btn.setText("停止录制 (F9)")
                self.log("开始录制操作，请在Scrcpy窗口进行操作...")
                self.action_list.clear()
                self.statusBar().showMessage("🔴 正在录制...")
            else:
                QMessageBox.warning(self, "警告", "无法找到Scrcpy窗口，请先启动Scrcpy")
                self.record_btn.setChecked(False)
        else:
            # 停止录制
            actions = self.controller.stop_recording()
            self.is_recording = False
            self.record_btn.setChecked(False)
            self.record_btn.setText("开始录制")
            self.log(f"录制完成，共 {len(actions)} 个操作")
            self.play_btn.setEnabled(len(actions) > 0)
            self.statusBar().showMessage("就绪")

    def on_randomization_changed(self):
        """随机化设置改变"""
        enabled = self.random_enabled_check.isChecked()
        position_range = self.position_random_spin.value() / 100.0  # 转换为小数
        delay_range = self.delay_random_spin.value() / 100.0
        longpress_range = self.longpress_random_spin.value() / 100.0

        # 更新控制器的随机化设置
        self.controller.set_randomization(
            enabled,
            position_range,
            delay_range,
            longpress_range
        )

        # 根据是否启用来启用/禁用参数输入框
        self.position_random_spin.setEnabled(enabled)
        self.delay_random_spin.setEnabled(enabled)
        self.longpress_random_spin.setEnabled(enabled)

        # 记录到日志
        if enabled:
            self.log(f"随机化已启用: 位置±{position_range * 100:.1f}%, "
                     f"延迟±{delay_range * 100:.1f}%, 长按±{longpress_range * 100:.1f}%")
        else:
            self.log("随机化已禁用")

    def play_recording(self):
        """播放录制（使用当前的随机化设置）"""
        if not self.controller.recorded_actions:
            QMessageBox.information(self, "提示", "没有可播放的录制")
            return

        # 禁用播放按钮，启用停止按钮
        self.play_btn.setEnabled(False)
        self.stop_play_btn.setEnabled(True)

        speed = self.speed_spin.value()
        use_random = self.random_enabled_check.isChecked()

        self.log(f"开始播放录制 (速度: {speed}x, 随机化: {'开启' if use_random else '关闭'})...")
        self.statusBar().showMessage("▶ 正在播放...")

        # 在新线程中播放
        from threading import Thread
        def play_thread():
            result = self.controller.play_recording(
                self.controller.recorded_actions, speed, use_random)

            # 播放完成后恢复按钮状态
            self.play_btn.setEnabled(True)
            self.stop_play_btn.setEnabled(False)

            if result:
                self.statusBar().showMessage("播放完成")
            else:
                self.statusBar().showMessage("播放中断或失败")

        thread = Thread(target=play_thread, daemon=True)
        thread.start()

    # 添加停止播放方法
    def stop_playing(self):
        """停止播放"""
        if self.controller.stop_playing():
            self.log("播放已停止")
            self.play_btn.setEnabled(True)
            self.stop_play_btn.setEnabled(False)
            self.statusBar().showMessage("播放已停止")
    def save_recording(self):
        """保存录制"""
        if not self.controller.recorded_actions:
            QMessageBox.information(self, "提示", "没有可保存的录制")
            return

        filename, _ = QFileDialog.getSaveFileName(
            self, "保存录制", "", "JSON文件 (*.json)")

        if filename:
            self.controller.save_recording(filename)
            self.log(f"录制已保存到: {filename}")

    def load_recording(self):
        """加载录制"""
        filename, _ = QFileDialog.getOpenFileName(
            self, "加载录制", "", "JSON文件 (*.json)")

        if filename:
            try:
                actions = self.controller.load_recording(filename)
                self.controller.recorded_actions = actions
                self.log(f"已加载录制: {filename} ({len(actions)} 个操作)")
                self.play_btn.setEnabled(len(actions) > 0)
                self.record_info_label.setText(f"已加载 {len(actions)} 个操作")

                self.action_list.clear()
                for action in actions:
                    self.on_action_recorded(action)

            except Exception as e:
                QMessageBox.critical(self, "错误", f"加载失败: {str(e)}")

    def click_coordinate(self):
        """点击指定坐标"""
        x = self.x_input.value()
        y = self.y_input.value()
        self.controller.click(x, y)
        self.log(f"点击坐标: ({x}, {y})")

    def send_text(self):
        """发送文本"""
        text = self.text_input.text()
        if text:
            self.controller.input_text(text)
            self.log(f"发送文本: {text}")
            self.text_input.clear()

    def take_screenshot(self):
        """截图 - 带HDR提示"""
        # 检查是否可能有HDR问题
        import win32api
        import win32con

        try:
            # 检查显示器色深
            dc = win32api.GetDC(0)
            bits = win32api.GetDeviceCaps(dc, win32con.BITSPIXEL)
            win32api.ReleaseDC(0, dc)

            if bits > 32:  # 可能是HDR
                reply = QMessageBox.question(
                    self, "HDR提示",
                    "检测到您可能在使用HDR显示。\n\n"
                    "如果截图出现问题（全灰或乱码），建议：\n"
                    "1. 临时关闭Windows HDR（设置->显示->HDR）\n"
                    "2. 或直接从设备截图\n\n"
                    "是否继续？",
                    QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
                )
                if reply == QMessageBox.StandardButton.No:
                    return
        except:
            pass

        self.log("正在截图...")
        img = self.controller.screenshot()
        if img:
            from datetime import datetime
            filename = f"screenshot_{datetime.now().strftime('%Y%m%d_%H%M%S')}.png"
            img.save(filename)
            self.log(f"截图保存为 {filename}")
        else:
            self.log("截图失败")

    def log(self, message):
        """添加日志"""
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.log_text.append(f"[{timestamp}] {message}")

    def closeEvent(self, event):
        """关闭事件"""
        if self.scrcpy.is_running():
            self.scrcpy.stop()
        if self.is_recording:
            self.controller.stop_recording()
        if self.auto_monitor.monitoring:
            self.auto_monitor.stop_monitoring()
        event.accept()