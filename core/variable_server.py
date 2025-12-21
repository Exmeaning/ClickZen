"""
变量服务器模块
提供网络变量广播和接收功能
"""

import socket
import threading
import json
import time
from typing import Dict, Any, Optional, Callable, Set
from PyQt6.QtCore import QObject, pyqtSignal
from utils.network_protocol import NetworkMessage, MessageType
import select


class VariableServer(QObject):
    """变量服务器 - 处理变量的网络广播和接收"""
    
    # 信号
    variable_updated = pyqtSignal(str, object)  # 变量名, 值
    client_connected = pyqtSignal(str)          # 客户端地址
    client_disconnected = pyqtSignal(str)       # 客户端地址
    error_occurred = pyqtSignal(str)            # 错误消息
    log_message = pyqtSignal(str)               # 日志消息
    
    def __init__(self, port: int = 9527, token: Optional[str] = None):
        super().__init__()
        self.port = port
        self.token = token
        self.running = False
        self.server_socket = None
        self.server_thread = None
        self.clients = {}  # {address: socket}
        self.subscriptions = {}  # {address: set(variable_names)}
        self.variables = {}  # 本地变量存储
        self.broadcast_enabled = False
        self.receive_enabled = False
        
    def start(self, broadcast: bool = True, receive: bool = True) -> bool:
        """
        启动服务器
        
        Args:
            broadcast: 是否启用广播
            receive: 是否接收变量
        """
        if self.running:
            return True
            
        self.broadcast_enabled = broadcast
        self.receive_enabled = receive
        
        try:
            self.server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self.server_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            self.server_socket.bind(('0.0.0.0', self.port))
            self.server_socket.listen(5)
            self.server_socket.setblocking(False)
            
            self.running = True
            self.server_thread = threading.Thread(target=self._server_loop, daemon=True)
            self.server_thread.start()
            
            self.log_message.emit(f"变量服务器启动在端口 {self.port}")
            return True
            
        except Exception as e:
            self.error_occurred.emit(f"启动服务器失败: {str(e)}")
            return False
    
    def stop(self):
        """停止服务器"""
        self.running = False
        
        # 关闭所有客户端连接
        for client_socket in self.clients.values():
            try:
                client_socket.close()
            except:
                pass
        self.clients.clear()
        self.subscriptions.clear()
        
        # 关闭服务器socket
        if self.server_socket:
            try:
                self.server_socket.close()
            except:
                pass
            self.server_socket = None
        
        # 等待线程结束
        if self.server_thread:
            self.server_thread.join(timeout=2)
            self.server_thread = None
        
        self.log_message.emit("变量服务器已停止")
    
    def _server_loop(self):
        """服务器主循环"""
        while self.running:
            try:
                # 使用select检查是否有新连接或数据
                readable, _, exceptional = select.select(
                    [self.server_socket] + list(self.clients.values()),
                    [], 
                    [self.server_socket] + list(self.clients.values()),
                    0.1
                )
                
                # 处理异常的socket
                for sock in exceptional:
                    if sock == self.server_socket:
                        self.error_occurred.emit("服务器socket异常")
                    else:
                        # 查找并移除异常的客户端
                        for addr, client_sock in list(self.clients.items()):
                            if client_sock == sock:
                                self._remove_client(addr)
                                break
                
                # 处理可读的socket
                for sock in readable:
                    if sock == self.server_socket:
                        # 接受新连接
                        try:
                            client_socket, address = self.server_socket.accept()
                            # 设置为阻塞模式，使用超时
                            client_socket.settimeout(1.0)
                            addr_str = f"{address[0]}:{address[1]}"
                            self.clients[addr_str] = client_socket
                            self.client_connected.emit(addr_str)
                            self.log_message.emit(f"客户端连接: {addr_str}")
                        except Exception as e:
                            self.error_occurred.emit(f"接受连接失败: {str(e)}")
                    else:
                        # 处理客户端数据
                        self._handle_client_data(sock)
                        
            except Exception as e:
                if self.running:
                    self.error_occurred.emit(f"服务器错误: {str(e)}")
                    import traceback
                    print("服务器循环错误:")
                    print(traceback.format_exc())
            
            time.sleep(0.01)
    
    def _handle_client_data(self, client_socket):
        """处理客户端数据"""
        # 查找客户端地址
        client_addr = None
        for addr, sock in self.clients.items():
            if sock == client_socket:
                client_addr = addr
                break
        
        if not client_addr:
            return
        
        try:
            # 接收数据（可能包含多个JSON消息）
            data = client_socket.recv(4096)
            if not data:
                # 客户端断开连接
                self._remove_client(client_addr)
                return
            
            # 解码数据
            data_str = data.decode('utf-8')
            self.log_message.emit(f"收到数据 from {client_addr}: {data_str[:100]}")
            
            # 尝试解析为JSON（可能有多个消息）
            # 简单处理：假设每个消息以换行符分隔
            messages = data_str.strip().split('\n')
            
            for msg_str in messages:
                if not msg_str:
                    continue
                    
                try:
                    # 解析消息
                    message = NetworkMessage.parse(msg_str)
                    
                    # 验证令牌（如果设置了）
                    if self.token and message.get('type') != 'ping':  # ping不需要token
                        if message.get('token') != self.token:
                            response = NetworkMessage.create_error("Invalid token")
                            client_socket.send((response + '\n').encode('utf-8'))
                            continue
                    
                    # 处理不同类型的消息
                    self._process_message(message, client_socket, client_addr)
                    
                except json.JSONDecodeError as e:
                    self.error_occurred.emit(f"JSON解析错误: {str(e)}")
                    response = NetworkMessage.create_error(f"Invalid JSON: {str(e)}")
                    client_socket.send((response + '\n').encode('utf-8'))
                    
        except socket.timeout:
            # 超时是正常的，继续
            pass
        except socket.error as e:
            # 连接错误，移除客户端
            self.error_occurred.emit(f"Socket错误: {str(e)}")
            self._remove_client(client_addr)
        except Exception as e:
            self.error_occurred.emit(f"处理客户端数据错误: {str(e)}")
            import traceback
            print("处理客户端数据错误:")
            print(traceback.format_exc())
    
    def _process_message(self, message: Dict, client_socket, client_addr: str):
        """处理消息"""
        msg_type = message.get('type')
        data = message.get('data', {})
        
        self.log_message.emit(f"处理消息类型: {msg_type} from {client_addr}")
        
        try:
            if msg_type == MessageType.PING:
                # 连接测试
                response = NetworkMessage.create_success({"message": "pong"})
                client_socket.send((response + '\n').encode('utf-8'))
                self.log_message.emit(f"响应PING from {client_addr}")
            
            elif msg_type == MessageType.SET_VARIABLE and self.receive_enabled:
                # 设置变量
                name = data.get('name')
                value = data.get('value')
                if name:
                    self.variables[name] = value
                    self.variable_updated.emit(name, value)
                    response = NetworkMessage.create_success({"name": name, "value": value})
                    client_socket.send((response + '\n').encode('utf-8'))
                    self.log_message.emit(f"设置变量: {name} = {value}")
                    
                    # 广播给订阅者
                    if self.broadcast_enabled:
                        self._broadcast_variable(name, value)
                else:
                    response = NetworkMessage.create_error("Variable name required")
                    client_socket.send((response + '\n').encode('utf-8'))
                
            elif msg_type == MessageType.GET_VARIABLE:
                # 获取变量
                name = data.get('name')
                if name in self.variables:
                    response = NetworkMessage.create_success({
                        "name": name,
                        "value": self.variables[name]
                    })
                    self.log_message.emit(f"返回变量: {name} = {self.variables[name]}")
                else:
                    response = NetworkMessage.create_error(f"Variable not found: {name}")
                    self.log_message.emit(f"变量不存在: {name}")
                client_socket.send((response + '\n').encode('utf-8'))
                
            elif msg_type == MessageType.GET_ALL_VARIABLES:
                # 获取所有变量
                response = NetworkMessage.create_success({"variables": self.variables})
                client_socket.send((response + '\n').encode('utf-8'))
                self.log_message.emit(f"返回所有变量: {self.variables}")
                
            elif msg_type == MessageType.SUBSCRIBE:
                # 订阅变量
                var_names = data.get('variables', [])
                if client_addr not in self.subscriptions:
                    self.subscriptions[client_addr] = set()
                self.subscriptions[client_addr].update(var_names)
                response = NetworkMessage.create_success({"subscribed": var_names})
                client_socket.send((response + '\n').encode('utf-8'))
                self.log_message.emit(f"客户端订阅: {var_names}")
                
            elif msg_type == MessageType.UNSUBSCRIBE:
                # 取消订阅
                if client_addr in self.subscriptions:
                    del self.subscriptions[client_addr]
                response = NetworkMessage.create_success()
                client_socket.send((response + '\n').encode('utf-8'))
                
            elif msg_type == MessageType.CLEAR_VARIABLES:
                # 清空变量
                self.variables.clear()
                response = NetworkMessage.create_success()
                client_socket.send((response + '\n').encode('utf-8'))
                self.log_message.emit("已清空所有变量")
                
            elif msg_type == "sync_variables":
                # 批量同步变量
                vars_to_sync = data.get('variables', {})
                updated_count = 0
                for name, value in vars_to_sync.items():
                    self.variables[name] = value
                    self.variable_updated.emit(name, value)
                    updated_count += 1
                    # 广播给其他客户端
                    if self.broadcast_enabled:
                        self._broadcast_variable(name, value)
                
                response = NetworkMessage.create_success({
                    "updated": updated_count,
                    "message": f"Synchronized {updated_count} variables"
                })
                client_socket.send((response + '\n').encode('utf-8'))
                self.log_message.emit(f"批量同步 {updated_count} 个变量")
                
            elif msg_type == MessageType.SUCCESS or msg_type == "success":
                # 处理客户端的成功响应（通常是对广播的确认）
                msg_text = data.get('message', 'ACK')
                self.log_message.emit(f"收到确认 from {client_addr}: {msg_text}")
                # 不需要再回复响应，避免死循环
                
            elif msg_type == MessageType.ERROR or msg_type == "error":
                # 处理客户端报告的错误
                error_msg = data.get('error', 'Unknown error')
                self.log_message.emit(f"客户端错误 from {client_addr}: {error_msg}")
                
            else:
                # 未知消息类型
                response = NetworkMessage.create_error(f"Unknown message type: {msg_type}")
                client_socket.send((response + '\n').encode('utf-8'))
                self.log_message.emit(f"未知消息类型: {msg_type}")
                
        except Exception as e:
            self.error_occurred.emit(f"处理消息错误: {str(e)}")
            import traceback
            print("处理消息错误:")
            print(traceback.format_exc())
    
    def _remove_client(self, client_addr: str):
        """移除客户端"""
        if client_addr in self.clients:
            try:
                self.clients[client_addr].close()
            except:
                pass
            del self.clients[client_addr]
            
        if client_addr in self.subscriptions:
            del self.subscriptions[client_addr]
            
        self.client_disconnected.emit(client_addr)
        self.log_message.emit(f"客户端断开: {client_addr}")
    
    def _broadcast_variable(self, name: str, value: Any):
        """广播变量给订阅者"""
        if not self.broadcast_enabled:
            return
            
        message = NetworkMessage.create_broadcast({name: value})
        
        # 发送给所有订阅了这个变量的客户端
        for addr, var_names in self.subscriptions.items():
            if name in var_names and addr in self.clients:
                try:
                    self.clients[addr].send(message.encode('utf-8'))
                except:
                    # 发送失败，可能客户端已断开
                    self._remove_client(addr)
    
    def set_variable(self, name: str, value: Any):
        """设置变量并广播"""
        self.variables[name] = value
        if self.broadcast_enabled:
            self._broadcast_variable(name, value)
    
    def get_variable(self, name: str) -> Optional[Any]:
        """获取变量值"""
        return self.variables.get(name)
    
    def get_all_variables(self) -> Dict[str, Any]:
        """获取所有变量"""
        return self.variables.copy()
    
    def clear_variables(self):
        """清空所有变量"""
        self.variables.clear()
