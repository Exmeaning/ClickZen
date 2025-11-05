"""截图助手 - 解决HDR等特殊显示模式的截图问题"""

import numpy as np
from PIL import Image
import mss
import win32gui
import win32ui
import win32con
import win32api


class ScreenshotHelper:
    """截图助手类 - 提供多种截图方法"""

    @staticmethod
    def capture_with_mss(region=None):
        """使用mss库截图（推荐，对HDR支持好）"""
        try:
            with mss.mss() as sct:
                if region:
                    # 指定区域截图
                    x, y, width, height = region
                    monitor = {"top": y, "left": x, "width": width, "height": height}
                else:
                    # 全屏截图
                    monitor = sct.monitors[1]  # 主显示器

                # 截图
                screenshot = sct.grab(monitor)

                # 转换为PIL Image
                img = Image.frombytes("RGB", screenshot.size, screenshot.bgra, "raw", "BGRX")
                return img

        except Exception as e:
            print(f"[Screenshot] mss截图失败: {e}")
            return None

    @staticmethod
    def capture_with_win32(region=None):
        """使用Win32 API截图（备用方案）"""
        try:
            # 获取屏幕DC
            hdesktop = win32gui.GetDesktopWindow()
            desktop_dc = win32gui.GetWindowDC(hdesktop)
            img_dc = win32ui.CreateDCFromHandle(desktop_dc)
            mem_dc = img_dc.CreateCompatibleDC()

            # 确定截图区域
            if region:
                x, y, width, height = region
            else:
                # 全屏
                x, y = 0, 0
                width = win32api.GetSystemMetrics(win32con.SM_CXSCREEN)
                height = win32api.GetSystemMetrics(win32con.SM_CYSCREEN)

            # 创建位图
            screenshot = win32ui.CreateBitmap()
            screenshot.CreateCompatibleBitmap(img_dc, width, height)
            mem_dc.SelectObject(screenshot)

            # 复制屏幕内容到位图
            mem_dc.BitBlt((0, 0), (width, height), img_dc, (x, y), win32con.SRCCOPY)

            # 获取位图数据
            bmpinfo = screenshot.GetInfo()
            bmpstr = screenshot.GetBitmapBits(True)

            # 转换为PIL Image
            img = Image.frombuffer(
                'RGB',
                (bmpinfo['bmWidth'], bmpinfo['bmHeight']),
                bmpstr, 'raw', 'BGRX', 0, 1
            )

            # 清理资源
            mem_dc.DeleteDC()
            win32gui.DeleteObject(screenshot.GetHandle())
            img_dc.DeleteDC()
            win32gui.ReleaseDC(hdesktop, desktop_dc)

            return img

        except Exception as e:
            print(f"[Screenshot] Win32截图失败: {e}")
            return None

    @staticmethod
    def capture_with_pil(region=None):
        """使用PIL截图（简单但可能有HDR问题）"""
        try:
            from PIL import ImageGrab
            if region:
                x, y, width, height = region
                img = ImageGrab.grab(bbox=(x, y, x + width, y + height))
            else:
                img = ImageGrab.grab()
            return img
        except Exception as e:
            print(f"[Screenshot] PIL截图失败: {e}")
            return None

    @staticmethod
    def capture_best(region=None):
        """自动选择最佳截图方法"""
        # 优先尝试mss（对HDR支持最好）
        img = ScreenshotHelper.capture_with_mss(region)
        if img:
            return img

        # 然后尝试Win32 API
        img = ScreenshotHelper.capture_with_win32(region)
        if img:
            return img

        # 最后尝试PIL
        return ScreenshotHelper.capture_with_pil(region)