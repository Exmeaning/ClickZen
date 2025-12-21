"""
高级监控功能对话框
提供网络变量同步的配置界面
"""

from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *
import json
from core.variable_server import VariableServer
from utils.network_protocol import get_sample_file_content
import os


class AdvancedMonitorDialog(QDialog):
    """高级监控功能配置对话框 - 简化版"""
    
    def __init__(self, auto_monitor, parent=None):
        super().__init__(parent)
        self.auto_monitor = auto_monitor
        self.network_handler = None  # 服务器实例
        self.settings_file = "advanced_monitor_settings.json"
        self.auto_save_timer = QTimer(self)
        self.auto_save_timer.timeout.connect(self.auto_save_settings)
        self.auto_save_timer.setInterval(3000)
        
        self.sync_variables = []  # 要同步的变量列表
        self.is_initializing = True  # 标记正在初始化
        
        self.setWindowTitle("🌐 网络变量同步")
        self.setMinimumSize(700, 500)
        
        self.initUI()
        self.load_settings()
        
        self.is_initializing = False  # 初始化完成
        self.auto_save_timer.start()
    
    def initUI(self):
        """初始化UI - 服务器模式"""
        layout = QVBoxLayout(self)
        
        # 模式说明
        mode_group = QGroupBox("🌐 TCP服务器模式")
        mode_layout = QVBoxLayout()
        
        mode_info = QLabel(
            "📡 <b>功能说明</b>\n\n"
            "• 在本机指定端口监听TCP连接\n"
            "• 接收客户端的变量更新请求\n"
            "• 向客户端提供变量查询服务\n"
            "• 支持多客户端同时连接\n"
            "• 变量更新时自动广播给所有客户端"
        )
        mode_info.setWordWrap(True)
        mode_info.setStyleSheet("""
            padding: 10px;
            background-color: #f5f5f5;
            border-radius: 4px;
            font-size: 11px;
        """)
        mode_layout.addWidget(mode_info)
        
        mode_group.setLayout(mode_layout)
        layout.addWidget(mode_group)
        
        # 服务器设置
        server_group = QGroupBox("📡 服务器设置")
        server_layout = QFormLayout()
        
        # 端口设置
        self.server_port = QSpinBox()
        self.server_port.setRange(1024, 65535)
        self.server_port.setValue(9527)
        self.server_port.valueChanged.connect(lambda: self.mark_dirty())
        server_layout.addRow("监听端口:", self.server_port)
        
        # Token设置
        token_layout = QHBoxLayout()
        self.server_token = QLineEdit()
        self.server_token.setPlaceholderText("留空则不需要认证")
        self.server_token.setEchoMode(QLineEdit.EchoMode.Password)
        self.server_token.textChanged.connect(lambda: self.mark_dirty())
        
        self.show_token_btn = QPushButton("👁")
        self.show_token_btn.setMaximumWidth(30)
        self.show_token_btn.setCheckable(True)
        self.show_token_btn.toggled.connect(
            lambda checked: self.server_token.setEchoMode(
                QLineEdit.EchoMode.Normal if checked else QLineEdit.EchoMode.Password
            )
        )
        token_layout.addWidget(self.server_token)
        token_layout.addWidget(self.show_token_btn)
        server_layout.addRow("认证Token:", token_layout)
        
        # 显示本机IP
        import socket
        hostname = socket.gethostname()
        local_ip = socket.gethostbyname(hostname)
        info_label = QLabel(f"本机IP: {local_ip}")
        info_label.setStyleSheet("color: #666; font-size: 11px;")
        server_layout.addRow("", info_label)
        
        server_group.setLayout(server_layout)
        layout.addWidget(server_group)
        
        # 变量同步设置
        sync_group = QGroupBox("🔄 变量同步")
        sync_layout = QVBoxLayout()
        
        sync_info = QLabel(
            "配置需要同步的变量：\n"
            "• 接收客户端的变量更新\n"
            "• 主动推送变量给所有客户端"
        )
        sync_info.setStyleSheet("font-size: 11px; color: #666; margin-bottom: 10px;")
        sync_layout.addWidget(sync_info)
        
        # 变量列表
        self.var_list = QListWidget()
        self.var_list.setMaximumHeight(120)
        sync_layout.addWidget(self.var_list)
        
        # 变量操作按钮
        var_btn_layout = QHBoxLayout()
        self.add_var_btn = QPushButton("添加变量")
        self.add_var_btn.clicked.connect(self.add_variable)
        self.edit_var_btn = QPushButton("编辑")
        self.edit_var_btn.clicked.connect(self.edit_variable)
        self.remove_var_btn = QPushButton("删除")
        self.remove_var_btn.clicked.connect(self.remove_variable)
        
        var_btn_layout.addWidget(self.add_var_btn)
        var_btn_layout.addWidget(self.edit_var_btn)
        var_btn_layout.addWidget(self.remove_var_btn)
        var_btn_layout.addStretch()
        
        sync_layout.addLayout(var_btn_layout)
        
        # 主动推送设置
        push_group = QGroupBox("主动推送")
        push_layout = QVBoxLayout()
        
        # 推送控制
        push_control_layout = QHBoxLayout()
        self.auto_push_check = QCheckBox("启用定时推送")
        self.auto_push_check.setChecked(False)
        self.auto_push_check.toggled.connect(self.on_auto_push_toggled)
        push_control_layout.addWidget(self.auto_push_check)
        
        self.push_interval_spin = QDoubleSpinBox()
        self.push_interval_spin.setRange(0.5, 60)
        self.push_interval_spin.setValue(5.0)
        self.push_interval_spin.setSuffix(" 秒")
        self.push_interval_spin.setEnabled(False)
        self.push_interval_spin.valueChanged.connect(lambda: self.mark_dirty())
        push_control_layout.addWidget(QLabel("推送间隔:"))
        push_control_layout.addWidget(self.push_interval_spin)
        push_control_layout.addStretch()
        
        push_layout.addLayout(push_control_layout)
        
        # 手动推送按钮
        push_btn_layout = QHBoxLayout()
        self.push_now_btn = QPushButton("📤 立即推送所有变量")
        self.push_now_btn.clicked.connect(self.push_all_variables)
        self.push_selected_btn = QPushButton("📤 推送选中变量")
        self.push_selected_btn.clicked.connect(self.push_selected_variables)
        
        push_btn_layout.addWidget(self.push_now_btn)
        push_btn_layout.addWidget(self.push_selected_btn)
        push_btn_layout.addStretch()
        
        push_layout.addLayout(push_btn_layout)
        push_group.setLayout(push_layout)
        
        sync_layout.addWidget(push_group)
        
        # 同步间隔（用于监控循环检查）
        interval_layout = QHBoxLayout()
        interval_layout.addWidget(QLabel("监控检查间隔:"))
        self.sync_interval = QDoubleSpinBox()
        self.sync_interval.setRange(0.1, 60)
        self.sync_interval.setValue(1.0)
        self.sync_interval.setSuffix(" 秒")
        self.sync_interval.valueChanged.connect(lambda: self.mark_dirty())
        interval_layout.addWidget(self.sync_interval)
        interval_layout.addStretch()
        sync_layout.addLayout(interval_layout)
        
        sync_group.setLayout(sync_layout)
        layout.addWidget(sync_group)
        
        # 初始化推送定时器
        self.push_timer = QTimer(self)
        self.push_timer.timeout.connect(self.auto_push_variables)
        
        # 控制按钮
        control_layout = QHBoxLayout()
        self.start_btn = QPushButton("▶ 启动")
        self.start_btn.clicked.connect(self.start_network)
        self.stop_btn = QPushButton("⏹ 停止")
        self.stop_btn.clicked.connect(self.stop_network)
        self.stop_btn.setEnabled(False)
        self.test_btn = QPushButton("📊 服务器状态")
        self.test_btn.clicked.connect(self.test_connection)
        self.doc_btn = QPushButton("📄 查看文档")
        self.doc_btn.clicked.connect(self.show_documentation)
        
        control_layout.addWidget(self.start_btn)
        control_layout.addWidget(self.stop_btn)
        control_layout.addWidget(self.test_btn)
        control_layout.addWidget(self.doc_btn)
        control_layout.addStretch()
        
        layout.addLayout(control_layout)
        
        # 日志输出
        log_group = QGroupBox("📋 连接日志")
        log_layout = QVBoxLayout()
        self.log_text = QTextEdit()
        self.log_text.setReadOnly(True)
        self.log_text.setMaximumHeight(100)
        log_layout.addWidget(self.log_text)
        log_group.setLayout(log_layout)
        layout.addWidget(log_group)
        
        # 状态栏
        status_layout = QHBoxLayout()
        self.status_label = QLabel("⏹ 未启动")
        self.status_label.setStyleSheet("font-weight: bold;")
        self.save_status = QLabel("")
        self.save_status.setStyleSheet("color: green; font-size: 11px;")
        
        status_layout.addWidget(self.status_label)
        status_layout.addStretch()
        status_layout.addWidget(self.save_status)
        
        close_btn = QPushButton("关闭")
        close_btn.clicked.connect(self.close)
        status_layout.addWidget(close_btn)
        
        layout.addLayout(status_layout)
    

    
    def add_variable(self):
        """添加同步变量"""
        dialog = VariableConfigDialog(self)
        if dialog.exec():
            var_config = dialog.get_config()
            self.sync_variables.append(var_config)
            self.refresh_var_list()
            self.mark_dirty()
    
    def edit_variable(self):
        """编辑变量"""
        current = self.var_list.currentRow()
        if current >= 0:
            dialog = VariableConfigDialog(self, self.sync_variables[current])
            if dialog.exec():
                self.sync_variables[current] = dialog.get_config()
                self.refresh_var_list()
                self.mark_dirty()
    
    def remove_variable(self):
        """删除变量"""
        current = self.var_list.currentRow()
        if current >= 0:
            del self.sync_variables[current]
            self.refresh_var_list()
            self.mark_dirty()
    
    def refresh_var_list(self):
        """刷新变量列表"""
        self.var_list.clear()
        for config in self.sync_variables:
            name = config.get('name', '')
            direction = config.get('direction', 'both')
            if direction == 'both':
                arrow = '↔'
            elif direction == 'send':
                arrow = '→'
            else:
                arrow = '←'
            self.var_list.addItem(f"{arrow} {name}")
    
    def start_network(self):
        """启动网络服务"""
        self.start_server()
    
    def stop_network(self):
        """停止网络服务"""
        # 停止自动推送
        if self.push_timer.isActive():
            self.push_timer.stop()
            self.log("⏹ 自动推送已停止")
        
        if self.network_handler:
            self.network_handler.stop()
            # 注意：不要设置为None，因为可能还需要在auto_monitor中使用
            # self.network_handler = None
        
        self.status_label.setText("⏹ 已停止")
        self.start_btn.setEnabled(True)
        self.stop_btn.setEnabled(False)
        self.log("服务器已停止")
    
    def start_server(self):
        """启动服务器"""
        # 检查是否已有服务器在运行
        if self.auto_monitor and self.auto_monitor.variable_server:
            existing_server = self.auto_monitor.variable_server
            if existing_server.running:
                reply = QMessageBox.question(
                    self, "确认",
                    f"服务器已在端口 {existing_server.port} 运行。\n是否停止并重新启动？",
                    QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
                )
                if reply == QMessageBox.StandardButton.No:
                    return
                # 停止现有服务器
                existing_server.stop()
        
        port = self.server_port.value()
        token = self.server_token.text() if self.server_token.text() else None
        
        self.network_handler = VariableServer(port, token)
        self.network_handler.log_message.connect(self.log)
        self.network_handler.client_connected.connect(self.on_client_connected)
        self.network_handler.client_disconnected.connect(self.on_client_disconnected)
        self.network_handler.variable_updated.connect(self.on_variable_updated)
        self.network_handler.error_occurred.connect(lambda msg: self.log(f"❌ {msg}"))
        
        if self.network_handler.start():
            self.status_label.setText(f"✅ 服务器运行中 (端口: {port})")
            self.start_btn.setEnabled(False)
            self.stop_btn.setEnabled(True)
            self.log(f"服务器启动成功，监听端口 {port}")
            
            # 集成到auto_monitor
            if self.auto_monitor:
                self.auto_monitor.variable_server = self.network_handler
                self.auto_monitor.sync_variables = self.sync_variables
                self.auto_monitor.sync_interval = self.sync_interval.value()
            
            # 检查是否需要启动自动推送
            if self.auto_push_check.isChecked():
                interval = int(self.push_interval_spin.value() * 1000)
                self.push_timer.start(interval)
                self.log(f"✅ 自动推送已启动，间隔 {self.push_interval_spin.value()} 秒")
        else:
            self.log("启动服务器失败")
            self.network_handler = None
    
    def on_client_connected(self, address):
        """客户端连接事件"""
        self.log(f"✅ 客户端连接: {address}")
        # 更新状态显示客户端数量
        if self.network_handler and hasattr(self.network_handler, 'clients'):
            client_count = len(self.network_handler.clients)
            self.status_label.setText(f"✅ 服务器运行中 (端口: {self.server_port.value()}) - {client_count}个客户端")
    
    def on_client_disconnected(self, address):
        """客户端断开事件"""
        self.log(f"❌ 客户端断开: {address}")
        # 更新状态显示客户端数量
        if self.network_handler and hasattr(self.network_handler, 'clients'):
            client_count = len(self.network_handler.clients)
            self.status_label.setText(f"✅ 服务器运行中 (端口: {self.server_port.value()}) - {client_count}个客户端")
    

    
    def on_variable_updated(self, name, value):
        """变量更新回调"""
        if self.auto_monitor:
            self.auto_monitor.global_variables[name] = value
            self.log(f"📥 接收变量: {name} = {value}")
    
    def on_auto_push_toggled(self, checked):
        """自动推送开关切换"""
        # 初始化期间不处理
        if hasattr(self, 'is_initializing') and self.is_initializing:
            return
            
        self.push_interval_spin.setEnabled(checked)
        
        if checked:
            # 检查服务器是否在运行
            server_running = False
            if self.network_handler and self.network_handler.running:
                server_running = True
            elif self.auto_monitor and self.auto_monitor.variable_server and self.auto_monitor.variable_server.running:
                server_running = True
                self.network_handler = self.auto_monitor.variable_server
            
            if server_running:
                # 启动定时推送
                interval = int(self.push_interval_spin.value() * 1000)
                self.push_timer.start(interval)
                self.log(f"✅ 启用自动推送，间隔 {self.push_interval_spin.value()} 秒")
            else:
                self.log("⏸ 自动推送已启用但服务器未运行")
        else:
            # 停止定时推送
            self.push_timer.stop()
            self.log("⏹ 自动推送已停止")
        
        self.mark_dirty()
    
    def push_all_variables(self):
        """推送所有变量"""
        if not self.network_handler or not self.network_handler.running:
            QMessageBox.warning(self, "警告", "服务器未运行")
            return
        
        if not self.auto_monitor or not self.auto_monitor.global_variables:
            QMessageBox.information(self, "提示", "没有可推送的变量")
            return
        
        # 推送所有公共变量
        vars_to_push = self.auto_monitor.global_variables.copy()
        self._push_variables(vars_to_push)
        self.log(f"📤 手动推送 {len(vars_to_push)} 个变量")
    
    def push_selected_variables(self):
        """推送选中的同步变量"""
        if not self.network_handler or not self.network_handler.running:
            QMessageBox.warning(self, "警告", "服务器未运行")
            return
        
        current = self.var_list.currentRow()
        if current < 0:
            QMessageBox.information(self, "提示", "请先选择要推送的变量")
            return
        
        var_config = self.sync_variables[current]
        var_name = var_config.get('name')
        
        if self.auto_monitor and var_name in self.auto_monitor.global_variables:
            value = self.auto_monitor.global_variables[var_name]
            self._push_variables({var_name: value})
            self.log(f"📤 手动推送变量: {var_name} = {value}")
        else:
            QMessageBox.information(self, "提示", f"变量 {var_name} 不存在或未定义")
    
    def auto_push_variables(self):
        """定时自动推送变量"""
        if not self.network_handler or not self.network_handler.running:
            self.push_timer.stop()
            self.auto_push_check.setChecked(False)
            return
        
        if not self.auto_monitor:
            return
        
        # 推送配置的同步变量
        vars_to_push = {}
        for var_config in self.sync_variables:
            var_name = var_config.get('name')
            direction = var_config.get('direction', 'both')
            
            # 只推送 send 或 both 方向的变量
            if direction in ['send', 'both']:
                if var_name in self.auto_monitor.global_variables:
                    vars_to_push[var_name] = self.auto_monitor.global_variables[var_name]
        
        if vars_to_push:
            self._push_variables(vars_to_push)
            self.log(f"⏰ 自动推送 {len(vars_to_push)} 个变量")
    
    def _push_variables(self, variables):
        """推送变量到所有客户端"""
        if not self.network_handler or not variables:
            return
        
        # 使用广播消息推送给所有客户端
        from utils.network_protocol import NetworkMessage
        message = NetworkMessage.create_broadcast(variables)
        
        # 发送给所有连接的客户端
        success_count = 0
        fail_count = 0
        for client_addr, client_socket in list(self.network_handler.clients.items()):
            try:
                client_socket.send((message + '\n').encode('utf-8'))
                success_count += 1
            except Exception as e:
                fail_count += 1
                self.log(f"❌ 推送失败到 {client_addr}: {str(e)}")
        
        if success_count > 0:
            self.log(f"📡 广播成功到 {success_count} 个客户端")
        if fail_count > 0:
            self.log(f"⚠️ {fail_count} 个客户端推送失败")
    
    def test_connection(self):
        """显示服务器状态"""
        if self.network_handler and self.network_handler.running:
            QMessageBox.information(self, "服务器状态", 
                f"服务器运行中\n"
                f"端口: {self.server_port.value()}\n"
                f"客户端数: {len(self.network_handler.clients)}\n"
                f"当前变量: {len(self.network_handler.variables)}个"
            )
        else:
            QMessageBox.information(self, "服务器状态", "服务器未启动")
    
    def show_documentation(self):
        """显示文档"""
        dialog = DocumentationDialog(self)
        dialog.exec()
    
    def log(self, message):
        """添加日志"""
        timestamp = QTime.currentTime().toString("HH:mm:ss")
        self.log_text.append(f"[{timestamp}] {message}")
    
    def mark_dirty(self):
        """标记需要保存"""
        self.is_dirty = True
    
    def auto_save_settings(self):
        """自动保存设置"""
        if hasattr(self, 'is_dirty') and self.is_dirty:
            self.save_settings()
            self.save_status.setText("✅ 已保存")
            QTimer.singleShot(2000, lambda: self.save_status.clear())
            self.is_dirty = False
    
    def save_settings(self):
        """保存设置"""
        settings = {
            'server': {
                'port': self.server_port.value(),
                'token': self.server_token.text()
            },
            'sync_variables': self.sync_variables,
            'sync_interval': self.sync_interval.value(),
            'auto_push': {
                'enabled': self.auto_push_check.isChecked(),
                'interval': self.push_interval_spin.value()
            }
        }
        
        try:
            with open(self.settings_file, 'w', encoding='utf-8') as f:
                json.dump(settings, f, indent=2, ensure_ascii=False)
            return True
        except:
            return False
    
    def load_settings(self):
        """加载设置"""
        try:
            if os.path.exists(self.settings_file):
                with open(self.settings_file, 'r', encoding='utf-8') as f:
                    settings = json.load(f)
                
                # 加载服务器设置
                server = settings.get('server', {})
                self.server_port.setValue(server.get('port', 9527))
                self.server_token.setText(server.get('token', ''))
                
                # 加载同步变量
                self.sync_variables = settings.get('sync_variables', [])
                self.sync_interval.setValue(settings.get('sync_interval', 1.0))
                
                # 加载自动推送设置（先不触发信号）
                auto_push = settings.get('auto_push', {})
                self.push_interval_spin.setValue(auto_push.get('interval', 5.0))
                
                self.refresh_var_list()
                
                # 在检查服务器之前先设置标志，避免触发信号
                push_enabled = auto_push.get('enabled', False)
                
                # 同步到auto_monitor
                if self.auto_monitor:
                    self.auto_monitor.sync_variables = self.sync_variables
                    self.auto_monitor.sync_interval = self.sync_interval.value()
                
        except Exception as e:
            print(f"加载设置失败: {e}")
        
        self.is_dirty = False
        
        # 检查并恢复服务器状态
        self.check_existing_server()
        
        # 在服务器状态确认后再设置自动推送
        if 'push_enabled' in locals() and push_enabled:
            self.auto_push_check.setChecked(True)
            # 如果服务器在运行，自动推送会在on_auto_push_toggled中启动
    
    def check_existing_server(self):
        """检查现有的服务器状态"""
        if self.auto_monitor and self.auto_monitor.variable_server:
            server = self.auto_monitor.variable_server
            if server.running:
                # 服务器已在运行，更新UI状态
                self.network_handler = server
                self.status_label.setText(f"✅ 服务器运行中 (端口: {server.port})")
                self.start_btn.setEnabled(False)
                self.stop_btn.setEnabled(True)
                self.log("检测到服务器已在运行")
                
                # 连接信号
                if not server.receivers(server.log_message):
                    server.log_message.connect(self.log)
                if not server.receivers(server.client_connected):
                    server.client_connected.connect(self.on_client_connected)
                if not server.receivers(server.client_disconnected):
                    server.client_disconnected.connect(self.on_client_disconnected)
                if not server.receivers(server.variable_updated):
                    server.variable_updated.connect(self.on_variable_updated)
                
                # 显示当前客户端数
                if hasattr(server, 'clients'):
                    client_count = len(server.clients)
                    if client_count > 0:
                        self.status_label.setText(f"✅ 服务器运行中 (端口: {server.port}) - {client_count}个客户端")
    
    def closeEvent(self, event):
        """关闭事件"""
        if hasattr(self, 'auto_save_timer'):
            self.auto_save_timer.stop()
        
        if hasattr(self, 'is_dirty') and self.is_dirty:
            self.save_settings()
        
        # 同步配置到auto_monitor
        if self.auto_monitor:
            self.auto_monitor.sync_variables = self.sync_variables
            self.auto_monitor.sync_interval = self.sync_interval.value()
        
        super().closeEvent(event)


class VariableConfigDialog(QDialog):
    """变量配置对话框"""
    
    def __init__(self, parent=None, config=None):
        super().__init__(parent)
        self.config = config or {}
        self.setWindowTitle("配置同步变量")
        self.setModal(True)
        
        layout = QFormLayout(self)
        
        # 变量名
        self.name_input = QLineEdit()
        self.name_input.setPlaceholderText("例如: counter")
        self.name_input.setText(self.config.get('name', ''))
        layout.addRow("变量名:", self.name_input)
        
        # 同步方向
        self.direction_combo = QComboBox()
        self.direction_combo.addItems([
            "双向同步 (收发)",
            "仅发送 (本地→远程)",
            "仅接收 (远程→本地)"
        ])
        
        direction = self.config.get('direction', 'both')
        if direction == 'send':
            self.direction_combo.setCurrentIndex(1)
        elif direction == 'receive':
            self.direction_combo.setCurrentIndex(2)
        else:
            self.direction_combo.setCurrentIndex(0)
        
        layout.addRow("同步方向:", self.direction_combo)
        
        # 说明
        info = QLabel(
            "• 双向：变量改变时广播给客户端，也接收客户端的更新\n"
            "• 仅发送：只广播给客户端，不接收客户端更新\n"
            "• 仅接收：只接收客户端更新，不广播"
        )
        info.setStyleSheet("color: #666; font-size: 10px;")
        layout.addRow("", info)
        
        # 按钮
        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
    
    def get_config(self):
        """获取配置"""
        directions = ['both', 'send', 'receive']
        return {
            'name': self.name_input.text(),
            'direction': directions[self.direction_combo.currentIndex()]
        }


class DocumentationDialog(QDialog):
    """文档对话框"""
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("📄 TCP服务器协议文档")
        self.setModal(True)
        self.setMinimumSize(600, 500)
        
        layout = QVBoxLayout(self)
        
        # 只显示服务器文档
        layout.addWidget(QLabel("<h3>TCP服务器协议文档</h3>"))
        content = self.get_server_doc()
        
        doc_text = QTextEdit()
        doc_text.setReadOnly(True)
        doc_text.setPlainText(content)
        doc_text.setFont(QFont("Consolas", 9))
        layout.addWidget(doc_text)
        
        # 关闭按钮
        close_btn = QPushButton("关闭")
        close_btn.clicked.connect(self.accept)
        layout.addWidget(close_btn, alignment=Qt.AlignmentFlag.AlignCenter)
    
    def get_server_doc(self):
        """服务器模式文档"""
        return """
=== TCP服务器模式详细文档 ===

## 一、基本说明
服务器监听指定端口，可同时接受多个客户端连接。
默认端口：9527（可在界面设置）
最大连接数：100（可配置）
广播模式：变量更新时自动推送给所有客户端

## 二、服务器端处理流程

### 1. 接受连接
- 监听端口等待客户端连接
- 为每个客户端创建独立的处理线程
- 记录客户端地址和连接时间

### 2. 处理认证（如果启用Token）
收到请求:
{
    "type": "auth",
    "token": "client_token",
    "timestamp": "2024-01-01T12:00:00"
}

验证成功，返回:
{
    "type": "success",
    "data": {"message": "authenticated"},
    "timestamp": "2024-01-01T12:00:01"
}

验证失败，返回并断开连接:
{
    "type": "error",
    "data": {"error": "Invalid token"},
    "timestamp": "2024-01-01T12:00:01"
}

### 3. 处理变量设置请求
收到客户端请求:
{
    "type": "set_variable",
    "data": {
        "name": "device_01_status",
        "value": "online"
    },
    "timestamp": "2024-01-01T12:00:02"
}

服务器处理：
1. 更新本地变量存储
2. 触发本地监控规则（如果有）
3. 广播给其他客户端（如果配置了广播）
4. 返回确认

返回响应:
{
    "type": "success",
    "data": {
        "name": "device_01_status",
        "value": "online",
        "message": "Variable updated"
    },
    "timestamp": "2024-01-01T12:00:02"
}

### 4. 处理变量获取请求
收到请求:
{
    "type": "get_variable",
    "data": {"name": "global_command"},
    "timestamp": "2024-01-01T12:00:03"
}

查找变量并返回:
{
    "type": "success",
    "data": {
        "name": "global_command",
        "value": "start_all"
    },
    "timestamp": "2024-01-01T12:00:03"
}

变量不存在时:
{
    "type": "error",
    "data": {"error": "Variable not found: global_command"},
    "timestamp": "2024-01-01T12:00:03"
}

### 5. 处理批量同步
收到客户端批量更新:
{
    "type": "sync_variables",
    "data": {
        "variables": {
            "device_01_battery": 75,
            "device_01_cpu": 30,
            "device_01_memory": 2048
        }
    },
    "timestamp": "2024-01-01T12:00:04"
}

服务器处理并返回:
{
    "type": "success",
    "data": {
        "updated": 3,
        "message": "Variables synchronized"
    },
    "timestamp": "2024-01-01T12:00:04"
}

### 6. 主动广播变量
当服务器端变量更新时，主动推送给所有客户端:
{
    "type": "broadcast",
    "data": {
        "variables": {
            "global_command": "pause",
            "emergency_stop": false,
            "task_id": 12345
        }
    },
    "timestamp": "2024-01-01T12:00:05"
}

期待客户端确认（可选）:
{
    "type": "success",
    "data": {"message": "Variables received"},
    "timestamp": "2024-01-01T12:00:05"
}

### 7. 心跳响应
收到心跳:
{
    "type": "ping",
    "timestamp": "2024-01-01T12:00:30"
}

立即响应:
{
    "type": "success",
    "data": {"message": "pong"},
    "timestamp": "2024-01-01T12:00:30"
}

## 三、服务器管理策略

### 客户端管理
1. **连接管理**：
   - 记录每个客户端的IP、端口、连接时间
   - 60秒无心跳视为断线，主动关闭连接
   - 支持查看当前连接列表

2. **权限控制**：
   - Token认证（可选）
   - 可设置只读客户端（只能获取不能设置）
   - IP白名单（未来功能）

3. **负载均衡**：
   - 限制最大连接数
   - 消息队列防止阻塞
   - 异步处理客户端请求

### 变量管理
1. **存储策略**：
   - 内存存储，重启后清空
   - 可选持久化到文件（未来功能）
   
2. **同步策略**：
   - 变量更新立即广播
   - 支持选择性广播（只给订阅的客户端）
   - 批量更新减少网络开销

3. **冲突处理**：
   - 多客户端同时更新：后到优先
   - 可选时间戳判断（未来功能）

## 四、性能指标

### 典型性能
- 单机支持客户端数：100+
- 消息延迟：<10ms（局域网）
- 吞吐量：1000+ msg/s
- 内存占用：<50MB（100客户端）

### 优化建议
1. **网络优化**：
   - 使用局域网减少延迟
   - 批量同步减少消息数
   - 合理设置同步间隔

2. **变量优化**：
   - 控制变量数量（<100个）
   - 使用简单数据类型
   - 避免频繁更新

## 五、应用场景示例

### 场景1：设备集群管理
服务器作为主控：
- 下发：command（统一命令）
- 收集：device_*_status（各设备状态）
- 监控：error_count（错误统计）

### 场景2：分布式任务调度
服务器分配任务：
- 下发：task_queue（任务队列）
- 收集：worker_*_progress（进度）
- 协调：resource_allocation（资源分配）

### 场景3：实时数据汇总
服务器收集数据：
- 收集：sensor_*_data（传感器数据）
- 计算：average_value（平均值）
- 广播：alert_status（警报状态）

## 六、故障处理

### 客户端断线
- 自动清理断线客户端
- 记录断线日志
- 不影响其他客户端

### 网络异常
- 消息发送失败自动重试
- 缓存未发送消息（限制大小）
- 恢复后补发重要消息

### 服务器重启
- 客户端自动重连
- 变量状态可选持久化
- 平滑重启不断连接
"""

    
    def start_server(self):
        """启动服务器"""
        if not self.variable_server:
            port = self.port_spin.value()
            token = self.token_input.text() if self.token_input.text() else None
            
            self.variable_server = VariableServer(port, token)
            self.variable_server.log_message.connect(self.on_server_log)
            self.variable_server.client_connected.connect(self.on_client_connected)
            self.variable_server.client_disconnected.connect(self.on_client_disconnected)
            self.variable_server.variable_updated.connect(self.on_variable_updated)
            
            # 与auto_monitor集成
            self.auto_monitor.variable_server = self.variable_server
            
        # 同时更新获取配置到auto_monitor
        self.auto_monitor.set_fetch_configs(self.fetch_configs)
        
        if self.variable_server.start(
            self.enable_broadcast_check.isChecked(),
            self.enable_receive_check.isChecked()
        ):
            self.server_status_label.setText("服务器状态: ✅ 运行中")
            self.server_status_label.setStyleSheet("color: green; font-weight: bold; padding: 5px;")
            self.start_server_btn.setEnabled(False)
            self.stop_server_btn.setEnabled(True)
            
            # 显示连接信息
            import socket
            hostname = socket.gethostname()
            ip = socket.gethostbyname(hostname)
            self.connection_info.append(f"服务器启动成功！")
            self.connection_info.append(f"本机IP: {ip}")
            self.connection_info.append(f"端口: {self.port_spin.value()}")
    
    def stop_server(self):
        """停止服务器"""
        if self.variable_server:
            self.variable_server.stop()
            self.server_status_label.setText("服务器状态: ⏹ 已停止")
            self.server_status_label.setStyleSheet("color: gray; font-weight: bold; padding: 5px;")
            self.start_server_btn.setEnabled(True)
            self.stop_server_btn.setEnabled(False)
            self.connection_info.append("服务器已停止")
    
    def test_connection(self):
        """测试连接"""
        dialog = TestConnectionDialog(self.port_spin.value(), self.token_input.text(), self)
        dialog.exec()
    
    def on_server_log(self, message):
        """服务器日志"""
        self.connection_info.append(message)
    
    def on_client_connected(self, address):
        """客户端连接"""
        self.connection_info.append(f"✅ 客户端连接: {address}")
    
    def on_client_disconnected(self, address):
        """客户端断开"""
        self.connection_info.append(f"❌ 客户端断开: {address}")
    
    def on_variable_updated(self, name, value):
        """变量更新"""
        # 同步到auto_monitor
        if self.auto_monitor:
            self.auto_monitor.global_variables[name] = value
    
    def add_broadcast_config(self):
        """添加广播配置"""
        dialog = BroadcastConfigDialog(self)
        if dialog.exec():
            config = dialog.get_config()
            self.broadcast_configs.append(config)
            self.refresh_broadcast_list()
            self.mark_dirty()
    
    def edit_broadcast_config(self):
        """编辑广播配置"""
        current = self.broadcast_list.currentRow()
        if current >= 0:
            dialog = BroadcastConfigDialog(self, self.broadcast_configs[current])
            if dialog.exec():
                self.broadcast_configs[current] = dialog.get_config()
                self.refresh_broadcast_list()
                self.mark_dirty()
    
    def remove_broadcast_config(self):
        """删除广播配置"""
        current = self.broadcast_list.currentRow()
        if current >= 0:
            del self.broadcast_configs[current]
            self.refresh_broadcast_list()
            self.mark_dirty()
    
    def refresh_broadcast_list(self):
        """刷新广播列表"""
        self.broadcast_list.clear()
        for config in self.broadcast_configs:
            var_name = config.get('variable', '')
            interval = config.get('interval', 1.0)
            self.broadcast_list.addItem(f"{var_name} (每{interval}秒)")
    
    def add_fetch_config(self):
        """添加获取配置"""
        dialog = FetchConfigDialog(self)
        if dialog.exec():
            config = dialog.get_config()
            self.fetch_configs.append(config)
            self.refresh_fetch_list()
            self.mark_dirty()
    
    def edit_fetch_config(self):
        """编辑获取配置"""
        current = self.fetch_list.currentRow()
        if current >= 0:
            dialog = FetchConfigDialog(self, self.fetch_configs[current])
            if dialog.exec():
                self.fetch_configs[current] = dialog.get_config()
                self.refresh_fetch_list()
                self.mark_dirty()
    
    def remove_fetch_config(self):
        """删除获取配置"""
        current = self.fetch_list.currentRow()
        if current >= 0:
            del self.fetch_configs[current]
            self.refresh_fetch_list()
            self.mark_dirty()

    
    def load_settings(self):
        """加载设置"""
        try:
            if os.path.exists(self.settings_file):
                with open(self.settings_file, 'r', encoding='utf-8') as f:
                    settings = json.load(f)
                    
                self.port_spin.setValue(settings.get('port', 9527))
                self.token_input.setText(settings.get('token', ''))
                self.enable_broadcast_check.setChecked(settings.get('enable_broadcast', True))
                self.enable_receive_check.setChecked(settings.get('enable_receive', True))
                self.broadcast_interval_spin.setValue(settings.get('broadcast_interval', 1.0))
                self.broadcast_configs = settings.get('broadcast_configs', [])
                self.fetch_configs = settings.get('fetch_configs', [])
                
                self.refresh_broadcast_list()
                self.refresh_fetch_list()
                
                # 将获取配置同步到auto_monitor
                if self.auto_monitor:
                    self.auto_monitor.set_fetch_configs(self.fetch_configs)
                
                # 恢复服务器状态
                if settings.get('server_running', False) and self.auto_monitor:
                    QTimer.singleShot(500, lambda: self.start_server())
        except Exception as e:
            print(f"加载高级监控设置失败: {e}")
        
        self.is_dirty = False
    
    def mark_dirty(self):
        """标记设置已更改，需要保存"""
        self.is_dirty = True
    
    def auto_save_settings(self):
        """自动保存设置"""
        if hasattr(self, 'is_dirty') and self.is_dirty:
            self.save_settings()
            self.show_save_status()
            self.is_dirty = False
    
    def show_save_status(self):
        """显示保存状态"""
        self.save_status_label.show()
        QTimer.singleShot(2000, lambda: self.save_status_label.hide())
    
    def save_settings(self):
        """保存设置"""
        settings = {
            'port': self.port_spin.value(),
            'token': self.token_input.text(),
            'enable_broadcast': self.enable_broadcast_check.isChecked(),
            'enable_receive': self.enable_receive_check.isChecked(),
            'broadcast_interval': self.broadcast_interval_spin.value(),
            'broadcast_configs': self.broadcast_configs,
            'fetch_configs': self.fetch_configs,
            'server_running': self.variable_server.running if self.variable_server else False
        }
        
        try:
            with open(self.settings_file, 'w', encoding='utf-8') as f:
                json.dump(settings, f, indent=2, ensure_ascii=False)
            return True
        except Exception as e:
            print(f"保存高级监控设置失败: {e}")
            return False
    

    
    def closeEvent(self, event):
        """关闭事件"""
        # 停止自动保存定时器
        if hasattr(self, 'auto_save_timer'):
            self.auto_save_timer.stop()
        
        # 最后保存一次
        if hasattr(self, 'is_dirty') and self.is_dirty:
            self.save_settings()
        
        # 更新获取配置到auto_monitor
        if self.auto_monitor:
            self.auto_monitor.set_fetch_configs(self.fetch_configs)
        
        # 询问是否停止服务器
        if self.variable_server and self.variable_server.running:
            reply = QMessageBox.question(
                self, "确认",
                "服务器正在运行，关闭后服务器将继续在后台运行。\n是否停止服务器？",
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No | QMessageBox.StandardButton.Cancel,
                QMessageBox.StandardButton.No
            )
            if reply == QMessageBox.StandardButton.Cancel:
                event.ignore()
                return
            elif reply == QMessageBox.StandardButton.Yes:
                self.stop_server()
        
        super().closeEvent(event)


class TestConnectionDialog(QDialog):
    """测试连接对话框"""
    
    def __init__(self, default_port, default_token, parent=None):
        super().__init__(parent)
        self.setWindowTitle("测试连接（作为客户端）")
        self.setModal(True)
        
        layout = QVBoxLayout(self)
        
        # 说明
        info_label = QLabel(
            "📥 <b>测试模式：TCP客户端</b>\n"
            "此测试将作为客户端连接到指定的TCP服务器。"
        )
        info_label.setStyleSheet("padding: 10px; background-color: #f5f5f5; margin-bottom: 10px;")
        layout.addWidget(info_label)
        
        # 表单
        form_layout = QFormLayout()
        
        self.host_input = QLineEdit("localhost")
        self.host_input.setPlaceholderText("输入服务器IP地址")
        self.port_spin = QSpinBox()
        self.port_spin.setRange(1024, 65535)
        self.port_spin.setValue(default_port)
        
        self.token_input = QLineEdit(default_token)
        self.token_input.setPlaceholderText("留空则不使用Token")
        
        form_layout.addRow("TCP服务器地址:", self.host_input)
        form_layout.addRow("TCP端口:", self.port_spin)
        form_layout.addRow("认证Token:", self.token_input)
        
        layout.addLayout(form_layout)
        
        # 测试按钮
        test_btn = QPushButton("测试")
        test_btn.clicked.connect(self.test)
        layout.addRow(test_btn)
        
        # 结果显示
        self.result_text = QTextEdit()
        self.result_text.setReadOnly(True)
        self.result_text.setMaximumHeight(150)
        layout.addRow("测试结果:", self.result_text)
        
        # 关闭按钮
        close_btn = QPushButton("关闭")
        close_btn.clicked.connect(self.accept)
        layout.addRow(close_btn)

class BroadcastConfigDialog(QDialog):
    """广播配置对话框"""
    
    def __init__(self, parent=None, config=None):
        super().__init__(parent)
        self.config = config or {}
        self.setWindowTitle("广播配置（服务器端）")
        self.setModal(True)
        
        layout = QVBoxLayout(self)
        
        # 说明
        info = QLabel(
            "📡 配置要广播的变量\n"
            "当变量值改变时，将自动推送给所有连接的客户端"
        )
        info.setStyleSheet("padding: 8px; background-color: #e8f5e9;")
        layout.addWidget(info)
        
        form_layout = QFormLayout()
        
        self.variable_input = QLineEdit()
        self.variable_input.setPlaceholderText("例如: counter")
        self.variable_input.setText(self.config.get('variable', ''))
        
        self.interval_spin = QDoubleSpinBox()
        self.interval_spin.setRange(0.1, 60)
        self.interval_spin.setValue(self.config.get('interval', 1.0))
        self.interval_spin.setSuffix(" 秒")
        
        form_layout.addRow("变量名:", self.variable_input)
        form_layout.addRow("检查间隔:", self.interval_spin)
        
        layout.addLayout(form_layout)
        
        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
    
    def get_config(self):
        return {
            'variable': self.variable_input.text(),
            'interval': self.interval_spin.value()
        }


class FetchConfigDialog(QDialog):
    """获取配置对话框"""
    
    def __init__(self, parent=None, config=None):
        super().__init__(parent)
        self.config = config or {}
        self.setWindowTitle("获取配置（客户端）")
        self.setModal(True)
        
        layout = QVBoxLayout(self)
        
        # 说明
        info = QLabel(
            "📥 配置从远程服务器获取的变量\n"
            "将作为TCP客户端连接到指定服务器"
        )
        info.setStyleSheet("padding: 8px; background-color: #e3f2fd;")
        layout.addWidget(info)
        
        form_layout = QFormLayout()
        
        self.host_input = QLineEdit()
        self.host_input.setPlaceholderText("例如: 192.168.1.100")
        self.host_input.setText(self.config.get('host', 'localhost'))
        self.host_input.setToolTip("要连接的TCP服务器IP地址")
        
        self.port_spin = QSpinBox()
        self.port_spin.setRange(1024, 65535)
        self.port_spin.setValue(self.config.get('port', 9527))
        self.port_spin.setToolTip("TCP服务器端口")
        
        self.token_input = QLineEdit()
        self.token_input.setPlaceholderText("留空则不使用Token")
        self.token_input.setText(self.config.get('token', ''))
        self.token_input.setEchoMode(QLineEdit.EchoMode.Password)
        
        self.variable_input = QLineEdit()
        self.variable_input.setPlaceholderText("例如: counter")
        self.variable_input.setText(self.config.get('variable', ''))
        self.variable_input.setToolTip("要获取的变量名称")
        
        self.interval_spin = QDoubleSpinBox()
        self.interval_spin.setRange(0.5, 300)
        self.interval_spin.setValue(self.config.get('interval', 5.0))
        self.interval_spin.setSuffix(" 秒")
        self.interval_spin.setToolTip("从服务器拉取变量的间隔")
        
        form_layout.addRow("TCP服务器:", self.host_input)
        form_layout.addRow("端口:", self.port_spin)
        form_layout.addRow("认证Token:", self.token_input)
        form_layout.addRow("变量名:", self.variable_input)
        form_layout.addRow("拉取间隔:", self.interval_spin)
        
        layout.addLayout(form_layout)
        
        buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
    
    def get_config(self):
        return {
            'host': self.host_input.text(),
            'port': self.port_spin.value(),
            'token': self.token_input.text(),
            'variable': self.variable_input.text(),
            'interval': self.interval_spin.value()
        }