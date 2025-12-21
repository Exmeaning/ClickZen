from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *

class CoordinatePickerDialog(QDialog):
    """坐标拾取对话框 - 显示截图并允许点击拾取"""
    
    def __init__(self, screenshot, device_resolution, parent=None):
        """
        Args:
            screenshot: PIL Image 对象
            device_resolution: (width, height) 设备分辨率
            parent: 父窗口
        """
        super().__init__(parent)
        self.screenshot = screenshot
        self.device_resolution = device_resolution
        self.picked_coordinate = None
        
        self.initUI()
        
    def initUI(self):
        self.setWindowTitle("点击截图拾取坐标")
        self.setModal(True)
        
        # 转换 PIL Image 到 QPixmap
        self.original_pixmap = self.pil2pixmap(self.screenshot)
        
        layout = QVBoxLayout(self)
        
        # 顶部提示
        tip_label = QLabel("请在截图上点击目标位置\n移动鼠标可查看对应设备坐标")
        tip_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        layout.addWidget(tip_label)
        
        # 图片显示区域 (使用ScrollArea以防图片过大，或者缩放)
        # 这里选择缩放显示以适应屏幕
        self.image_label = QLabel()
        self.image_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.image_label.setMouseTracking(True)  # 启用鼠标追踪
        self.image_label.mousePressEvent = self.on_image_clicked
        self.image_label.mouseMoveEvent = self.on_mouse_move
        
        # 初始缩放
        # 获取屏幕大小
        screen = QApplication.primaryScreen().availableGeometry()
        max_w = int(screen.width() * 0.8)
        max_h = int(screen.height() * 0.8)
        
        self.scaled_pixmap = self.original_pixmap.scaled(
            max_w, max_h,
            Qt.AspectRatioMode.KeepAspectRatio,
            Qt.TransformationMode.SmoothTransformation
        )
        self.image_label.setPixmap(self.scaled_pixmap)
        
        layout.addWidget(self.image_label)
        
        # 底部状态栏
        self.status_label = QLabel("坐标: -")
        self.status_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        layout.addWidget(self.status_label)
        
        # 取消按钮
        cancel_btn = QPushButton("取消")
        cancel_btn.clicked.connect(self.reject)
        layout.addWidget(cancel_btn)
        
    def pil2pixmap(self, pil_image):
        """PIL Image 转 QPixmap"""
        if pil_image.mode == "RGB":
            r, g, b = pil_image.split()
            pil_image = Image.merge("RGB", (b, g, r))
        elif  pil_image.mode == "RGBA":
            r, g, b, a = pil_image.split()
            pil_image = Image.merge("RGBA", (b, g, r, a))
        elif pil_image.mode == "L":
            pil_image = pil_image.convert("RGBA")
            
        im2 = pil_image.convert("RGBA")
        data = im2.tobytes("raw", "BGRA")
        qim = QImage(data, im2.width, im2.height, QImage.Format.Format_ARGB32)
        return QPixmap.fromImage(qim)

    def get_device_coordinate(self, pos):
        """将控件坐标转换为设备坐标"""
        # 获取图片在label中的偏移（如果是居中对齐）
        # QLabel居中对齐时，Pixmap绘制在中心
        
        # 简单起见，假设Label大小适应Pixmap
        # 计算缩放比例
        label_w = self.image_label.width()
        label_h = self.image_label.height()
        pix_w = self.scaled_pixmap.width()
        pix_h = self.scaled_pixmap.height()
        
        # 计算图片在label中的实际位置（居中）
        offset_x = (label_w - pix_w) // 2
        offset_y = (label_h - pix_h) // 2
        
        rel_x = pos.x() - offset_x
        rel_y = pos.y() - offset_y
        
        if rel_x < 0 or rel_x >= pix_w or rel_y < 0 or rel_y >= pix_h:
            return None
            
        # 转换为原始图片坐标
        orig_x = int(rel_x * self.original_pixmap.width() / pix_w)
        orig_y = int(rel_y * self.original_pixmap.height() / pix_h)
        
        # 转换为设备坐标
        # 逻辑：将截图区域（Window/Crop）映射到 设备分辨率
        # 假设截图覆盖了整个对应的设备区域（Simulator模式下Crop Rect对应全屏）
        
        device_w, device_h = self.device_resolution
        img_w = self.original_pixmap.width()
        img_h = self.original_pixmap.height()
        
        scale_x = device_w / img_w
        scale_y = device_h / img_h
        
        device_x = int(orig_x * scale_x)
        device_y = int(orig_y * scale_y)
        
        # 边界检查
        device_x = max(0, min(device_x, device_w - 1))
        device_y = max(0, min(device_y, device_h - 1))
        
        return device_x, device_y

    def on_mouse_move(self, event):
        """鼠标移动事件"""
        coord = self.get_device_coordinate(event.position())
        if coord:
            self.status_label.setText(f"设备坐标: ({coord[0]}, {coord[1]})")
        else:
            self.status_label.setText("坐标: -")
            
    def on_image_clicked(self, event):
        """点击事件"""
        if event.button() == Qt.MouseButton.LeftButton:
            coord = self.get_device_coordinate(event.position())
            if coord:
                self.picked_coordinate = coord
                self.accept()
    
    def get_result(self):
        """获取结果"""
        return self.picked_coordinate

from PIL import Image
