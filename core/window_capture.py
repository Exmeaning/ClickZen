"""Windows窗口截图工具"""

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
        """截取指定窗口（减少日志）"""
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