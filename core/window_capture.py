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
    def capture_window(window_title="scrcpy", client_only=True):
        """截取指定窗口"""
        try:
            return WindowCapture._capture_window_printwindow(window_title, client_only)
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
        """验证窗口句柄是否有效
        
        Args:
            hwnd: 窗口句柄
            
        Returns:
            bool: 窗口是否有效
        """
        try:
            if not hwnd:
                return False
            return win32gui.IsWindow(hwnd) and win32gui.IsWindowVisible(hwnd)
        except Exception:
            return False
    
    @staticmethod
    def capture_window_by_hwnd(hwnd, crop_rect=None):
        """通过句柄捕获窗口
        
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
            
            window_text = win32gui.GetWindowText(hwnd)
            class_name = win32gui.GetClassName(hwnd)
            if WindowCapture._log_enabled:
                print(f"[WindowCapture] 捕获窗口: '{window_text}' (类名: {class_name})")
            
            # 获取窗口客户区矩形
            client_rect = win32gui.GetClientRect(hwnd)
            width = client_rect[2] - client_rect[0]
            height = client_rect[3] - client_rect[1]
            
            if width <= 0 or height <= 0:
                if WindowCapture._log_enabled:
                    print(f"[WindowCapture] 窗口尺寸无效: {width}x{height}")
                return None
            
            # 创建设备上下文
            wDC = win32gui.GetWindowDC(hwnd)
            dcObj = win32ui.CreateDCFromHandle(wDC)
            cDC = dcObj.CreateCompatibleDC()
            
            # 创建位图对象
            dataBitMap = win32ui.CreateBitmap()
            dataBitMap.CreateCompatibleBitmap(dcObj, width, height)
            cDC.SelectObject(dataBitMap)
            
            # 使用PrintWindow API捕获窗口内容
            flags_to_try = [3, 2, 1, 0]
            result = 0
            
            for flag in flags_to_try:
                if flag != flags_to_try[0]:
                    dcObj.DeleteDC()
                    cDC.DeleteDC()
                    win32gui.DeleteObject(dataBitMap.GetHandle())
                    
                    dcObj = win32ui.CreateDCFromHandle(wDC)
                    cDC = dcObj.CreateCompatibleDC()
                    dataBitMap = win32ui.CreateBitmap()
                    dataBitMap.CreateCompatibleBitmap(dcObj, width, height)
                    cDC.SelectObject(dataBitMap)
                
                result = ctypes.windll.user32.PrintWindow(hwnd, cDC.GetSafeHdc(), flag)
                if result:
                    break
            
            if not result:
                dcObj.DeleteDC()
                cDC.DeleteDC()
                win32gui.ReleaseDC(hwnd, wDC)
                win32gui.DeleteObject(dataBitMap.GetHandle())
                return None
            
            # 获取位图数据
            bmpstr = dataBitMap.GetBitmapBits(True)
            if not bmpstr or len(bmpstr) == 0:
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
            
            # 应用裁剪
            if crop_rect:
                cx, cy, cw, ch = crop_rect
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