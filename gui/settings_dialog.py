"""设置对话框"""

from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *
import json
import os


class SettingsDialog(QDialog):
    """设置对话框"""
    
    # 设置改变信号
    settings_changed = pyqtSignal(dict)
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.parent_window = parent
        self.settings_file = "settings.json"
        self.settings = self.load_settings()
        self.initUI()
        self.load_current_settings()
        
    def initUI(self):
        """初始化UI"""
        self.setWindowTitle("设置")
        self.setModal(True)
        self.setMinimumSize(500, 400)
        
        # 主布局
        layout = QVBoxLayout(self)
        
        # 创建选项卡
        tab_widget = QTabWidget()
        
        # 捕获设置选项卡
        capture_tab = self.create_capture_tab()
        tab_widget.addTab(capture_tab, "截图设置")
        
        # 性能设置选项卡
        performance_tab = self.create_performance_tab()
        tab_widget.addTab(performance_tab, "性能设置")
        
        # 录制设置选项卡
        record_tab = self.create_record_tab()
        tab_widget.addTab(record_tab, "录制设置")
        
        # 其他设置选项卡
        other_tab = self.create_other_tab()
        tab_widget.addTab(other_tab, "其他设置")
        
        layout.addWidget(tab_widget)
        
        # 按钮布局
        button_layout = QHBoxLayout()
        
        self.apply_btn = QPushButton("应用")
        self.apply_btn.clicked.connect(self.apply_settings)
        
        self.ok_btn = QPushButton("确定")
        self.ok_btn.clicked.connect(self.accept_settings)
        
        self.cancel_btn = QPushButton("取消")
        self.cancel_btn.clicked.connect(self.reject)
        
        self.reset_btn = QPushButton("恢复默认")
        self.reset_btn.clicked.connect(self.reset_defaults)
        
        button_layout.addWidget(self.reset_btn)
        button_layout.addStretch()
        button_layout.addWidget(self.apply_btn)
        button_layout.addWidget(self.ok_btn)
        button_layout.addWidget(self.cancel_btn)
        
        layout.addLayout(button_layout)
        
    def create_capture_tab(self):
        """创建捕获设置选项卡"""
        widget = QWidget()
        layout = QVBoxLayout(widget)
        
        # 调试选项
        debug_group = QGroupBox("调试选项")
        debug_layout = QVBoxLayout()
        
        self.capture_debug_check = QCheckBox("启用捕获日志")
        self.capture_debug_check.setToolTip("在控制台输出详细的捕获日志")
        
        debug_layout.addWidget(self.capture_debug_check)
        debug_group.setLayout(debug_layout)
        
        layout.addWidget(debug_group)
        layout.addStretch()
        
        return widget
        
    def create_performance_tab(self):
        """创建性能设置选项卡"""
        widget = QWidget()
        layout = QVBoxLayout(widget)
        
        # 监控性能组
        monitor_group = QGroupBox("自动监控")
        monitor_layout = QFormLayout()
        
        self.min_check_interval = QDoubleSpinBox()
        self.min_check_interval.setRange(0.01, 1.0)
        self.min_check_interval.setValue(0.05)
        self.min_check_interval.setSingleStep(0.01)
        self.min_check_interval.setSuffix(" 秒")
        self.min_check_interval.setToolTip("监控最小检查间隔，过小可能影响性能")
        
        monitor_layout.addRow("最小检查间隔:", self.min_check_interval)
        monitor_group.setLayout(monitor_layout)
        
        # 坐标更新组
        coord_group = QGroupBox("坐标追踪")
        coord_layout = QFormLayout()
        
        self.coord_update_interval = QSpinBox()
        self.coord_update_interval.setRange(10, 500)
        self.coord_update_interval.setValue(50)
        self.coord_update_interval.setSingleStep(10)
        self.coord_update_interval.setSuffix(" ms")
        self.coord_update_interval.setToolTip("鼠标坐标更新频率")
        
        coord_layout.addRow("更新间隔:", self.coord_update_interval)
        coord_group.setLayout(coord_layout)
        
        layout.addWidget(monitor_group)
        layout.addWidget(coord_group)
        layout.addStretch()
        
        return widget
        
    def create_record_tab(self):
        """创建录制设置选项卡"""
        widget = QWidget()
        layout = QVBoxLayout(widget)
        
        # 录制选项组
        record_group = QGroupBox("录制选项")
        record_layout = QVBoxLayout()
        
        self.record_mouse_move_check = QCheckBox("记录鼠标移动")
        self.record_mouse_move_check.setToolTip("录制时是否记录鼠标移动轨迹（暂未实现）")
        self.record_mouse_move_check.setEnabled(False)
        
        self.record_timestamps_check = QCheckBox("精确时间戳")
        self.record_timestamps_check.setToolTip("录制操作的精确时间间隔")
        self.record_timestamps_check.setChecked(True)
        
        record_layout.addWidget(self.record_mouse_move_check)
        record_layout.addWidget(self.record_timestamps_check)
        record_group.setLayout(record_layout)
        
        # 默认参数组
        default_group = QGroupBox("默认参数")
        default_layout = QFormLayout()
        
        self.default_play_speed = QDoubleSpinBox()
        self.default_play_speed.setRange(0.1, 10.0)
        self.default_play_speed.setValue(1.0)
        self.default_play_speed.setSingleStep(0.1)
        self.default_play_speed.setSuffix("x")
        
        default_layout.addRow("默认播放速度:", self.default_play_speed)
        default_group.setLayout(default_layout)
        
        layout.addWidget(record_group)
        layout.addWidget(default_group)
        layout.addStretch()
        
        return widget
        
    def create_other_tab(self):
        """创建其他设置选项卡"""
        widget = QWidget()
        layout = QVBoxLayout(widget)
        
        # 无线连接设置组
        wireless_group = QGroupBox("无线ADB设置")
        wireless_layout = QVBoxLayout()
        
        # 常用设备列表
        device_list_layout = QVBoxLayout()
        device_list_label = QLabel("常用无线设备:")
        self.wireless_device_list = QListWidget()
        self.wireless_device_list.setMaximumHeight(100)
        
        # 加载已保存的设备
        saved_devices = self.settings.get("wireless", {}).get("saved_devices", [])
        for device in saved_devices:
            self.wireless_device_list.addItem(f"{device['name']} - {device['ip']}:{device['port']}")
        
        device_list_layout.addWidget(device_list_label)
        device_list_layout.addWidget(self.wireless_device_list)
        
        # 添加/编辑设备
        device_input_layout = QGridLayout()
        
        self.device_name_input = QLineEdit()
        self.device_name_input.setPlaceholderText("设备名称")
        self.device_ip_input = QLineEdit()
        self.device_ip_input.setPlaceholderText("IP地址 (如: 192.168.1.100)")
        self.device_port_input = QLineEdit()
        self.device_port_input.setPlaceholderText("端口 (默认5555)")
        self.device_port_input.setText("5555")
        
        device_input_layout.addWidget(QLabel("名称:"), 0, 0)
        device_input_layout.addWidget(self.device_name_input, 0, 1)
        device_input_layout.addWidget(QLabel("IP:"), 1, 0)
        device_input_layout.addWidget(self.device_ip_input, 1, 1)
        device_input_layout.addWidget(QLabel("端口:"), 2, 0)
        device_input_layout.addWidget(self.device_port_input, 2, 1)
        
        # 按钮
        device_btn_layout = QHBoxLayout()
        self.add_device_btn = QPushButton("添加设备")
        self.add_device_btn.clicked.connect(self.add_wireless_device)
        self.remove_device_btn = QPushButton("删除选中")
        self.remove_device_btn.clicked.connect(self.remove_wireless_device)
        
        device_btn_layout.addWidget(self.add_device_btn)
        device_btn_layout.addWidget(self.remove_device_btn)
        
        # 配对设置
        pair_layout = QFormLayout()
        self.default_pair_port_input = QLineEdit()
        self.default_pair_port_input.setText("5037")
        self.default_pair_port_input.setPlaceholderText("配对端口")
        pair_layout.addRow("默认配对端口:", self.default_pair_port_input)
        
        # 无线调试选项
        self.auto_connect_wireless_check = QCheckBox("启动时自动连接无线设备")
        self.wireless_priority_check = QCheckBox("优先使用无线连接")
        
        # 说明文字
        wireless_info = QLabel(
            "无线ADB使用说明:\n"
            "1. Android 11+: 开发者选项 → 无线调试\n"
            "2. Android 10-: 先USB连接，执行'adb tcpip 5555'"
        )
        wireless_info.setStyleSheet("color: gray; font-size: 10px; margin: 10px;")
        
        wireless_layout.addLayout(device_list_layout)
        wireless_layout.addLayout(device_input_layout)
        wireless_layout.addLayout(device_btn_layout)
        wireless_layout.addLayout(pair_layout)
        wireless_layout.addWidget(self.auto_connect_wireless_check)
        wireless_layout.addWidget(self.wireless_priority_check)
        wireless_layout.addWidget(wireless_info)
        wireless_group.setLayout(wireless_layout)
        
        # 界面设置组
        ui_group = QGroupBox("界面设置")
        ui_layout = QVBoxLayout()
        
        self.auto_refresh_devices_check = QCheckBox("启动时自动刷新设备")
        self.confirm_exit_check = QCheckBox("退出时确认")
        self.confirm_exit_check.setChecked(True)
        
        ui_layout.addWidget(self.auto_refresh_devices_check)
        ui_layout.addWidget(self.confirm_exit_check)
        ui_group.setLayout(ui_layout)
        
        # 日志设置组
        log_group = QGroupBox("日志设置")
        log_layout = QFormLayout()
        
        self.max_log_lines = QSpinBox()
        self.max_log_lines.setRange(100, 10000)
        self.max_log_lines.setValue(1000)
        self.max_log_lines.setSingleStep(100)
        self.max_log_lines.setSuffix(" 行")
        
        log_layout.addRow("最大日志行数:", self.max_log_lines)
        log_group.setLayout(log_layout)
        
        # 开发者调试组
        debug_group = QGroupBox("开发者调试")
        debug_layout = QVBoxLayout()
        
        self.debug_device_events_check = QCheckBox("设备事件详细日志")
        self.debug_device_events_check.setToolTip(
            "输出getevent命令的详细日志\n"
            "包括原始事件数据、解析过程等"
        )
        
        self.debug_adb_commands_check = QCheckBox("ADB命令日志")
        self.debug_adb_commands_check.setToolTip(
            "输出所有ADB命令及其返回结果"
        )
        
        self.debug_touch_events_check = QCheckBox("触摸事件追踪")
        self.debug_touch_events_check.setToolTip(
            "详细追踪触摸事件的状态变化\n"
            "包括按下、移动、抬起等"
        )
        
        self.save_raw_events_check = QCheckBox("保存原始事件数据")
        self.save_raw_events_check.setToolTip(
            "将getevent的原始输出保存到文件\n"
            "用于离线分析"
        )
        
        debug_info = QLabel(
            "⚠️ 开启调试会产生大量日志，可能影响性能\n"
            "建议仅在排查问题时开启"
        )
        debug_info.setStyleSheet("color: orange; font-size: 10px; margin: 10px;")
        
        debug_layout.addWidget(self.debug_device_events_check)
        debug_layout.addWidget(self.debug_adb_commands_check)
        debug_layout.addWidget(self.debug_touch_events_check)
        debug_layout.addWidget(self.save_raw_events_check)
        debug_layout.addWidget(debug_info)
        debug_group.setLayout(debug_layout)
        
        layout.addWidget(wireless_group)  # 添加无线设置组
        layout.addWidget(ui_group)
        layout.addWidget(log_group)
        layout.addWidget(debug_group)
        layout.addStretch()
        
        return widget
    
    def add_wireless_device(self):
        """添加无线设备到列表"""
        name = self.device_name_input.text().strip()
        ip = self.device_ip_input.text().strip()
        port = self.device_port_input.text().strip() or "5555"
        
        if not name or not ip:
            QMessageBox.warning(self, "警告", "请输入设备名称和IP地址")
            return
        
        # 添加到列表显示
        self.wireless_device_list.addItem(f"{name} - {ip}:{port}")
        
        # 清空输入
        self.device_name_input.clear()
        self.device_ip_input.clear()
        self.device_port_input.setText("5555")
    
    def remove_wireless_device(self):
        """从列表删除无线设备"""
        current_item = self.wireless_device_list.currentItem()
        if current_item:
            self.wireless_device_list.takeItem(self.wireless_device_list.row(current_item))
        
    def load_settings(self):
        """加载设置"""
        if os.path.exists(self.settings_file):
            try:
                with open(self.settings_file, 'r', encoding='utf-8') as f:
                    return json.load(f)
            except:
                pass
        return self.get_default_settings()
        
    def save_settings(self):
        """保存设置"""
        try:
            with open(self.settings_file, 'w', encoding='utf-8') as f:
                json.dump(self.settings, f, indent=2, ensure_ascii=False)
            return True
        except Exception as e:
            QMessageBox.critical(self, "错误", f"保存设置失败: {str(e)}")
            return False
            
    def get_default_settings(self):
        """获取默认设置"""
        return {
            "wireless": {
                "saved_devices": [],
                "auto_connect": False,
                "wireless_priority": False,
                "default_pair_port": "5037"
            },
            "capture": {
                "debug_log": False
            },
            "performance": {
                "min_check_interval": 0.05,
                "coord_update_interval": 50
            },
            "record": {
                "record_mouse_move": False,
                "record_timestamps": True,
                "default_play_speed": 1.0
            },
            "ui": {
                "auto_refresh_devices": False,
                "confirm_exit": True,
                "max_log_lines": 1000
            },
            "debug": {
                "device_events": False,
                "adb_commands": False,
                "touch_events": False,
                "save_raw_events": False
            }
        }
        
    def load_current_settings(self):
        """加载当前设置到UI"""
        # 无线设置
        wireless = self.settings.get("wireless", {})
        saved_devices = wireless.get("saved_devices", [])
        for device in saved_devices:
            self.wireless_device_list.addItem(f"{device['name']} - {device['ip']}:{device['port']}")
        self.auto_connect_wireless_check.setChecked(wireless.get("auto_connect", False))
        self.wireless_priority_check.setChecked(wireless.get("wireless_priority", False))
        self.default_pair_port_input.setText(wireless.get("default_pair_port", "5037"))
        
        # 捕获设置
        capture = self.settings.get("capture", {})
        self.capture_debug_check.setChecked(capture.get("debug_log", False))
        
        # 性能设置
        performance = self.settings.get("performance", {})
        self.min_check_interval.setValue(performance.get("min_check_interval", 0.05))
        self.coord_update_interval.setValue(performance.get("coord_update_interval", 50))
        
        # 录制设置
        record = self.settings.get("record", {})
        self.record_mouse_move_check.setChecked(record.get("record_mouse_move", False))
        self.record_timestamps_check.setChecked(record.get("record_timestamps", True))
        self.default_play_speed.setValue(record.get("default_play_speed", 1.0))
        
        # UI设置
        ui = self.settings.get("ui", {})
        self.auto_refresh_devices_check.setChecked(ui.get("auto_refresh_devices", False))
        self.confirm_exit_check.setChecked(ui.get("confirm_exit", True))
        self.max_log_lines.setValue(ui.get("max_log_lines", 1000))
        
        # 调试设置
        debug = self.settings.get("debug", {})
        self.debug_device_events_check.setChecked(debug.get("device_events", False))
        self.debug_adb_commands_check.setChecked(debug.get("adb_commands", False))
        self.debug_touch_events_check.setChecked(debug.get("touch_events", False))
        self.save_raw_events_check.setChecked(debug.get("save_raw_events", False))
        
    def get_current_settings(self):
        """从UI获取当前设置"""
        # 收集无线设备列表
        saved_devices = []
        for i in range(self.wireless_device_list.count()):
            item_text = self.wireless_device_list.item(i).text()
            # 解析格式: "name - ip:port"
            parts = item_text.split(" - ")
            if len(parts) == 2:
                name = parts[0]
                ip_port = parts[1].split(":")
                if len(ip_port) == 2:
                    saved_devices.append({
                        "name": name,
                        "ip": ip_port[0],
                        "port": ip_port[1]
                    })
        
        return {
            "wireless": {
                "saved_devices": saved_devices,
                "auto_connect": self.auto_connect_wireless_check.isChecked(),
                "wireless_priority": self.wireless_priority_check.isChecked(),
                "default_pair_port": self.default_pair_port_input.text()
            },
            "capture": {
                "debug_log": self.capture_debug_check.isChecked()
            },
            "performance": {
                "min_check_interval": self.min_check_interval.value(),
                "coord_update_interval": self.coord_update_interval.value()
            },
            "record": {
                "record_mouse_move": self.record_mouse_move_check.isChecked(),
                "record_timestamps": self.record_timestamps_check.isChecked(),
                "default_play_speed": self.default_play_speed.value()
            },
            "ui": {
                "auto_refresh_devices": self.auto_refresh_devices_check.isChecked(),
                "confirm_exit": self.confirm_exit_check.isChecked(),
                "max_log_lines": self.max_log_lines.value()
            },
            "debug": {
                "device_events": self.debug_device_events_check.isChecked(),
                "adb_commands": self.debug_adb_commands_check.isChecked(),
                "touch_events": self.debug_touch_events_check.isChecked(),
                "save_raw_events": self.save_raw_events_check.isChecked()
            }
        }
        
    def apply_settings(self):
        """应用设置"""
        self.settings = self.get_current_settings()
        self.save_settings()
        
        # 发送信号通知主窗口
        self.settings_changed.emit(self.settings)
        
        # 立即应用捕获方法
        if self.parent_window:
            from core.window_capture import WindowCapture
            WindowCapture.enable_log(self.settings["capture"]["debug_log"])
            
        QMessageBox.information(self, "提示", "设置已应用")
        
    def accept_settings(self):
        """确定并关闭"""
        self.apply_settings()
        self.accept()
        
    def reset_defaults(self):
        """恢复默认设置"""
        reply = QMessageBox.question(
            self, "确认", "确定要恢复默认设置吗？",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
        )
        
        if reply == QMessageBox.StandardButton.Yes:
            self.settings = self.get_default_settings()
            self.load_current_settings()
            QMessageBox.information(self, "提示", "已恢复默认设置")