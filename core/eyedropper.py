"""
滴管工具模块 - 用于从Scrcpy窗口拾取坐标（使用钩子阻止事件传递）
"""

import time
import win32gui
import win32con
import ctypes
from ctypes import wintypes
from PyQt6.QtCore import QObject, QTimer, pyqtSignal, Qt
from PyQt6.QtWidgets import QApplication
from core.window_capture import WindowCapture


# Windows常量
WH_MOUSE_LL = 14
WM_LBUTTONDOWN = 0x0201

# 定义结构体
class POINT(ctypes.Structure):
    _fields_ = [("x", wintypes.LONG), ("y", wintypes.LONG)]

class MSLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [
        ("pt", POINT),
        ("mouseData", wintypes.DWORD),
        ("flags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.POINTER(wintypes.ULONG))
    ]


class EyeDropper(QObject):
    """滴管工具类"""
    
    # 信号
    coordinate_updated = pyqtSignal(int, int)  # 设备坐标更新
    coordinate_picked = pyqtSignal(int, int)   # 坐标被选取
    screen_coordinate_updated = pyqtSignal(int, int)  # 屏幕坐标更新
    
    def __init__(self, controller=None):
        super().__init__()
        self.controller = controller
        self.active = False
        self.last_click_time = 0
        self.click_threshold = 0.3
        self.update_timer = QTimer()
        self.update_timer.timeout.connect(self.update_coordinates)
        
        # 钩子相关
        self.hook_id = None
        self.hook_proc = None
        
    def start(self):
        """启动滴管模式"""
        if self.active:
            return
            
        print("启动滴管模式")
        self.active = True
        self.last_click_time = 0
        
        # 设置鼠标样式为十字
        QApplication.setOverrideCursor(Qt.CursorShape.CrossCursor)
        
        # 安装钩子
        self.install_hook()
        
        # 开始坐标更新
        self.update_timer.start(30)
        
    def stop(self):
        """停止滴管模式"""
        if not self.active:
            return
            
        print("停止滴管模式")
        self.active = False
        
        # 停止定时器
        self.update_timer.stop()
        
        # 卸载钩子
        self.uninstall_hook()
        
        # 恢复鼠标样式
        QApplication.restoreOverrideCursor()
        
    def install_hook(self):
        """安装鼠标钩子"""
        # 定义 CallNextHookEx 函数原型
        user32 = ctypes.windll.user32
        CallNextHookEx = user32.CallNextHookEx
        CallNextHookEx.argtypes = [wintypes.HHOOK, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM]
        CallNextHookEx.restype = wintypes.LPARAM
        
        # 定义钩子回调函数类型
        HOOKPROC = ctypes.WINFUNCTYPE(wintypes.LPARAM, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)
        
        def mouse_hook_proc(nCode, wParam, lParam):
            """鼠标钩子回调"""
            try:
                if nCode >= 0 and self.active and wParam == WM_LBUTTONDOWN:
                    # 解析鼠标事件结构
                    mouse_info = ctypes.cast(lParam, ctypes.POINTER(MSLLHOOKSTRUCT)).contents
                    x = mouse_info.pt.x
                    y = mouse_info.pt.y
                    
                    # 检查是否在Scrcpy窗口内
                    if self.is_point_in_scrcpy_window(x, y):
                        print(f"拦截Scrcpy窗口点击: ({x}, {y})")
                        # 使用QTimer在主线程中处理
                        QTimer.singleShot(0, lambda: self.handle_click(x, y))
                        # 返回1阻止事件传递
                        return 1
                        
            except Exception as e:
                print(f"钩子错误: {e}")
                import traceback
                traceback.print_exc()
            
            # 调用下一个钩子
            return CallNextHookEx(None, nCode, wParam, lParam)
        
        # 保存回调函数（防止被垃圾回收）
        self.hook_proc = HOOKPROC(mouse_hook_proc)
        
        # 定义 SetWindowsHookEx 函数原型
        SetWindowsHookEx = user32.SetWindowsHookExA
        SetWindowsHookEx.argtypes = [ctypes.c_int, HOOKPROC, wintypes.HINSTANCE, wintypes.DWORD]
        SetWindowsHookEx.restype = wintypes.HHOOK
        
        # 安装钩子 (低级钩子使用0作为hMod参数)
        self.hook_id = SetWindowsHookEx(
            WH_MOUSE_LL,
            self.hook_proc,
            None,  # 对于低级钩子，hMod必须为NULL
            0      # dwThreadId为0表示全局钩子
        )
        
        if self.hook_id:
            print(f"钩子安装成功: {self.hook_id}")
        else:
            error = ctypes.windll.kernel32.GetLastError()
            print(f"钩子安装失败，错误码: {error}")
    
    def uninstall_hook(self):
        """卸载钩子"""
        if self.hook_id:
            result = ctypes.windll.user32.UnhookWindowsHookEx(self.hook_id)
            if result:
                print("钩子卸载成功")
            else:
                print("钩子卸载失败")
            self.hook_id = None
            self.hook_proc = None
    
    def is_point_in_scrcpy_window(self, x, y):
        """检查点是否在Scrcpy窗口内"""
        try:
            hwnd = WindowCapture.find_scrcpy_window()
            if not hwnd:
                return False
                
            rect = win32gui.GetClientRect(hwnd)
            point = win32gui.ClientToScreen(hwnd, (0, 0))
            
            left = point[0]
            top = point[1]
            right = point[0] + rect[2]
            bottom = point[1] + rect[3]
            
            return left <= x <= right and top <= y <= bottom
            
        except Exception as e:
            print(f"检查窗口位置错误: {e}")
            return False
    
    def handle_click(self, screen_x, screen_y):
        """处理点击事件"""
        if not self.active:
            return
            
        current_time = time.time()
        if current_time - self.last_click_time < self.click_threshold:
            return
            
        self.last_click_time = current_time
        print(f"处理点击: ({screen_x}, {screen_y})")
        
        # 转换为设备坐标
        device_coords = self.screen_to_device(screen_x, screen_y)
        if device_coords:
            device_x, device_y = device_coords
            print(f"设备坐标: ({device_x}, {device_y})")
            self.coordinate_picked.emit(device_x, device_y)
            
        # 延迟停止
        QTimer.singleShot(100, self.stop)
    
    def update_coordinates(self):
        """更新坐标显示"""
        if not self.active:
            return
            
        try:
            # 获取鼠标位置
            cursor_pos = win32gui.GetCursorPos()
            self.screen_coordinate_updated.emit(cursor_pos[0], cursor_pos[1])
            
            # 转换为设备坐标
            device_coords = self.screen_to_device(cursor_pos[0], cursor_pos[1])
            if device_coords:
                self.coordinate_updated.emit(device_coords[0], device_coords[1])
            else:
                self.coordinate_updated.emit(-1, -1)
                
        except Exception as e:
            print(f"坐标更新错误: {e}")
            self.coordinate_updated.emit(-1, -1)
    
    def screen_to_device(self, screen_x, screen_y):
        """屏幕坐标转设备坐标"""
        try:
            hwnd = WindowCapture.find_scrcpy_window()
            if not hwnd:
                return None
                
            rect = win32gui.GetClientRect(hwnd)
            point = win32gui.ClientToScreen(hwnd, (0, 0))
            
            # 窗口客户区坐标
            left = point[0]
            top = point[1]
            width = rect[2]
            height = rect[3]
            
            # 检查是否在窗口内
            if not (left <= screen_x <= left + width and top <= screen_y <= top + height):
                return None
            
            # 相对坐标
            rel_x = screen_x - left
            rel_y = screen_y - top
            
            if not self.controller or width <= 0 or height <= 0:
                return (int(rel_x), int(rel_y))
            
            # 获取设备分辨率
            device_width, device_height = self.controller.get_device_resolution()
            
            # 判断方向
            window_aspect = width / height
            if window_aspect > 1.3:  # 横屏
                actual_width = max(device_width, device_height)
                actual_height = min(device_width, device_height)
            else:  # 竖屏
                actual_width = min(device_width, device_height)
                actual_height = max(device_width, device_height)
            
            # 转换
            device_x = int(rel_x * actual_width / width)
            device_y = int(rel_y * actual_height / height)
            
            # 限制范围
            device_x = max(0, min(device_x, actual_width - 1))
            device_y = max(0, min(device_y, actual_height - 1))
            
            return (device_x, device_y)
            
        except Exception as e:
            print(f"坐标转换错误: {e}")
            return None
    
    def get_current_device_coordinates(self):
        """获取当前设备坐标（同步）"""
        try:
            cursor_pos = win32gui.GetCursorPos()
            return self.screen_to_device(cursor_pos[0], cursor_pos[1])
        except:
            return None
    
    def __del__(self):
        """析构"""
        self.uninstall_hook()