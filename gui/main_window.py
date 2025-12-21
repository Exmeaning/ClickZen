from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *
import sys
import json
from datetime import datetime
import time
import urllib.request
from core.auto_monitor import AutoMonitor
from gui.monitor_dialog import MonitorTaskDialog
from gui.settings_dialog import SettingsDialog
from utils.config import VERSION
from gui.device_manager import DeviceManager
from gui.left_panel import LeftPanel
from gui.center_panel import CenterPanel
from gui.right_panel import RightPanel


class MainWindow(QMainWindow):
    # 添加自定义信号
    version_fetched = pyqtSignal(str)

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
        # 连接版本检测信号
        self.version_fetched.connect(self.update_version_label)
        self.current_device_coords = (0, 0)
        # 初始化设备管理器
        self.device_manager = DeviceManager(self, adb_manager)
        # 模拟器模式状态
        self.simulator_mode_active = False
        self.simulator_hwnd = None
        self.simulator_crop_rect = None
        self.simulator_window_title = None
        # 先初始化UI，再设置坐标追踪器
        self.initUI()
        self.setup_coordinate_tracker()
        self.setup_shortcuts()
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
        """更新鼠标坐标显示 - 支持设备模式和模拟器模式"""
        try:
            # 检查UI是否已初始化
            if not hasattr(self, 'screen_coord_label') or not hasattr(self, 'device_coord_label'):
                return
                
            import win32gui

            # 获取鼠标位置
            cursor_pos = win32gui.GetCursorPos()
            self.screen_coord_label.setText(f"屏幕: ({cursor_pos[0]}, {cursor_pos[1]})")

            # 根据模式选择窗口
            if self.simulator_mode_active and self.simulator_hwnd:
                # 模拟器模式
                hwnd = self.simulator_hwnd
                crop_rect = self.simulator_crop_rect
                window_title = self.simulator_window_title or "模拟器"
            else:
                # 设备模式 - 使用WindowCapture查找Scrcpy窗口
                from core.window_capture import WindowCapture
                hwnd = WindowCapture.find_scrcpy_window()
                crop_rect = None
                window_title = "Scrcpy"

            if hwnd:
                # 检查窗口是否有效
                if not win32gui.IsWindow(hwnd) or not win32gui.IsWindowVisible(hwnd):
                    self.device_coord_label.setText(f"设备: (-, -)")
                    self.window_status_label.setText(f"{window_title}: 窗口无效")
                    return
                    
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

                    if self.simulator_mode_active and crop_rect:
                        # 模拟器模式 - 使用裁剪区域
                        cx, cy, cw, ch = crop_rect
                        
                        # 检查是否在裁剪区域内
                        if cx <= rel_x <= cx + cw and cy <= rel_y <= cy + ch:
                            crop_rel_x = rel_x - cx
                            crop_rel_y = rel_y - cy
                            
                            # 获取设备分辨率进行缩放映射
                            device_w, device_h = self.controller.get_device_resolution()
                            
                            if cw > 0 and ch > 0:
                                scale_x = device_w / cw
                                scale_y = device_h / ch
                                
                                device_x = int(crop_rel_x * scale_x)
                                device_y = int(crop_rel_y * scale_y)
                            else:
                                device_x = int(crop_rel_x)
                                device_y = int(crop_rel_y)
                            
                            device_x = max(0, min(device_x, device_w - 1))
                            device_y = max(0, min(device_y, device_h - 1))
                            
                            self.current_device_coords = (device_x, device_y)
                            self.device_coord_label.setText(f"设备: ({device_x}, {device_y})")
                            self.window_status_label.setText(f"模拟器: 裁剪区域 ({cw}x{ch}) -> 设备 ({device_w}x{device_h})")
                        else:
                            self.device_coord_label.setText(f"设备: (-, -)")
                            self.window_status_label.setText(f"模拟器: 鼠标在裁剪区域外")
                    else:
                        # 设备模式 - 原有逻辑
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
                    status_text = "模拟器" if self.simulator_mode_active else "Scrcpy"
                    self.window_status_label.setText(f"{status_text}: 鼠标在窗口外")
            else:
                self.device_coord_label.setText(f"设备: (-, -)")
                if self.simulator_mode_active:
                    self.window_status_label.setText(f"模拟器: 未选择窗口")
                else:
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
        self.setWindowTitle(f"ClickZen - 智能点击助手 v{VERSION}")
        
        # 设置窗口
        screen = QApplication.primaryScreen()
        screen = QApplication.primaryScreen()
        screen_rect = screen.availableGeometry()
        width = int(1280)
        height = int(900)
        self.setGeometry(
            int((screen_rect.width() - width) / 2),
            int((screen_rect.height() - height) / 2),
            width, height
        )
        
        # 设置最小窗口大小
        self.setMinimumSize(1280, 720)
        
        # 设置窗口图标（可选）
        self.setWindowIcon(QIcon())
        
        # 设置现代化样式
        self.setStyleSheet("""
            QMainWindow {
                background-color: #f5f5f5;
            }
            QStatusBar {
                background-color: #37474F;
                color: white;
                font-size: 13px;
            }
            QStatusBar::item {
                border: none;
            }
        """)
        
        # 创建菜单栏
        self.create_menu_bar()

        # 创建中心部件
        central_widget = QWidget()
        central_widget.setStyleSheet("background-color: #f5f5f5;")
        self.setCentralWidget(central_widget)

        # 主布局 - 三栏设计
        main_layout = QHBoxLayout(central_widget)
        main_layout.setContentsMargins(10, 10, 10, 10)
        main_layout.setSpacing(10)

        # 左栏 - 设备和Scrcpy控制
        self.left_panel = LeftPanel(self)
        self.left_panel.setMaximumWidth(400)
        self.left_panel.setMinimumWidth(350)
        
        # 中栏 - 操作录制和智能监控
        self.center_panel = CenterPanel(self)
        self.center_panel.setMinimumWidth(400)
        
        # 右栏 - 坐标显示和日志
        self.right_panel = RightPanel(self)
        self.right_panel.setMinimumWidth(400)
        
        # 添加分隔器使面板可调整大小
        splitter = QSplitter(Qt.Orientation.Horizontal)
        splitter.addWidget(self.left_panel)
        splitter.addWidget(self.center_panel)
        splitter.addWidget(self.right_panel)
        splitter.setStretchFactor(0, 2)  # 左栏比例
        splitter.setStretchFactor(1, 3)  # 中栏比例
        splitter.setStretchFactor(2, 3)  # 右栏比例
        
        main_layout.addWidget(splitter)

        # 状态栏
        status_bar = self.statusBar()
        status_bar.showMessage("就绪")
        
        # 连接面板信号
        self.connect_panel_signals()
        
        # 连接Scrcpy信号
        self.scrcpy.started.connect(lambda: self.statusBar().showMessage("✓ Scrcpy已启动"))
        self.scrcpy.stopped.connect(lambda: self.statusBar().showMessage("■ Scrcpy已停止"))
        self.scrcpy.error.connect(lambda msg: self.statusBar().showMessage(f"✗ 错误: {msg}"))
        self.scrcpy.log.connect(self.log)

        # 连接控制器信号
        self.controller.action_recorded.connect(self.on_action_recorded)
        
        # 连接设备监控器信号（如果存在）
        if hasattr(self.controller, 'device_monitor'):
            self.controller.device_monitor.log_message.connect(self.log)
            self.controller.device_monitor.error_occurred.connect(
                lambda msg: self.log(f"设备监控错误: {msg}", "error")
            )
        
        # 初始化面板引用（兼容旧代码）
        self.setup_widget_references()
        
        # 加载并应用设置
        self.load_and_apply_settings()
        
        # 检查版本
        QTimer.singleShot(1000, self.check_latest_version)

    def create_menu_bar(self):
        """创建菜单栏"""
        menubar = self.menuBar()
        
        # 文件菜单
        file_menu = menubar.addMenu("文件")
        
        # 加载录制
        load_action = QAction("加载录制", self)
        load_action.setShortcut("Ctrl+O")
        load_action.triggered.connect(self.load_recording)
        file_menu.addAction(load_action)
        
        # 保存录制
        save_action = QAction("保存录制", self)
        save_action.setShortcut("Ctrl+S")
        save_action.triggered.connect(self.save_recording)
        file_menu.addAction(save_action)
        
        file_menu.addSeparator()
        
        # 退出
        exit_action = QAction("退出", self)
        exit_action.setShortcut("Ctrl+Q")
        exit_action.triggered.connect(self.close)
        file_menu.addAction(exit_action)
        
        # 工具菜单
        tools_menu = menubar.addMenu("工具")
        
        # 设置
        settings_action = QAction("设置", self)
        settings_action.setShortcut("Ctrl+,")
        settings_action.triggered.connect(self.open_settings)
        tools_menu.addAction(settings_action)
        
        tools_menu.addSeparator()
        
        # 高级监控功能
        advanced_monitor_action = QAction("🌐 高级监控功能", self)
        advanced_monitor_action.triggered.connect(self.open_advanced_monitor)
        tools_menu.addAction(advanced_monitor_action)
        
        tools_menu.addSeparator()
        
        # 截图
        screenshot_action = QAction("截图", self)
        screenshot_action.setShortcut("Ctrl+P")
        screenshot_action.triggered.connect(self.take_screenshot)
        tools_menu.addAction(screenshot_action)
        
        # 帮助菜单
        help_menu = menubar.addMenu("帮助")
        
        # 关于
        about_action = QAction("关于", self)
        about_action.triggered.connect(self.show_about)
        help_menu.addAction(about_action)
        
        # GitHub
        github_action = QAction("GitHub项目", self)
        github_action.triggered.connect(lambda: QDesktopServices.openUrl(
            QUrl("https://github.com/Exmeaning/ClickZen")))
        help_menu.addAction(github_action)

    def open_settings(self):
        """打开设置对话框"""
        dialog = SettingsDialog(self)
        dialog.settings_changed.connect(self.on_settings_changed)
        dialog.exec()
    
    def open_advanced_monitor(self):
        """打开高级监控功能对话框"""
        from gui.advanced_monitor_dialog import AdvancedMonitorDialog
        dialog = AdvancedMonitorDialog(self.auto_monitor, self)
        dialog.exec()
    
    def on_settings_changed(self, settings):
        """设置改变时的处理"""
        # 应用坐标更新间隔
        interval = settings["performance"]["coord_update_interval"]
        self.coord_timer.setInterval(interval)
        
        # 应用日志设置
        max_lines = settings["ui"]["max_log_lines"]
        doc = self.log_text.document()
        doc.setMaximumBlockCount(max_lines)
        
        self.log(f"设置已更新")
    
    def check_latest_version(self):
        """检查GitHub最新版本（使用信号机制）"""
        from threading import Thread
        
        def fetch():
            try:
                req = urllib.request.Request(
                    'https://github.com/Exmeaning/ClickZen/releases/latest',
                    headers={'User-Agent': 'Mozilla/5.0'}  # 添加UA避免被拒绝
                )
                with urllib.request.urlopen(req, timeout=5) as response:
                    final_url = response.geturl()
                    # 从URL提取版本号
                    if '/tag/' in final_url:
                        version = final_url.split('/tag/')[-1]
                        self.version_fetched.emit(version)  # 发射信号
                    else:
                        self.version_fetched.emit('')  # 空字符串表示失败
            except Exception as e:
                # 出错时也发射信号，显示获取失败
                self.version_fetched.emit('')
        
        Thread(target=fetch, daemon=True).start()

    def update_version_label(self, version):
        """更新版本标签（槽函数，自动在主线程执行）"""
        if version:
            text = f'<a href="https://github.com/Exmeaning/ClickZen/releases/latest" style="color: #2196F3;">最新版本: v{version}</a>'
            self.log(f"GitHub最新版本: v{version}", "info")
        else:
            text = f'<span style="color: #999;">版本检测失败</span>'
            self.log("版本检测失败", "warning")
            
        self.left_panel.version_check_label.setText(text)
    
    def load_and_apply_settings(self):
        """加载并应用设置"""
        try:
            import os
            settings_file = "settings.json"
            if os.path.exists(settings_file):
                with open(settings_file, 'r', encoding='utf-8') as f:
                    settings = json.load(f)
                    
                # 应用捕获方法
                from core.window_capture import WindowCapture
                # 默认使用PrintWindow方法
                WindowCapture.enable_log(settings.get("capture", {}).get("debug_log", False))
                
                # 仅在coord_timer存在时应用设置
                if hasattr(self, 'coord_timer'):
                    self.on_settings_changed(settings)
                
                # 自动刷新设备
                if settings.get("ui", {}).get("auto_refresh_devices", False):
                    QTimer.singleShot(500, self.refresh_devices)
        except Exception as e:
            self.log(f"加载设置失败: {str(e)}")
    
    def show_about(self):
        """显示关于对话框"""
        QMessageBox.about(self, "关于 ClickZen",
            f"<h2>ClickZen v{VERSION}</h2>"
            f"<p>智能点击助手 - 自动化控制Android设备</p>"
            f"<p>基于ADB和Scrcpy的开源项目</p>"
            f"<p><a href='https://github.com/Exmeaning/ClickZen'>GitHub项目主页</a></p>"
            f"<p>作者: Exmeaning</p>"
        )

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
            else:
                self.match_result.setText("❌ 未找到匹配")
            self.search_btn.setText("🔍 搜索")
            self.search_btn.setEnabled(True)
            self.log(f"搜索耗时: {elapsed:.2f}s")

        Thread(target=search, daemon=True).start()
    def connect_panel_signals(self):
        """连接各面板的信号"""
        # 左侧面板信号
        self.left_panel.start_scrcpy_clicked.connect(self.start_scrcpy)
        self.left_panel.stop_scrcpy_clicked.connect(self.stop_scrcpy)
        self.left_panel.refresh_devices_clicked.connect(self.refresh_devices)
        
        # 连接无线设备按钮
        self.left_panel.connect_btn.clicked.connect(self.connect_saved_wireless_device)
        self.left_panel.disconnect_btn.clicked.connect(self.disconnect_wireless_device)
        self.left_panel.pair_btn.clicked.connect(self.show_pairing_dialog)
        
        # 中间面板信号
        self.center_panel.recording_toggled.connect(self.toggle_recording)
        self.center_panel.play_btn.clicked.connect(self.play_recording)
        self.center_panel.stop_btn.clicked.connect(self.stop_playing)
        self.center_panel.monitor_toggled.connect(self.toggle_monitoring)
        
        # 文件操作
        self.center_panel.save_btn.clicked.connect(self.save_recording)
        self.center_panel.load_btn.clicked.connect(self.load_recording)
        
        # 监控任务管理
        self.center_panel.add_task_btn.clicked.connect(self.add_monitor_task)
        self.center_panel.edit_task_btn.clicked.connect(self.edit_monitor_task)
        self.center_panel.copy_task_btn.clicked.connect(self.copy_monitor_task)
        self.center_panel.remove_task_btn.clicked.connect(self.remove_monitor_task)
        self.center_panel.save_scheme_btn.clicked.connect(self.save_monitor_scheme)
        self.center_panel.load_scheme_btn.clicked.connect(self.load_monitor_scheme)
        
        # 随机化设置
        self.center_panel.random_check.toggled.connect(self.on_randomization_changed)
        self.center_panel.position_spin.valueChanged.connect(self.on_randomization_changed)
        self.center_panel.delay_spin.valueChanged.connect(self.on_randomization_changed)
        self.center_panel.longpress_spin.valueChanged.connect(self.on_randomization_changed)
        
        # 监控间隔
        self.center_panel.interval_spin.valueChanged.connect(self.on_interval_changed)
        
        # 右侧面板信号
        self.right_panel.adb_command_entered.connect(self.execute_adb_command)
        self.right_panel.copy_coords_clicked.connect(self.copy_device_coordinates)
        self.right_panel.clear_log_btn.clicked.connect(self.clear_log)
        
        # 连接系统快捷键按钮（从左侧移到右侧）
        self.right_panel.back_btn.clicked.connect(self.controller.press_back)
        self.right_panel.home_btn.clicked.connect(self.controller.press_home)
        self.right_panel.recent_btn.clicked.connect(self.controller.press_recent)
        self.right_panel.screenshot_btn.clicked.connect(self.take_screenshot)
        
        # ADB快捷命令
        self.right_panel.activity_btn.clicked.connect(
            lambda: self.quick_adb_command("dumpsys window | grep mCurrentFocus")
        )
        self.right_panel.package_btn.clicked.connect(
            lambda: self.quick_adb_command("pm list packages -3")
        )
        self.right_panel.screen_btn.clicked.connect(
            lambda: self.quick_adb_command("wm size")
        )
        
        # 模拟器模式信号
        self.left_panel.simulator_mode_changed.connect(self.on_simulator_mode_changed)
        self.left_panel.simulator_window_selected.connect(self.on_simulator_window_selected)
        
    def setup_widget_references(self):
        """设置控件引用（兼容旧代码）"""
        # 左侧面板控件
        self.device_combo = self.left_panel.device_combo
        self.refresh_btn = self.left_panel.refresh_btn
        self.wireless_device_combo = self.left_panel.saved_devices_combo
        self.wireless_ip_input = self.left_panel.ip_input
        
        # 中间面板控件
        self.record_mode_combo = self.center_panel.record_mode_combo
        self.record_btn = self.center_panel.record_btn
        self.play_btn = self.center_panel.play_btn
        self.stop_play_btn = self.center_panel.stop_btn
        self.speed_spin = self.center_panel.speed_spin
        self.record_info_label = self.center_panel.record_info_label
        self.action_list = self.center_panel.action_list
        
        self.monitor_task_list = self.center_panel.monitor_task_list
        self.monitor_start_btn = self.center_panel.monitor_btn
        self.monitor_status_label = self.center_panel.monitor_status_label
        self.interval_spin = self.center_panel.interval_spin
        
        self.random_enabled_check = self.center_panel.random_check
        self.position_random_spin = self.center_panel.position_spin
        self.delay_random_spin = self.center_panel.delay_spin
        self.longpress_random_spin = self.center_panel.longpress_spin
        
        # 右侧面板控件
        self.screen_coord_label = self.right_panel.screen_coord_label
        self.device_coord_label = self.right_panel.device_coord_label
        self.window_status_label = self.right_panel.window_status_label
        self.log_text = self.right_panel.log_text
        self.adb_command_input = self.right_panel.adb_input

    def add_monitor_task(self):
        """添加监控任务"""
        dialog = MonitorTaskDialog(self.controller, self)
        if dialog.exec():
            config = dialog.get_config()
            if config:
                index = self.auto_monitor.add_monitor_config(config)
                self.refresh_monitor_task_list()
                self.log(f"添加监控任务: {config['name']}")
    
    def copy_monitor_task(self):
        """复制监控任务"""
        current = self.monitor_task_list.currentRow()
        if current >= 0 and current < len(self.auto_monitor.monitor_configs):
            import copy
            # 深拷贝配置
            original_config = self.auto_monitor.monitor_configs[current]
            config_copy = copy.deepcopy(original_config)
            
            # 修改名称
            original_name = config_copy.get('name', '未命名')
            config_copy['name'] = f"{original_name}_副本"
            
            # 重置执行时间
            if 'last_executed' in config_copy:
                config_copy['last_executed'] = 0
            
            # 添加副本
            self.auto_monitor.add_monitor_config(config_copy)
            self.refresh_monitor_task_list()
            self.log(f"复制监控任务: {original_name} → {config_copy['name']}")
        else:
            QMessageBox.information(self, "提示", "请先选择要复制的任务")

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
                self.log("开始自动监控", "success")
                self.center_panel.monitor_status_label.setText("状态: 监控中...")
                self.center_panel.monitor_status_label.setStyleSheet("color: #4CAF50;")
            else:
                self.center_panel.monitor_btn.setChecked(False)
                QMessageBox.warning(self, "警告", "无法启动监控，请检查是否有配置任务")
        else:
            self.auto_monitor.stop_monitoring()
            self.log("停止自动监控", "info")
            self.center_panel.monitor_status_label.setText("状态: 已停止")
            self.center_panel.monitor_status_label.setStyleSheet("color: #666;")

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
        source = action.get('source', 'unknown')
        source_icon = "📱" if source == 'device' else "🖱️"

        if action['type'] == 'click':
            action_text = f"{source_icon} 点击 ({action['x']}, {action['y']})"
        elif action['type'] == 'long_click':
            duration = action.get('duration', 1000)
            action_text = f"{source_icon} 长按 ({action['x']}, {action['y']}) {duration}ms"
        elif action['type'] == 'swipe':
            duration = action.get('duration', 300)
            action_text = f"{source_icon} 滑动 ({action['x1']}, {action['y1']}) → ({action['x2']}, {action['y2']}) {duration}ms"
        elif action['type'] == 'key':
            action_text = f"{source_icon} 按键 {action.get('key_name', action['keycode'])}"
        elif action['type'] == 'text':
            action_text = f"{source_icon} 输入文本: {action['text']}"

        if action_text:
            self.action_list.addItem(action_text)
            # 自动滚动到底部
            self.action_list.scrollToBottom()

        # 更新录制信息
        count = len(self.controller.recorded_actions)
        mode_text = "设备录制" if source == 'device' else "窗口录制"
        self.record_info_label.setText(f"已录制 {count} 个操作 ({mode_text})")

    def load_saved_wireless_devices(self):
        """加载已保存的无线设备"""
        self.device_manager.load_saved_wireless_devices()

    def save_wireless_device(self, name, ip, port):
        """保存无线设备到设置"""
        return self.device_manager.save_wireless_device(name, ip, port)

    def connect_saved_wireless_device(self):
        """连接已保存的无线设备"""
        self.device_manager.connect_saved_wireless_device()

    def manual_connect_wireless(self):
        """手动连接无线设备"""
        self.device_manager.manual_connect_wireless()

    def disconnect_wireless_device(self):
        """断开所有无线设备"""
        self.device_manager.disconnect_wireless_device()

    def show_pairing_dialog(self):
        """显示配对对话框"""
        self.device_manager.show_pairing_dialog()

    def refresh_devices(self):
        """刷新设备列表"""
        self.device_manager.refresh_devices()

    def start_scrcpy(self):
        """启动Scrcpy"""
        if self.device_combo.count() == 0:
            QMessageBox.warning(self, "警告", "请先刷新并选择设备")
            self.left_panel.scrcpy_btn.setChecked(False)
            return

        serial = self.device_combo.currentData()
        if not serial:
            QMessageBox.warning(self, "警告", "请先选择设备")
            self.left_panel.scrcpy_btn.setChecked(False)
            return

        self.log(f"正在启动Scrcpy...", "info")
        
        # 设置自动重启选项
        self.scrcpy.auto_restart_enabled = self.left_panel.auto_restart_check.isChecked()

        if self.adb.connect_device(serial):
            if self.scrcpy.start(serial):
                self.left_panel.scrcpy_btn.setChecked(True)
                self.log("Scrcpy启动成功", "success")
                if self.scrcpy.auto_restart_enabled:
                    self.log("自动重启已启用", "info")
            else:
                QMessageBox.critical(self, "错误", "Scrcpy启动失败")
                self.left_panel.scrcpy_btn.setChecked(False)
                self.log("Scrcpy启动失败", "error")
        else:
            self.log("设备连接失败", "error")
            self.left_panel.scrcpy_btn.setChecked(False)

    def stop_scrcpy(self):
        """停止Scrcpy"""
        self.scrcpy.stop()
        self.left_panel.scrcpy_btn.setChecked(False)
        self.log("Scrcpy已停止", "info")

    def toggle_recording(self, checked=None):
        """切换录制状态"""
        if checked is None:
            checked = not self.is_recording

        if checked:
            # 获取录制模式
            mode = 'device' if self.record_mode_combo.currentText() == "设备录制" else 'window'

            # 设备录制需要先确保设备已连接
            if mode == 'device':
                # 如果没有连接设备，尝试连接当前选中的设备
                if not self.adb.device_serial:
                    serial = self.device_combo.currentData()
                    if not serial:
                        QMessageBox.warning(self, "警告", "请先选择设备")
                        self.record_btn.setChecked(False)
                        return
                    if not self.adb.connect_device(serial):
                        QMessageBox.warning(self, "警告", "设备连接失败")
                        self.record_btn.setChecked(False)
                        return
                    self.log(f"已连接设备: {serial}")

            self.controller.set_recording_mode(mode)

            # 开始录制
            if self.controller.start_recording():
                self.is_recording = True
                self.record_btn.setChecked(True)
                self.record_btn.setText("停止录制 (F9)")
                self.record_mode_combo.setEnabled(False)  # 录制时禁用模式选择

                if mode == 'device':
                    self.log("开始设备录制，请直接在手机上进行操作...")
                    self.statusBar().showMessage("🔴 正在录制 (设备模式)...")
                else:
                    self.log("开始窗口录制，请在Scrcpy窗口进行操作...")
                    self.statusBar().showMessage("🔴 正在录制 (窗口模式)...")

                self.action_list.clear()
            else:
                if mode == 'window':
                    QMessageBox.warning(self, "警告", "无法找到Scrcpy窗口，请先启动Scrcpy")
                else:
                    QMessageBox.warning(self, "警告", "无法启动设备录制，请检查设备连接")
                self.record_btn.setChecked(False)
        else:
            # 停止录制
            actions = self.controller.stop_recording()
            self.is_recording = False
            self.record_btn.setChecked(False)
            self.record_btn.setText("开始录制")
            self.record_mode_combo.setEnabled(True)  # 恢复模式选择
            self.log(f"录制完成，共 {len(actions)} 个操作")
            self.play_btn.setEnabled(len(actions) > 0)
            self.statusBar().showMessage("就绪")

    def on_randomization_changed(self):
        """随机化设置改变"""
        enabled = self.center_panel.random_check.isChecked()
        position_range = self.center_panel.position_spin.value() / 100.0
        delay_range = self.center_panel.delay_spin.value() / 100.0
        longpress_range = self.center_panel.longpress_spin.value() / 100.0

        # 更新控制器的随机化设置
        self.controller.set_randomization(
            enabled,
            position_range,
            delay_range,
            longpress_range
        )

        # 根据是否启用来启用/禁用参数输入框
        self.center_panel.position_spin.setEnabled(enabled)
        self.center_panel.delay_spin.setEnabled(enabled)
        self.center_panel.longpress_spin.setEnabled(enabled)

        # 记录到日志
        if enabled:
            self.log(f"随机化已启用: 位置±{position_range * 100:.1f}%, "
                     f"延迟±{delay_range * 100:.1f}%, 长按±{longpress_range * 100:.1f}%", "success")
        else:
            self.log("随机化已禁用", "info")

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

    def take_screenshot(self):
        """截图 - 带HDR提示"""
        # 检查是否需要显示HDR警告
        try:
            import os
            show_warning = True
            settings_file = "settings.json"
            if os.path.exists(settings_file):
                with open(settings_file, 'r', encoding='utf-8') as f:
                    settings = json.load(f)
                    show_warning = settings.get("capture", {}).get("show_hdr_warning", True)
                    
            if show_warning:
                from core.window_capture import WindowCapture
                # 只在使用屏幕DC方法时才检查HDR
                if not WindowCapture.get_capture_method():
                    import win32api
                    import win32con
                    
                    # 检查显示器色深
                    dc = win32api.GetDC(0)
                    bits = win32api.GetDeviceCaps(dc, win32con.BITSPIXEL)
                    win32api.ReleaseDC(0, dc)

                    if bits > 32:  # 可能是HDR
                        reply = QMessageBox.question(
                            self, "HDR提示",
                            "检测到您可能在使用HDR显示。\n\n"
                            "如果截图出现问题（全灰或乱码），建议：\n"
                            "1. 在设置中切换到PrintWindow API方法\n"
                            "2. 或临时关闭Windows HDR（设置->显示->HDR）\n\n"
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

    def log(self, message, level="info"):
        """添加日志"""
        self.right_panel.log(message, level)
        
    def clear_log(self):
        """清空日志"""
        self.log_text.clear()

    def execute_adb_command(self, command):
        """执行ADB命令"""
        if not command:
            return
        
        self.log(f"执行ADB命令: {command}")
        result = self.adb.shell(command)
        
        if result:
            # 显示结果（限制长度）
            lines = result.strip().split('\n')
            if len(lines) > 10:
                result_display = '\n'.join(lines[:10]) + f"\n... (共{len(lines)}行)"
            else:
                result_display = result.strip()
            self.log(f"结果:\n{result_display}")
        else:
            self.log("命令执行失败或无返回")

    def on_simulator_mode_changed(self, is_simulator_mode):
        """模拟器模式切换"""
        self.simulator_mode_active = is_simulator_mode
        if is_simulator_mode:
            self.log("已切换到模拟器模式", "info")
            # 配置控制器的监控器为模拟器模式
        else:
            self.log("已切换到设备模式", "info")
            # 清除模拟器配置
            self.simulator_hwnd = None
            self.simulator_crop_rect = None
            self.simulator_window_title = None
            self.simulator_window_title = None
            self.controller.clear_simulator_config()
    
    def on_simulator_window_selected(self, hwnd, crop_rect, window_title):
        """模拟器窗口选择完成"""
        self.simulator_hwnd = hwnd
        self.simulator_crop_rect = crop_rect
        self.simulator_window_title = window_title
        
        # 1. 检查是否有已保存的配置
        saved_config = self.controller.load_simulator_config(window_title)
        
        target_resolution = None
        
        # 如果有保存的配置且CropRect一样(或者用户想直接复用)，这里我们简单处理：
        # 弹出对话框确认，但预填保存的值
        
        default_res = self.controller.get_device_resolution()
        if saved_config:
            if 'resolution' in saved_config:
                default_res = tuple(saved_config['resolution'])
                
        # 2. 弹出配置对话框
        from gui.simulator_config_dialog import SimulatorConfigDialog
        dialog = SimulatorConfigDialog(crop_rect, window_title, default_res, self)
        
        if dialog.exec():
            target_resolution, should_save = dialog.get_result()
            
            # 保存配置
            if should_save:
                self.controller.save_simulator_config(window_title, crop_rect, target_resolution)
        else:
            # 用户取消，使用默认
            target_resolution = default_res
        
        # 3. 配置控制器
        self.controller.set_simulator_config(hwnd, crop_rect, target_resolution)
        
        self.log(f"模拟器窗口已配置: {window_title[:40]}", "success")
        x, y, w, h = crop_rect
        self.log(f"裁剪区域: ({x}, {y}) - {w}x{h}", "info")
        self.log(f"目标分辨率: {target_resolution[0]}x{target_resolution[1]}", "info")
        
        # 更新显示
        self.window_status_label.setText(f"模拟器: {target_resolution[0]}x{target_resolution[1]}")
        
    def quick_adb_command(self, command):
        """快速执行ADB命令"""
        self.adb_command_input.setText(command)
        self.execute_adb_command(command)
    
    def closeEvent(self, event):
        """关闭事件 - 添加保存提示"""
        # 检查是否需要退出确认
        confirm_exit = True
        try:
            import os
            settings_file = "settings.json"
            if os.path.exists(settings_file):
                with open(settings_file, 'r', encoding='utf-8') as f:
                    settings = json.load(f)
                    confirm_exit = settings.get("ui", {}).get("confirm_exit", True)
        except:
            pass
            
        if confirm_exit:
            reply = QMessageBox.question(
                self, "退出确认",
                "请在退出前检查方案是否保存！\n\n确定要退出吗？",
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                QMessageBox.StandardButton.No  # 默认选择"否"
            )
            
            if reply != QMessageBox.StandardButton.Yes:
                event.ignore()
                return
                
        # 清理资源
        if self.scrcpy.is_running():
            self.scrcpy.stop()
        if self.is_recording:
            self.controller.stop_recording()
        if self.auto_monitor.monitoring:
            self.auto_monitor.stop_monitoring()
        event.accept()