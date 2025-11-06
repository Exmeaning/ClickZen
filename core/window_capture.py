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
    _use_printwindow = True  # 默认使用PrintWindow API

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
    def capture_window(window_title="scrcpy", client_only=True):
        """截取指定窗口（自动选择最佳方法）"""
        try:
            # 按照用户设置使用相应方法，不预判SDL窗口
            if WindowCapture._use_printwindow:
                result = WindowCapture._capture_window_printwindow(window_title, client_only)
                # PrintWindow失败时自动回退
                if result is None:
                    if WindowCapture._log_enabled:
                        print(f"[WindowCapture] PrintWindow失败，回退到屏幕DC方法")
                    return WindowCapture.capture_window_original(window_title, client_only)
                return result
            else:
                return WindowCapture.capture_window_original(window_title, client_only)
                
        except Exception as e:
            if WindowCapture._log_enabled:
                print(f"[WindowCapture] 捕获异常: {e}")
            return None

    @staticmethod
    def _capture_window_printwindow(window_title="scrcpy", client_only=True):
        """使用PrintWindow API截取窗口（支持被遮挡）"""
        try:
            hwnd = WindowCapture.find_scrcpy_window()

            if not hwnd:
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] PrintWindow: 未找到窗口句柄")
                return None
            
            # 检查窗口是否可见
            if not win32gui.IsWindowVisible(hwnd):
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] PrintWindow: 窗口不可见")
                return None
            
            # 获取窗口标题和类名用于调试
            window_text = win32gui.GetWindowText(hwnd)
            class_name = win32gui.GetClassName(hwnd)
            if WindowCapture._log_enabled:
                print(f"[WindowCapture] PrintWindow: 尝试捕获窗口 '{window_text}' (类名: {class_name}, 句柄: {hwnd})")

            # 获取窗口客户区矩形（不包含标题栏）
            client_rect = win32gui.GetClientRect(hwnd)
            width = client_rect[2] - client_rect[0]
            height = client_rect[3] - client_rect[1]

            if width <= 0 or height <= 0:
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] PrintWindow: 窗口尺寸无效: {width}x{height}")
                return None
            
            if WindowCapture._log_enabled:
                print(f"[WindowCapture] PrintWindow: 窗口尺寸: {width}x{height}")

            # 创建设备上下文
            wDC = win32gui.GetWindowDC(hwnd)
            dcObj = win32ui.CreateDCFromHandle(wDC)
            cDC = dcObj.CreateCompatibleDC()
            
            # 创建位图对象
            dataBitMap = win32ui.CreateBitmap()
            dataBitMap.CreateCompatibleBitmap(dcObj, width, height)
            cDC.SelectObject(dataBitMap)
            
            # 使用PrintWindow API捕获窗口内容
            # 根据demo测试，flags=3 对Scrcpy效果最好
            # flags含义：1=PW_CLIENTONLY, 2=PW_RENDERFULLCONTENT, 3=两者结合
            
            # 尝试不同的flags参数
            flags_to_try = [3, 2, 1, 0]  # 按优先级排序
            result = 0
            
            for flag in flags_to_try:
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] 尝试PrintWindow，flags={flag}")
                    
                # 如果不是第一次尝试，需要重新创建DC和位图
                if flag != flags_to_try[0]:
                    dcObj.DeleteDC()
                    cDC.DeleteDC()
                    win32gui.DeleteObject(dataBitMap.GetHandle())
                    
                    dcObj = win32ui.CreateDCFromHandle(wDC)
                    cDC = dcObj.CreateCompatibleDC()
                    dataBitMap = win32ui.CreateBitmap()
                    dataBitMap.CreateCompatibleBitmap(dcObj, width, height)
                    cDC.SelectObject(dataBitMap)
                
                # 使用 ctypes 调用 PrintWindow API
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
                # 清理资源
                dcObj.DeleteDC()
                cDC.DeleteDC()
                win32gui.ReleaseDC(hwnd, wDC)
                win32gui.DeleteObject(dataBitMap.GetHandle())
                return None
            
            # 获取位图数据前，先检查是否为空
            try:
                bmpstr = dataBitMap.GetBitmapBits(True)
                if not bmpstr or len(bmpstr) == 0:
                    if WindowCapture._log_enabled:
                        print(f"[WindowCapture] PrintWindow: 位图数据为空")
                    raise Exception("位图数据为空")
                    
                # 检查位图数据是否全为0（黑屏）
                import struct
                # 检查前100个像素
                sample_size = min(400, len(bmpstr))  # 每个像素4字节
                sample_data = bmpstr[:sample_size]
                # 解析为整数列表
                pixels = struct.unpack('B' * sample_size, sample_data)
                # 允许一定比例的黑色像素，而非全黑才算失败
                black_pixel_ratio = sum(1 for p in pixels if p == 0) / len(pixels)
                if black_pixel_ratio > 0.99:  # 99%以上为黑色才认为是黑屏
                    if WindowCapture._log_enabled:
                        print(f"[WindowCapture] PrintWindow: 检测到黑屏(黑色像素比例: {black_pixel_ratio:.2%})")
                    raise Exception("捕获的图像为黑屏")
                    
            except Exception as e:
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] PrintWindow: 位图数据检查失败: {e}")
                # 清理资源
                dcObj.DeleteDC()
                cDC.DeleteDC()
                win32gui.ReleaseDC(hwnd, wDC)
                win32gui.DeleteObject(dataBitMap.GetHandle())
                return None
            
            # 转换为PIL图像
            img = Image.frombuffer(
                'RGB',
                (width, height),
                bmpstr, 'raw', 'BGRX', 0, 1
            )
            
            # 清理资源
            dcObj.DeleteDC()
            cDC.DeleteDC()
            win32gui.ReleaseDC(hwnd, wDC)
            win32gui.DeleteObject(dataBitMap.GetHandle())
            
            if WindowCapture._log_enabled:
                print(f"[WindowCapture] PrintWindow成功: {width}x{height}")
            
            return img

        except Exception as e:
            if WindowCapture._log_enabled:
                print(f"[WindowCapture] PrintWindow截图异常: {e}")
                import traceback
                traceback.print_exc()
            return None

    @staticmethod
    def capture_window_original(window_title="scrcpy", client_only=True):
        """原始截取方法（从屏幕DC截取）"""
        try:
            hwnd = WindowCapture.find_scrcpy_window()

            if not hwnd:
                return None

            # 只在调试模式下输出详细信息
            if WindowCapture._log_enabled:
                window_text = win32gui.GetWindowText(hwnd)
                class_name = win32gui.GetClassName(hwnd)
                print(f"[WindowCapture] 准备截取窗口: '{window_text}' (类名: {class_name})")

            # 获取客户区
            client_rect = win32gui.GetClientRect(hwnd)
            client_point = win32gui.ClientToScreen(hwnd, (0, 0))

            x = client_point[0]
            y = client_point[1]
            width = client_rect[2] - client_rect[0]
            height = client_rect[3] - client_rect[1]

            # 使用屏幕DC进行截图
            desktop_dc = win32gui.GetDC(0)
            desktop_mfc_dc = win32ui.CreateDCFromHandle(desktop_dc)
            mem_dc = desktop_mfc_dc.CreateCompatibleDC()

            bitmap = win32ui.CreateBitmap()
            bitmap.CreateCompatibleBitmap(desktop_mfc_dc, width, height)
            mem_dc.SelectObject(bitmap)

            mem_dc.BitBlt((0, 0), (width, height), desktop_mfc_dc, (x, y), win32con.SRCCOPY)

            bmpinfo = bitmap.GetInfo()
            bmpstr = bitmap.GetBitmapBits(True)

            img = Image.frombuffer(
                'RGB',
                (width, height),
                bmpstr, 'raw', 'BGRX', 0, 1
            )

            # 清理资源
            mem_dc.DeleteDC()
            desktop_mfc_dc.DeleteDC()
            win32gui.ReleaseDC(0, desktop_dc)
            win32gui.DeleteObject(bitmap.GetHandle())

            return img

        except Exception as e:
            if WindowCapture._log_enabled:
                print(f"[WindowCapture] 截图失败: {e}")
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
    def set_capture_method(use_printwindow=True):
        """设置捕获方法
        
        Args:
            use_printwindow: True使用PrintWindow API（支持被遮挡），False使用屏幕DC（传统方法）
        """
        WindowCapture._use_printwindow = use_printwindow
        if WindowCapture._log_enabled:
            method = "PrintWindow API" if use_printwindow else "屏幕DC"
            print(f"[WindowCapture] 切换到{method}捕获方式")
    
    @staticmethod
    def get_capture_method():
        """获取当前捕获方法"""
        return WindowCapture._use_printwindow