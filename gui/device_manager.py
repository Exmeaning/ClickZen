"""设备管理相关功能模块"""
from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
import json

class DeviceManager:
    """设备管理器 - 处理USB和无线连接相关功能"""
    
    def __init__(self, parent, adb_manager):
        self.parent = parent
        self.adb = adb_manager
        
    def refresh_devices(self):
        """刷新设备列表"""
        self.parent.log("正在刷新设备列表...")
        devices = self.adb.get_devices()

        self.parent.device_combo.clear()
        
        # 标记无线设备
        for serial, info in devices:
            if ':' in serial:  # 无线设备
                display_text = f"[无线] {info} ({serial})"
            else:
                display_text = f"{info} ({serial})"
            self.parent.device_combo.addItem(display_text, serial)

        if devices:
            self.parent.log(f"发现 {len(devices)} 个设备")
        else:
            self.parent.log("未发现设备，请检查连接")
    
    def load_saved_wireless_devices(self):
        """加载已保存的无线设备"""
        try:
            import os
            if os.path.exists("settings.json"):
                with open("settings.json", 'r', encoding='utf-8') as f:
                    settings = json.load(f)
                    devices = settings.get("wireless", {}).get("saved_devices", [])
                    
                    self.parent.wireless_device_combo.clear()
                    self.parent.wireless_device_combo.addItem("选择已保存设备...")
                    for device in devices:
                        self.parent.wireless_device_combo.addItem(
                            device['name'], 
                            f"{device['ip']}:{device['port']}"
                        )
        except Exception as e:
            self.parent.log(f"加载无线设备失败: {e}")
    
    def save_wireless_device(self, name, ip, port):
        """保存无线设备到设置"""
        try:
            import os
            settings = {}
            
            # 读取现有设置
            if os.path.exists("settings.json"):
                with open("settings.json", 'r', encoding='utf-8') as f:
                    settings = json.load(f)
            
            # 确保wireless结构存在
            if "wireless" not in settings:
                settings["wireless"] = {"saved_devices": []}
            if "saved_devices" not in settings["wireless"]:
                settings["wireless"]["saved_devices"] = []
            
            # 检查是否已存在（避免重复）
            devices = settings["wireless"]["saved_devices"]
            for device in devices:
                if device.get("ip") == ip and device.get("port") == port:
                    # 更新名称
                    device["name"] = name
                    break
            else:
                # 添加新设备
                devices.append({
                    "name": name,
                    "ip": ip,
                    "port": port
                })
            
            # 保存设置
            with open("settings.json", 'w', encoding='utf-8') as f:
                json.dump(settings, f, indent=2, ensure_ascii=False)
            
            return True
        except Exception as e:
            self.parent.log(f"保存设备失败: {e}")
            return False
    
    def connect_saved_wireless_device(self):
        """连接无线设备（优先连接输入框中的地址，否则连接选中的已保存设备）"""
        # 1. 优先检查手动输入框
        manual_ip = self.parent.wireless_ip_input.text().strip()
        if manual_ip:
            # 使用手动输入的地址
            self.parent.log(f"正在连接手动输入设备: {manual_ip}")
            # 自动补全端口
            if ':' not in manual_ip:
                manual_ip += ":5555"
            
            # 复用manual_connect_wireless的逻辑，但直接调用
            self.parent.log(f"正在连接无线设备: {manual_ip}")
            
            # 更新状态显示
            status_label = self.parent.findChild(QLabel, "wireless_status")
            if status_label:
                status_label.setText("连接中...")
                status_label.setStyleSheet("font-size: 10px; color: #FFA500;")
                
            success, msg = self.adb.connect_wireless_device(manual_ip)
            if success:
                self.parent.log(f"✓ 无线连接成功: {msg}")
                # 更新状态
                if status_label:
                    status_label.setText(f"已连接: {manual_ip}")
                    status_label.setStyleSheet("font-size: 10px; color: #4CAF50;")
                # 刷新设备列表
                self.refresh_devices()
                
                # 询问是否保存（如果不在saved列表中）
                # 这里简单处理，可以直接调用manual_connect_wireless里的保存逻辑，
                # 或者提取公共方法。为了简单起见，我们直接询问。
                parts = manual_ip.split(':')
                if len(parts) == 2:
                    # 检查是否已保存
                    is_saved = False
                    for i in range(self.parent.wireless_device_combo.count()):
                        if self.parent.wireless_device_combo.itemData(i) == manual_ip:
                            is_saved = True
                            break
                    
                    if not is_saved:
                        save_reply = QMessageBox.question(
                            self.parent, "保存设备",
                            f"连接成功！是否将此设备保存到常用设备列表？",
                            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
                        )
                        if save_reply == QMessageBox.StandardButton.Yes:
                            name, ok = QInputDialog.getText(
                                self.parent, "设备名称", 
                                "请输入设备名称:", 
                                QLineEdit.EchoMode.Normal,
                                f"设备_{parts[0].split('.')[-1]}"
                            )
                            if ok and name:
                                self.save_wireless_device(name, parts[0], parts[1])
                                self.load_saved_wireless_devices()
                                self.parent.log(f"已保存设备: {name}")
            else:
                self.parent.log(f"✗ 无线连接失败: {msg}")
                if status_label:
                    status_label.setText("连接失败")
                    status_label.setStyleSheet("font-size: 10px; color: #f44336;")
                QMessageBox.warning(self.parent, "连接失败", msg)
            return

        # 2. 如果没有手动输入，连接下拉框选中的设备
        ip_port = self.parent.wireless_device_combo.currentData()
        if not ip_port:
            QMessageBox.information(self.parent, "提示", "请输入IP地址或选择一个已保存的设备")
            return
            
        self.parent.log(f"正在连接无线设备: {ip_port}")
        
        # 更新状态显示
        status_label = self.parent.findChild(QLabel, "wireless_status")
        if status_label:
            status_label.setText("连接中...")
            status_label.setStyleSheet("font-size: 10px; color: #FFA500;")
        
        success, msg = self.adb.connect_wireless_device(ip_port)
        if success:
            self.parent.log(f"✓ 无线连接成功: {msg}")
            # 更新状态
            if status_label:
                status_label.setText(f"已连接: {ip_port}")
                status_label.setStyleSheet("font-size: 10px; color: #4CAF50;")
            # 刷新设备列表
            self.refresh_devices()
        else:
            self.parent.log(f"✗ 无线连接失败: {msg}")
            # 更新状态
            if status_label:
                status_label.setText("连接失败")
                status_label.setStyleSheet("font-size: 10px; color: #f44336;")
            QMessageBox.warning(self.parent, "连接失败", msg)
    
    def manual_connect_wireless(self):
        """手动连接无线设备"""
        ip_port = self.parent.wireless_ip_input.text().strip()
        if not ip_port:
            QMessageBox.warning(self.parent, "警告", "请输入IP:端口")
            return
        
        # 验证格式
        if ':' not in ip_port:
            ip_port += ":5555"  # 默认端口
        
        self.parent.log(f"正在连接无线设备: {ip_port}")
        success, msg = self.adb.connect_wireless_device(ip_port)
        if success:
            self.parent.log(f"✓ 无线连接成功: {msg}")
            self.parent.wireless_ip_input.clear()
            # 刷新设备列表
            self.refresh_devices()
            
            # 询问是否保存
            parts = ip_port.split(':')
            if len(parts) == 2:
                save_reply = QMessageBox.question(
                    self.parent, "保存设备",
                    f"连接成功！是否将此设备保存到常用设备列表？",
                    QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
                )
                
                if save_reply == QMessageBox.StandardButton.Yes:
                    name, ok = QInputDialog.getText(
                        self.parent, "设备名称", 
                        "请输入设备名称:", 
                        QLineEdit.EchoMode.Normal,
                        f"设备_{parts[0].split('.')[-1]}"
                    )
                    
                    if ok and name:
                        self.save_wireless_device(name, parts[0], parts[1])
                        self.load_saved_wireless_devices()
                        self.parent.log(f"已保存设备: {name}")
        else:
            self.parent.log(f"✗ 无线连接失败: {msg}")
            
            # 如果是"already connected"错误，也刷新列表
            if "already connected" in msg.lower():
                self.refresh_devices()
                self.parent.log("设备可能已连接，已刷新列表")
            else:
                QMessageBox.warning(self.parent, "连接失败", msg)
    
    def disconnect_wireless_device(self):
        """断开所有无线设备"""
        # 断开所有无线连接
        success, msg = self.adb.disconnect_wireless_device()  # 不传参数断开所有
        if success:
            self.parent.log(f"已断开所有无线设备")
            # 更新状态
            status_label = self.parent.findChild(QLabel, "wireless_status")
            if status_label:
                status_label.setText("未连接")
                status_label.setStyleSheet("font-size: 10px; color: #999;")
            self.refresh_devices()
        else:
            self.parent.log(f"断开失败: {msg}")
    
    def show_pairing_dialog(self):
        """显示配对对话框"""
        dialog = QDialog(self.parent)
        dialog.setWindowTitle("配对新设备 (Android 11+)")
        dialog.setModal(True)
        
        layout = QFormLayout(dialog)
        
        # 说明
        info_label = QLabel(
            "Android 11+ 配对步骤:\n"
            "1. 手机开启'开发者选项' → '无线调试'\n"
            "2. 点击'使用配对码配对设备'\n"
            "3. 输入显示的IP、端口和配对码"
        )
        info_label.setWordWrap(True)
        layout.addRow(info_label)
        
        # 输入框
        ip_input = QLineEdit()
        ip_input.setPlaceholderText("如: 192.168.1.100")
        port_input = QLineEdit()
        port_input.setPlaceholderText("如: 36789")
        code_input = QLineEdit()
        code_input.setPlaceholderText("6位配对码")
        
        layout.addRow("IP地址:", ip_input)
        layout.addRow("配对端口:", port_input)
        layout.addRow("配对码:", code_input)
        
        # 按钮
        btn_layout = QHBoxLayout()
        pair_btn = QPushButton("配对")
        cancel_btn = QPushButton("取消")
        
        def do_pair():
            ip = ip_input.text().strip()
            port = port_input.text().strip()
            code = code_input.text().strip()
            
            if not all([ip, port, code]):
                QMessageBox.warning(dialog, "警告", "请填写完整信息")
                return
            
            ip_port = f"{ip}:{port}"
            self.parent.log(f"正在配对设备: {ip_port}")
            
            success, msg = self.adb.pair_wireless_device(ip_port, code)
            if success:
                # 从返回消息中提取IP（格式: "配对成功|192.168.x.x"）
                if '|' in msg:
                    parts = msg.split('|')
                    actual_ip = parts[1] if len(parts) > 1 else ip
                else:
                    actual_ip = ip
                
                self.parent.log(f"✓ 配对成功")
                
                # 等待一下让配对生效
                import time
                time.sleep(1)
                
                # 配对成功后自动连接到无线调试端口
                connect_port = "5555"  # Android 11+无线调试默认端口
                connect_ip_port = f"{actual_ip}:{connect_port}"
                
                self.parent.log(f"正在自动连接到 {connect_ip_port}...")
                
                # 尝试连接3次
                connect_success = False
                for attempt in range(3):
                    connect_success, connect_msg = self.adb.connect_wireless_device(connect_ip_port)
                    if connect_success:
                        break
                    time.sleep(1)
                    self.parent.log(f"重试连接 ({attempt + 1}/3)...")
                
                if connect_success:
                    self.parent.log(f"✓ 自动连接成功: {connect_msg}")
                    # 刷新设备列表
                    self.refresh_devices()
                    
                    # 询问是否保存设备
                    save_reply = QMessageBox.question(
                        dialog, "保存设备",
                        f"连接成功！是否将此设备保存到常用设备列表？\n设备: {ip}:{connect_port}",
                        QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
                    )
                    
                    if save_reply == QMessageBox.StandardButton.Yes:
                        # 获取设备名称
                        name_dialog = QInputDialog()
                        device_name, ok = name_dialog.getText(
                            dialog, "设备名称", 
                            "请输入设备名称（便于识别）:", 
                            QLineEdit.EchoMode.Normal,
                            f"Android设备_{ip.split('.')[-1]}"
                        )
                        
                        if ok and device_name:
                            # 保存到设置
                            self.save_wireless_device(device_name, ip, connect_port)
                            self.load_saved_wireless_devices()  # 刷新下拉列表
                            self.parent.log(f"已保存设备: {device_name}")
                    
                    QMessageBox.information(dialog, "成功", 
                        f"配对并连接成功！\n设备已添加到设备列表")
                else:
                    self.parent.log(f"⚠ 配对成功但自动连接失败: {connect_msg}")
                    QMessageBox.warning(dialog, "注意", 
                        f"配对成功但自动连接失败！\n\n"
                        f"请手动连接到: {ip}:{connect_port}\n"
                        f"错误信息: {connect_msg}")
                
                dialog.accept()
            else:
                self.parent.log(f"✗ 配对失败: {msg}")
                QMessageBox.warning(dialog, "配对失败", msg)
        
        pair_btn.clicked.connect(do_pair)
        cancel_btn.clicked.connect(dialog.reject)
        
        btn_layout.addWidget(pair_btn)
        btn_layout.addWidget(cancel_btn)
        layout.addRow(btn_layout)
        
        dialog.exec()