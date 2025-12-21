"""
网络通信协议定义
定义变量服务器的消息格式和协议

协议类型: TCP Socket (原生TCP协议)
端口: 默认9527
数据格式: JSON (UTF-8编码)
消息结构: 每条消息为独立的JSON对象

特点:
- 使用持久TCP连接，减少握手开销
- 低延迟，适合实时变量同步
- 支持双向通信
- 可选Token认证
"""

import json
from typing import Dict, Any, Optional, List
from datetime import datetime


class MessageType:
    """消息类型常量"""
    # 变量操作
    SET_VARIABLE = "set_variable"      # 设置变量
    GET_VARIABLE = "get_variable"      # 获取单个变量
    GET_ALL_VARIABLES = "get_all"      # 获取所有变量
    DELETE_VARIABLE = "delete_variable" # 删除变量
    CLEAR_VARIABLES = "clear_all"      # 清空所有变量
    SYNC_VARIABLES = "sync_variables"  # 批量同步变量
    
    # 广播
    BROADCAST = "broadcast"             # 广播变量更新
    SUBSCRIBE = "subscribe"             # 订阅变量更新
    UNSUBSCRIBE = "unsubscribe"       # 取消订阅
    
    # 系统
    PING = "ping"                      # 连接测试
    AUTH = "auth"                      # 身份验证
    ERROR = "error"                    # 错误消息
    SUCCESS = "success"                # 成功响应


class NetworkMessage:
    """网络消息封装类"""
    
    @staticmethod
    def create(msg_type: str, data: Optional[Dict] = None, 
               token: Optional[str] = None) -> str:
        """
        创建消息
        
        Args:
            msg_type: 消息类型
            data: 消息数据
            token: 认证令牌
        
        Returns:
            JSON格式的消息字符串
        """
        message = {
            "type": msg_type,
            "timestamp": datetime.now().isoformat(),
            "data": data or {}
        }
        
        if token:
            message["token"] = token
            
        return json.dumps(message, ensure_ascii=False)
    
    @staticmethod
    def parse(message: str) -> Dict[str, Any]:
        """
        解析消息
        
        Args:
            message: JSON格式的消息字符串
        
        Returns:
            解析后的消息字典
        """
        try:
            return json.loads(message)
        except json.JSONDecodeError:
            return {
                "type": MessageType.ERROR,
                "data": {"error": "Invalid JSON format"}
            }
    
    @staticmethod
    def create_set_variable(name: str, value: Any, token: Optional[str] = None) -> str:
        """创建设置变量消息"""
        return NetworkMessage.create(
            MessageType.SET_VARIABLE,
            {"name": name, "value": value},
            token
        )
    
    @staticmethod
    def create_get_variable(name: str, token: Optional[str] = None) -> str:
        """创建获取变量消息"""
        return NetworkMessage.create(
            MessageType.GET_VARIABLE,
            {"name": name},
            token
        )
    
    @staticmethod
    def create_broadcast(variables: Dict[str, Any], token: Optional[str] = None) -> str:
        """创建广播消息"""
        return NetworkMessage.create(
            MessageType.BROADCAST,
            {"variables": variables},
            token
        )
    
    @staticmethod
    def create_subscribe(variable_names: List[str], token: Optional[str] = None) -> str:
        """创建订阅消息"""
        return NetworkMessage.create(
            MessageType.SUBSCRIBE,
            {"variables": variable_names},
            token
        )
    
    @staticmethod
    def create_auth(token: str) -> str:
        """创建认证消息"""
        return NetworkMessage.create(MessageType.AUTH, token=token)
    
    @staticmethod
    def create_error(error_msg: str) -> str:
        """创建错误消息"""
        return NetworkMessage.create(
            MessageType.ERROR,
            {"error": error_msg}
        )
    
    @staticmethod
    def create_success(data: Optional[Dict] = None) -> str:
        """创建成功响应消息"""
        return NetworkMessage.create(MessageType.SUCCESS, data)


# 示例消息格式文档
SAMPLE_MESSAGES = """
# 变量服务器通信协议示例

## 1. 设置变量
请求:
{
    "type": "set_variable",
    "timestamp": "2024-01-01T12:00:00",
    "data": {
        "name": "counter",
        "value": 10
    },
    "token": "your_token_here"
}

响应:
{
    "type": "success",
    "timestamp": "2024-01-01T12:00:01",
    "data": {
        "name": "counter",
        "value": 10
    }
}

## 2. 获取变量
请求:
{
    "type": "get_variable",
    "timestamp": "2024-01-01T12:00:00",
    "data": {
        "name": "counter"
    },
    "token": "your_token_here"
}

响应:
{
    "type": "success",
    "timestamp": "2024-01-01T12:00:01",
    "data": {
        "name": "counter",
        "value": 10
    }
}

## 3. 获取所有变量
请求:
{
    "type": "get_all",
    "timestamp": "2024-01-01T12:00:00",
    "token": "your_token_here"
}

响应:
{
    "type": "success",
    "timestamp": "2024-01-01T12:00:01",
    "data": {
        "variables": {
            "counter": 10,
            "status": "running",
            "score": 85
        }
    }
}

## 4. 广播变量更新
{
    "type": "broadcast",
    "timestamp": "2024-01-01T12:00:00",
    "data": {
        "variables": {
            "counter": 15,
            "status": "stopped"
        }
    }
}

## 5. 订阅变量
请求:
{
    "type": "subscribe",
    "timestamp": "2024-01-01T12:00:00",
    "data": {
        "variables": ["counter", "status"]
    },
    "token": "your_token_here"
}

## 6. 连接测试
请求:
{
    "type": "ping",
    "timestamp": "2024-01-01T12:00:00"
}

响应:
{
    "type": "success",
    "timestamp": "2024-01-01T12:00:01",
    "data": {
        "message": "pong"
    }
}

## 7. 错误响应
{
    "type": "error",
    "timestamp": "2024-01-01T12:00:01",
    "data": {
        "error": "Variable not found: unknown_var"
    }
}
"""


def get_sample_file_content() -> str:
    """获取示例文件内容"""
    return SAMPLE_MESSAGES