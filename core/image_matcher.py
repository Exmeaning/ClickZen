import cv2
import numpy as np
from PIL import Image
import time
from PyQt6.QtCore import QObject, pyqtSignal


class ImageMatcher(QObject):
    """图像模板匹配器"""
    match_found = pyqtSignal(list)  # 发出匹配结果：[(x, y, confidence), ...]

    def __init__(self):
        super().__init__()
        self.method = cv2.TM_CCOEFF_NORMED  # 默认最佳方法

    def set_method(self, method_name):
        """设置匹配方法"""
        methods = {
            "CCOEFF_NORMED (推荐)": cv2.TM_CCOEFF_NORMED,
            "CCORR_NORMED": cv2.TM_CCORR_NORMED,
            "SQDIFF_NORMED": cv2.TM_SQDIFF_NORMED
        }
        self.method = methods.get(method_name, cv2.TM_CCOEFF_NORMED)

    def find_all(self, screenshot: Image.Image, template: Image.Image, threshold=0.85, region=None):
        """
        查找所有匹配位置
        :param screenshot: 全屏或区域截图 (PIL Image)
        :param template: 模板图片 (PIL Image)
        :param threshold: 容差阈值 (0.5-1.0)
        :param region: 搜索区域 (x, y, w, h)，None为全屏
        :return: 列表 [(center_x, center_y, confidence), ...] 按confidence降序
        """
        if template.size[0] > screenshot.size[0] or template.size[1] > screenshot.size[1]:
            return []

        # 转换为OpenCV格式
        ss = cv2.cvtColor(np.array(screenshot), cv2.COLOR_RGB2BGR)
        temp = cv2.cvtColor(np.array(template), cv2.COLOR_RGB2BGR)

        if region:
            x, y, w, h = region
            ss = ss[y:y+h, x:x+w]

        # 匹配
        result = cv2.matchTemplate(ss, temp, self.method)
        locations = np.where(result >= threshold)

        matches = []
        for pt in zip(*locations[::-1]):  # (y, x) -> (x, y)
            confidence = result[pt[1], pt[0]]
            # 计算中心点
            center_x = pt[0] + template.size[0] // 2
            center_y = pt[1] + template.size[1] // 2
            if region:
                center_x += x
                center_y += y
            matches.append((center_x, center_y, float(confidence)))

        # 去重（距离<10像素视为同一位置）
        matches = self._remove_duplicates(matches, threshold=10)
        matches.sort(key=lambda m: m[2], reverse=True)  # 按置信度降序

        self.match_found.emit(matches)
        return matches

    def _remove_duplicates(self, matches, threshold=10):
        """去除重复匹配"""
        if len(matches) < 2:
            return matches
        unique = []
        for m in matches:
            is_duplicate = False
            for u in unique:
                dist = ((m[0] - u[0]) ** 2 + (m[1] - u[1]) ** 2) ** 0.5
                if dist < threshold:
                    is_duplicate = True
                    break
            if not is_duplicate:
                unique.append(m)
        return unique

    def find_best(self, *args, **kwargs):
        """只返回最佳匹配"""
        all_matches = self.find_all(*args, **kwargs)
        return all_matches[0] if all_matches else None