"""Windows窗口截图工具"""

import ctypes
import win32gui
import win32ui
import win32con
import win32api
from PIL import Image
import numpy as np


class WindowCapture:
    """窗口截图类"""

    # 类变量，控制日志输出
    _last_found_hwnd = None
    _log_enabled = False  # 默认关闭日志

    @staticmethod
    def find_scrcpy_window():
        """查找Scrcpy窗口 - 减少日志输出"""
        def enum_callback(hwnd, windows):
            if win32gui.IsWindowVisible(hwnd):
                window_text = win32gui.GetWindowText(hwnd)
                class_name = win32gui.GetClassName(hwnd)

                # Scrcpy使用SDL库，类名是 "SDL_app"
                if class_name == "SDL_app":
                    # 检查是否真的是Scrcpy窗口
                    if "Scrcpy" in window_text or "-" in window_text:
                        windows.append((hwnd, window_text))
                        # 只在第一次找到或窗口改变时输出日志
                        if WindowCapture._last_found_hwnd != hwnd or WindowCapture._log_enabled:
                            print(f"[WindowCapture] 找到Scrcpy窗口: '{window_text}'")
                            WindowCapture._last_found_hwnd = hwnd
                    elif window_text == "Phone Controller":
                        windows.append((hwnd, window_text))

            return True

        windows = []
        win32gui.EnumWindows(enum_callback, windows)

        if windows:
            # 优先选择包含"Scrcpy"的窗口
            for hwnd, title in windows:
                if "Scrcpy" in title:
                    return hwnd
            return windows[0][0]

        # 只在找不到时输出一次
        if WindowCapture._last_found_hwnd is not None:
            print("[WindowCapture] Scrcpy窗口已关闭")
            WindowCapture._last_found_hwnd = None
        return None

    @staticmethod
    def _printwindow_capture(hwnd):
        """PrintWindow核心捕获逻辑（统一资源管理）
        
        Args:
            hwnd: 窗口句柄（已验证有效）
            
        Returns:
            tuple: (PIL.Image, width, height) 或 None
        """
        # 获取窗口客户区矩形
        client_rect = win32gui.GetClientRect(hwnd)
        width = client_rect[2] - client_rect[0]
        height = client_rect[3] - client_rect[1]

        if width <= 0 or height <= 0:
            if WindowCapture._log_enabled:
                print(f"[WindowCapture] 窗口尺寸无效: {width}x{height}")
            return None

        if WindowCapture._log_enabled:
            print(f"[WindowCapture] 窗口尺寸: {width}x{height}")

        wDC = None
        dcObj = None
        cDC = None
        dataBitMap = None

        try:
            wDC = win32gui.GetWindowDC(hwnd)
            
            # 尝试不同的flags参数
            flags_to_try = [3, 2, 1, 0]
            result = 0
            
            for flag in flags_to_try:
                # 每次循环都安全地清理上一轮的资源
                if dcObj is not None:
                    try:
                        dcObj.DeleteDC()
                    except Exception:
                        pass
                    dcObj = None
                if cDC is not None:
                    try:
                        cDC.DeleteDC()
                    except Exception:
                        pass
                    cDC = None
                if dataBitMap is not None:
                    try:
                        win32gui.DeleteObject(dataBitMap.GetHandle())
                    except Exception:
                        pass
                    dataBitMap = None
                
                # 创建新的DC和位图
                dcObj = win32ui.CreateDCFromHandle(wDC)
                cDC = dcObj.CreateCompatibleDC()
                dataBitMap = win32ui.CreateBitmap()
                dataBitMap.CreateCompatibleBitmap(dcObj, width, height)
                cDC.SelectObject(dataBitMap)
                
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] 尝试PrintWindow，flags={flag}")
                
                result = ctypes.windll.user32.PrintWindow(hwnd, cDC.GetSafeHdc(), flag)
                
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] PrintWindow返回值(flags={flag}): {result}")
                
                if result:
                    if WindowCapture._log_enabled:
                        print(f"[WindowCapture] PrintWindow成功，使用flags={flag}")
                    break
            
            if not result:
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] PrintWindow: 所有flags尝试均失败")
                return None
            
            # 获取位图数据
            bmpstr = dataBitMap.GetBitmapBits(True)
            if not bmpstr or len(bmpstr) == 0:
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] PrintWindow: 位图数据为空")
                return None
            
            # 黑屏检测 - 采样更多像素
            import struct
            sample_size = min(1600, len(bmpstr))  # 采样400个像素（每像素4字节）
            sample_data = bmpstr[:sample_size]
            pixels = struct.unpack('B' * sample_size, sample_data)
            black_pixel_ratio = sum(1 for p in pixels if p == 0) / len(pixels)
            if black_pixel_ratio > 0.99:
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] PrintWindow: 检测到黑屏(黑色像素比例: {black_pixel_ratio:.2%})")
                return None
            
            # 转换为PIL图像
            img = Image.frombuffer(
                'RGB',
                (width, height),
                bmpstr, 'raw', 'BGRX', 0, 1
            )
            
            if WindowCapture._log_enabled:
                print(f"[WindowCapture] PrintWindow成功: {width}x{height}")
            
            return img
            
        finally:
            # 统一资源清理 - 确保所有资源都被释放
            if dataBitMap is not None:
                try:
                    win32gui.DeleteObject(dataBitMap.GetHandle())
                except Exception:
                    pass
            if cDC is not None:
                try:
                    cDC.DeleteDC()
                except Exception:
                    pass
            if dcObj is not None:
                try:
                    dcObj.DeleteDC()
                except Exception:
                    pass
            if wDC is not None:
                try:
                    win32gui.ReleaseDC(hwnd, wDC)
                except Exception:
                    pass

    @staticmethod
    def capture_window(window_title="scrcpy", client_only=True):
        """截取Scrcpy窗口"""
        try:
            hwnd = WindowCapture.find_scrcpy_window()
            if not hwnd:
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] 未找到Scrcpy窗口")
                return None
            
            if not win32gui.IsWindowVisible(hwnd):
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] 窗口不可见")
                return None
            
            if WindowCapture._log_enabled:
                window_text = win32gui.GetWindowText(hwnd)
                class_name = win32gui.GetClassName(hwnd)
                print(f"[WindowCapture] 捕获窗口: '{window_text}' (类名: {class_name}, 句柄: {hwnd})")
            
            return WindowCapture._printwindow_capture(hwnd)
            
        except Exception as e:
            if WindowCapture._log_enabled:
                print(f"[WindowCapture] 捕获异常: {e}")
                import traceback
                traceback.print_exc()
            return None

    @staticmethod
    def capture_window_safe(window_title="scrcpy", client_only=True):
        """安全的截图方法"""
        return WindowCapture.capture_window(window_title, client_only)

    @staticmethod
    def enable_log(enabled=True):
        """启用/禁用日志"""
        WindowCapture._log_enabled = enabled
    
    @staticmethod
    def get_all_visible_windows():
        """获取所有可见窗口列表
        
        Returns:
            list: [(hwnd, title, class_name), ...]
        """
        windows = []
        
        def enum_callback(hwnd, _):
            if win32gui.IsWindowVisible(hwnd):
                title = win32gui.GetWindowText(hwnd)
                class_name = win32gui.GetClassName(hwnd)
                # 过滤掉无标题和特殊窗口
                if title and title.strip() and class_name not in ['Progman', 'Shell_TrayWnd', 'WorkerW']:
                    windows.append((hwnd, title, class_name))
            return True
        
        win32gui.EnumWindows(enum_callback, None)
        # 按标题排序
        windows.sort(key=lambda x: x[1].lower())
        return windows
    
    @staticmethod
    def find_window_by_hwnd(hwnd):
        """验证窗口句柄是否有效"""
        try:
            if not hwnd:
                return False
            return win32gui.IsWindow(hwnd) and win32gui.IsWindowVisible(hwnd)
        except Exception:
            return False
    
    @staticmethod
    def capture_window_by_hwnd(hwnd, crop_rect=None):
        """通过句柄捕获窗口（复用核心捕获逻辑）
        
        Args:
            hwnd: 窗口句柄
            crop_rect: 裁剪区域 (x, y, width, height)，相对于窗口客户区
            
        Returns:
            PIL.Image or None
        """
        try:
            if not WindowCapture.find_window_by_hwnd(hwnd):
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] 窗口句柄无效: {hwnd}")
                return None
            
            if WindowCapture._log_enabled:
                window_text = win32gui.GetWindowText(hwnd)
                class_name = win32gui.GetClassName(hwnd)
                print(f"[WindowCapture] 捕获窗口: '{window_text}' (类名: {class_name})")
            
            # 使用统一的核心捕获方法
            img = WindowCapture._printwindow_capture(hwnd)
            
            if img is None:
                return None
            
            # 应用裁剪
            if crop_rect:
                cx, cy, cw, ch = crop_rect
                width, height = img.size
                # 确保裁剪区域在图像范围内
                cx = max(0, min(cx, width - 1))
                cy = max(0, min(cy, height - 1))
                cw = min(cw, width - cx)
                ch = min(ch, height - cy)
                if cw > 0 and ch > 0:
                    img = img.crop((cx, cy, cx + cw, cy + ch))
                    if WindowCapture._log_enabled:
                        print(f"[WindowCapture] 应用裁剪: ({cx}, {cy}, {cw}, {ch})")
            
            if WindowCapture._log_enabled:
                print(f"[WindowCapture] 捕获成功: {img.size}")
            
            return img
            
        except Exception as e:
            if WindowCapture._log_enabled:
                print(f"[WindowCapture] 捕获异常: {e}")
            return None
    
    @staticmethod
    def get_window_client_rect(hwnd):
        """获取窗口客户区的屏幕坐标
        
        Args:
            hwnd: 窗口句柄
            
        Returns:
            tuple: (left, top, right, bottom) 或 None
        """
        try:
            if not WindowCapture.find_window_by_hwnd(hwnd):
                return None
            rect = win32gui.GetClientRect(hwnd)
            point = win32gui.ClientToScreen(hwnd, (0, 0))
            return (
                point[0],
                point[1],
                point[0] + rect[2],
                point[1] + rect[3]
            )
        except Exception:
            return None
