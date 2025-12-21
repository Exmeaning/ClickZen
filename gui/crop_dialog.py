"""裁剪区域对话框 - 用于绘制自定义裁剪区域"""
from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *
from core.window_capture import WindowCapture


class CropDialog(QDialog):
    """裁剪区域绘制对话框"""
    
    def __init__(self, hwnd, window_title="", parent=None):
        super().__init__(parent)
        self.hwnd = hwnd
        self.window_title = window_title
        self.crop_rect = None
        self.original_image = None
        self.init_ui()
        self.capture_window()
        
    def init_ui(self):
        """初始化UI"""
        self.setWindowTitle(f"裁剪区域设置 - {self.window_title}")
        self.setMinimumSize(800, 600)
        
        layout = QVBoxLayout(self)
        layout.setSpacing(10)
        
        # 说明
        info_layout = QHBoxLayout()
        info_label = QLabel("🖱️ 在图片上拖拽绘制裁剪区域")
        info_label.setStyleSheet("font-size: 14px; font-weight: bold;")
        info_layout.addWidget(info_label)
        
        # 重新截图按钮
        recapture_btn = QPushButton("🔄 重新截图")
        recapture_btn.clicked.connect(self.capture_window)
        info_layout.addWidget(recapture_btn)
        
        # 清除选择按钮
        clear_btn = QPushButton("❌ 清除选择")
        clear_btn.clicked.connect(self.clear_selection)
        info_layout.addWidget(clear_btn)
        
        info_layout.addStretch()
        layout.addLayout(info_layout)
        
        # 警告
        warning_label = QLabel("⚠️ 注意：设置裁剪区域后，请尽量不要改变窗口大小，否则需要重新设置裁剪区域！")
        warning_label.setStyleSheet("""
            QLabel {
                color: white;
                background-color: #FF9800;
                padding: 8px;
                border-radius: 4px;
                font-weight: bold;
            }
        """)
        warning_label.setWordWrap(True)
        layout.addWidget(warning_label)
        
        # 缩放控制
        zoom_layout = QHBoxLayout()
        self.zoom_out_btn = QPushButton("➖")
        self.zoom_out_btn.setFixedSize(30, 30)
        self.zoom_out_btn.clicked.connect(self.zoom_out)
        self.zoom_in_btn = QPushButton("➕")
        self.zoom_in_btn.setFixedSize(30, 30)
        self.zoom_in_btn.clicked.connect(self.zoom_in)
        self.zoom_fit_btn = QPushButton("适应窗口")
        self.zoom_fit_btn.clicked.connect(self.zoom_fit)
        self.zoom_100_btn = QPushButton("100%")
        self.zoom_100_btn.clicked.connect(lambda: self.set_zoom(1.0))
        
        self.zoom_label = QLabel("100%")
        self.zoom_label.setFixedWidth(50)
        self.zoom_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        
        zoom_layout.addWidget(QLabel("缩放:"))
        zoom_layout.addWidget(self.zoom_out_btn)
        zoom_layout.addWidget(self.zoom_label)
        zoom_layout.addWidget(self.zoom_in_btn)
        zoom_layout.addWidget(self.zoom_fit_btn)
        zoom_layout.addWidget(self.zoom_100_btn)
        zoom_layout.addStretch()
        layout.addLayout(zoom_layout)
        
        # 裁剪区域显示
        self.scroll_area = QScrollArea()
        self.scroll_area.setWidgetResizable(True)
        self.scroll_area.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.scroll_area.setStyleSheet("""
            QScrollArea {
                border: 2px solid #e0e0e0;
                border-radius: 6px;
                background-color: #333;
            }
        """)
        
        self.crop_widget = CropWidget()
        self.crop_widget = CropWidget()
        self.crop_widget.crop_changed.connect(self.on_crop_changed)
        self.scroll_area.setWidget(self.crop_widget)
        layout.addWidget(self.scroll_area, 1)
        
        # 裁剪信息
        self.crop_info_label = QLabel("请绘制裁剪区域...")
        self.crop_info_label.setStyleSheet("font-size: 13px; color: #666;")
        layout.addWidget(self.crop_info_label)
        
        # 按钮
        btn_layout = QHBoxLayout()
        
        self.confirm_btn = QPushButton("✓ 确认裁剪区域")
        self.confirm_btn.setMinimumHeight(45)
        self.confirm_btn.setEnabled(False)
        self.confirm_btn.setStyleSheet("""
            QPushButton {
                background-color: #4CAF50;
                color: white;
                font-size: 14px;
                font-weight: bold;
                border: none;
                border-radius: 6px;
            }
            QPushButton:hover {
                background-color: #45a049;
            }
            QPushButton:disabled {
                background-color: #ccc;
            }
        """)
        self.confirm_btn.clicked.connect(self.accept)
        
        # 使用全窗口按钮
        full_btn = QPushButton("📐 使用全窗口")
        full_btn.setMinimumHeight(45)
        full_btn.clicked.connect(self.use_full_window)
        
        cancel_btn = QPushButton("取消")
        cancel_btn.setMinimumHeight(45)
        cancel_btn.clicked.connect(self.reject)
        
        btn_layout.addWidget(self.confirm_btn)
        btn_layout.addWidget(full_btn)
        btn_layout.addWidget(cancel_btn)
        layout.addLayout(btn_layout)
        
    def capture_window(self):
        """捕获窗口截图"""
        try:
            img = WindowCapture.capture_window_by_hwnd(self.hwnd)
            if img:
                self.original_image = img
                self.crop_widget.set_image(img)
                self.crop_info_label.setText(f"窗口尺寸: {img.size[0]} x {img.size[1]}")
                # 自动适应窗口
                QTimer.singleShot(100, self.zoom_fit)
            else:
                self.crop_info_label.setText("无法捕获窗口截图")
        except Exception as e:
            self.crop_info_label.setText(f"截图失败: {str(e)[:50]}")
            
    def set_zoom(self, scale):
        """设置缩放比例"""
        self.crop_widget.set_scale(scale)
        self.zoom_label.setText(f"{int(scale * 100)}%")
        
    def zoom_in(self):
        """放大"""
        current = self.crop_widget.scale_factor
        self.set_zoom(min(current + 0.1, 3.0))
        
    def zoom_out(self):
        """缩小"""
        current = self.crop_widget.scale_factor
        self.set_zoom(max(current - 0.1, 0.1))
        
    def zoom_fit(self):
        """适应窗口"""
        if self.original_image:
            w, h = self.original_image.size
            view_w = self.scroll_area.viewport().width() - 20
            view_h = self.scroll_area.viewport().height() - 20
            
            if w > 0 and h > 0:
                scale_w = view_w / w
                scale_h = view_h / h
                scale = min(scale_w, scale_h)
                self.set_zoom(min(scale, 1.0))  # 不放大，只缩小
            
    def clear_selection(self):
        """清除选择"""
        self.crop_widget.clear_selection()
        self.crop_rect = None
        self.confirm_btn.setEnabled(False)
        self.crop_info_label.setText("请绘制裁剪区域...")
        
    def use_full_window(self):
        """使用全窗口"""
        if self.original_image:
            w, h = self.original_image.size
            self.crop_rect = (0, 0, w, h)
            self.crop_info_label.setText(f"裁剪区域: (0, 0) - ({w}, {h}) | 尺寸: {w} x {h}")
            self.confirm_btn.setEnabled(True)
            self.accept()
            
    def on_crop_changed(self, rect):
        """裁剪区域改变"""
        if rect and rect[2] > 0 and rect[3] > 0:
            x, y, w, h = rect
            self.crop_rect = rect
            self.crop_info_label.setText(f"裁剪区域: ({x}, {y}) - ({x+w}, {y+h}) | 尺寸: {w} x {h}")
            self.confirm_btn.setEnabled(True)
        else:
            self.confirm_btn.setEnabled(False)
            
    def get_crop_rect(self):
        """获取裁剪区域
        
        Returns:
            tuple: (x, y, width, height) 或 None
        """
        return self.crop_rect


class CropWidget(QWidget):
    """用于绘制裁剪区域的部件"""
    
    crop_changed = pyqtSignal(tuple)
    
    # 调整手柄大小
    HANDLE_SIZE = 8
    
    # 鼠标状态
    STATE_NONE = 0
    STATE_NEW = 1
    STATE_MOVE = 2
    STATE_RESIZE = 3
    
    # 手柄位置常量
    HANDLE_TOP_LEFT = 1
    HANDLE_TOP_RIGHT = 2
    HANDLE_BOTTOM_LEFT = 3
    HANDLE_BOTTOM_RIGHT = 4
    HANDLE_TOP = 5
    HANDLE_BOTTOM = 6
    HANDLE_LEFT = 7
    HANDLE_RIGHT = 8
    
    def __init__(self):
        super().__init__()
        self.image = None
        self.pixmap = None
        self.scale_factor = 1.0
        
        # 选择状态
        self.state = self.STATE_NONE
        self.active_handle = None
        self.start_pos = None  # 鼠标按下时的位置 (图片坐标)
        self.last_pos = None   # 上一次鼠标位置 (图片坐标)
        
        # 当前选择矩形 (图片坐标: x, y, w, h)
        self.selection_rect = None
        
        self.setMouseTracking(True)
        
    def set_image(self, pil_image):
        """设置图片"""
        self.image = pil_image
        
        try:
            # 转换为QPixmap - 先转换为RGB模式确保兼容性
            if pil_image.mode != 'RGB':
                pil_image = pil_image.convert('RGB')
            
            data = pil_image.tobytes("raw", "RGB")
            qimg = QImage(data, pil_image.width, pil_image.height, 
                         pil_image.width * 3, QImage.Format.Format_RGB888)
            
            # 必须复制
            self._qimage = qimg.copy()
            self.pixmap = QPixmap.fromImage(self._qimage)
            
            # 设置控件大小
            self.set_scale(self.scale_factor)
            
            self.clear_selection()
            self.update()
        except Exception as e:
            print(f"[CropWidget] 设置图片失败: {e}")
            
    def set_scale(self, scale):
        """设置缩放比例"""
        self.scale_factor = scale
        if self.pixmap:
            new_w = int(self.pixmap.width() * scale)
            new_h = int(self.pixmap.height() * scale)
            self.setMinimumSize(new_w, new_h)
            self.setMaximumSize(new_w, new_h)
            self.update()
            
    def map_to_image(self, pos):
        """映射控件坐标到图片坐标"""
        if self.scale_factor <= 0: return QPoint(0, 0)
        return QPoint(int(pos.x() / self.scale_factor), int(pos.y() / self.scale_factor))
    
    def map_from_image(self, pos):
        """映射图片坐标到控件坐标"""
        return QPoint(int(pos.x() * self.scale_factor), int(pos.y() * self.scale_factor))
    
    def get_selection_rect_qt(self):
        """获取Qt格式的矩形 (图片坐标)"""
        if not self.selection_rect:
            return QRect()
        return QRect(*self.selection_rect)
        
    def clear_selection(self):
        """清除选择"""
        self.state = self.STATE_NONE
        self.selection_rect = None
        self.update()
        self.setCursor(Qt.CursorShape.ArrowCursor)

    def get_handle_rect(self, handle_pos, rect):
        """获取手柄矩形 (图片坐标)"""
        # 为了更容易点击，手柄在逻辑上稍微大一点，但在绘制时保持视觉大小
        # 这里返回的是中心点坐标
        x, y, w, h = rect.x(), rect.y(), rect.width(), rect.height()
        
        if handle_pos == self.HANDLE_TOP_LEFT:
            return QPoint(x, y)
        elif handle_pos == self.HANDLE_TOP_RIGHT:
            return QPoint(x + w, y)
        elif handle_pos == self.HANDLE_BOTTOM_LEFT:
            return QPoint(x, y + h)
        elif handle_pos == self.HANDLE_BOTTOM_RIGHT:
            return QPoint(x + w, y + h)
        elif handle_pos == self.HANDLE_TOP:
            return QPoint(x + w // 2, y)
        elif handle_pos == self.HANDLE_BOTTOM:
            return QPoint(x + w // 2, y + h)
        elif handle_pos == self.HANDLE_LEFT:
            return QPoint(x, y + h // 2)
        elif handle_pos == self.HANDLE_RIGHT:
            return QPoint(x + w, y + h // 2)
        return None

    def hit_test(self, pos):
        """测试鼠标位置 hit test
        Returns: (state, handle_type)
        """
        if not self.selection_rect:
            return self.STATE_NEW, None
            
        # 将鼠标位置(控件坐标)转换为图片坐标
        img_pos = self.map_to_image(pos)
        rect = self.get_selection_rect_qt()
        
        # 检查手柄 (增加点击范围)
        hit_radius = 8 / self.scale_factor # 屏幕像素约为8
        
        handles = [
            (self.HANDLE_TOP_LEFT, Qt.CursorShape.SizeFDiagCursor),
            (self.HANDLE_TOP_RIGHT, Qt.CursorShape.SizeBDiagCursor),
            (self.HANDLE_BOTTOM_LEFT, Qt.CursorShape.SizeBDiagCursor),
            (self.HANDLE_BOTTOM_RIGHT, Qt.CursorShape.SizeFDiagCursor),
            (self.HANDLE_TOP, Qt.CursorShape.SizeVerCursor),
            (self.HANDLE_BOTTOM, Qt.CursorShape.SizeVerCursor),
            (self.HANDLE_LEFT, Qt.CursorShape.SizeHorCursor),
            (self.HANDLE_RIGHT, Qt.CursorShape.SizeHorCursor),
        ]
        
        for handle, cursor in handles:
            pt = self.get_handle_rect(handle, rect)
            if pt:
                # 距离检查
                if (QPoint(pt) - img_pos).manhattanLength() < hit_radius:
                    return self.STATE_RESIZE, handle
        
        # 检查是否在矩形内部
        if rect.contains(img_pos):
            return self.STATE_MOVE, None
            
        return self.STATE_NEW, None

    def update_cursor(self, pos):
        """更新鼠标光标"""
        state, handle = self.hit_test(pos)
        
        if state == self.STATE_RESIZE:
            cursors = {
                self.HANDLE_TOP_LEFT: Qt.CursorShape.SizeFDiagCursor,
                self.HANDLE_TOP_RIGHT: Qt.CursorShape.SizeBDiagCursor,
                self.HANDLE_BOTTOM_LEFT: Qt.CursorShape.SizeBDiagCursor,
                self.HANDLE_BOTTOM_RIGHT: Qt.CursorShape.SizeFDiagCursor,
                self.HANDLE_TOP: Qt.CursorShape.SizeVerCursor,
                self.HANDLE_BOTTOM: Qt.CursorShape.SizeVerCursor,
                self.HANDLE_LEFT: Qt.CursorShape.SizeHorCursor,
                self.HANDLE_RIGHT: Qt.CursorShape.SizeHorCursor,
            }
            self.setCursor(cursors.get(handle, Qt.CursorShape.ArrowCursor))
        elif state == self.STATE_MOVE:
            self.setCursor(Qt.CursorShape.SizeAllCursor)
        else:
            self.setCursor(Qt.CursorShape.CrossCursor)

    def mousePressEvent(self, event):
        """鼠标按下"""
        if event.button() == Qt.MouseButton.LeftButton and self.pixmap:
            pos = event.pos()
            img_pos = self.map_to_image(pos)
            
            self.state, self.active_handle = self.hit_test(pos)
            self.start_pos = img_pos
            self.last_pos = img_pos
            
            if self.state == self.STATE_NEW:
                # 开始新选择
                self.selection_rect = None
                self.crop_changed.emit(()) # 发送空元组表示清除
            
            self.update()

    def mouseMoveEvent(self, event):
        """鼠标移动"""
        pos = event.pos()
        img_pos = self.map_to_image(pos)
        
        # 如果没有按下鼠标，仅更新光标
        if not (event.buttons() & Qt.MouseButton.LeftButton):
            self.update_cursor(pos)
            return
            
        if not self.pixmap: return
        
        img_w = self.pixmap.width()
        img_h = self.pixmap.height()
        
        if self.state == self.STATE_NEW:
            # 创建新选区
            x1 = min(self.start_pos.x(), img_pos.x())
            y1 = min(self.start_pos.y(), img_pos.y())
            x2 = max(self.start_pos.x(), img_pos.x())
            y2 = max(self.start_pos.y(), img_pos.y())
            
            # 限制在图片范围内
            x1 = max(0, min(x1, img_w))
            y1 = max(0, min(y1, img_h))
            x2 = max(0, min(x2, img_w))
            y2 = max(0, min(y2, img_h))
            
            self.selection_rect = (x1, y1, x2 - x1, y2 - y1)
            
        elif self.state == self.STATE_MOVE:
            # 移动选区
            if self.selection_rect:
                dx = img_pos.x() - self.last_pos.x()
                dy = img_pos.y() - self.last_pos.y()
                
                x, y, w, h = self.selection_rect
                
                # 计算新位置并限制在图片范围内
                new_x = max(0, min(x + dx, img_w - w))
                new_y = max(0, min(y + dy, img_h - h))
                
                self.selection_rect = (new_x, new_y, w, h)
                self.last_pos = img_pos # 更新上一次位置
                
        elif self.state == self.STATE_RESIZE:
            # 调整大小
            if self.selection_rect:
                x, y, w, h = self.selection_rect
                
                # 获取当前矩形的四个边界
                left, top, right, bottom = x, y, x + w, y + h
                
                # 根据手柄调整边界
                # 限制坐标在图片范围内
                curr_x = max(0, min(img_pos.x(), img_w))
                curr_y = max(0, min(img_pos.y(), img_h))
                
                if self.active_handle in [self.HANDLE_LEFT, self.HANDLE_TOP_LEFT, self.HANDLE_BOTTOM_LEFT]:
                    left = min(curr_x, right - 10) # 保持最小宽度
                if self.active_handle in [self.HANDLE_RIGHT, self.HANDLE_TOP_RIGHT, self.HANDLE_BOTTOM_RIGHT]:
                    right = max(curr_x, left + 10)
                if self.active_handle in [self.HANDLE_TOP, self.HANDLE_TOP_LEFT, self.HANDLE_TOP_RIGHT]:
                    top = min(curr_y, bottom - 10) # 保持最小高度
                if self.active_handle in [self.HANDLE_BOTTOM, self.HANDLE_BOTTOM_LEFT, self.HANDLE_BOTTOM_RIGHT]:
                    bottom = max(curr_y, top + 10)
                
                self.selection_rect = (left, top, right - left, bottom - top)

        self.update()

    def mouseReleaseEvent(self, event):
        """鼠标释放"""
        if event.button() == Qt.MouseButton.LeftButton:
            if self.selection_rect:
                # 规范化矩形 (防止宽高为负)
                x, y, w, h = self.selection_rect
                # 如果宽高为负数，不会发生因为我们在moveEvent中保证了right > left
                
                # 发送信号
                if w > 10 and h > 10:
                    self.crop_changed.emit(self.selection_rect)
                else:
                    self.selection_rect = None
                    self.crop_changed.emit(())
            
            self.state = self.STATE_NONE
            self.active_handle = None
            self.update_cursor(event.pos())
            self.update()
            
    def paintEvent(self, event):
        """绘制"""
        painter = QPainter(self)
        
        # 绘制图片
        if self.pixmap:
            painter.scale(self.scale_factor, self.scale_factor)
            painter.drawPixmap(0, 0, self.pixmap)
            
            if self.selection_rect:
                x, y, w, h = self.selection_rect
                rect = QRect(x, y, w, h)
                
                # 绘制半透明覆盖层（非选区部分）
                overlay = QColor(0, 0, 0, 120)
                img_w = self.pixmap.width()
                img_h = self.pixmap.height()
                
                # 上
                painter.fillRect(0, 0, img_w, rect.top(), overlay)
                # 下
                painter.fillRect(0, rect.bottom(), img_w, img_h - rect.bottom(), overlay)
                # 左
                painter.fillRect(0, rect.top(), rect.left(), rect.height(), overlay)
                # 右
                painter.fillRect(rect.right(), rect.top(), img_w - rect.right(), rect.height(), overlay)
                
                # 绘制选区边框
                pen = QPen(QColor("#4CAF50"), 2 / self.scale_factor)
                painter.setPen(pen)
                painter.drawRect(rect)
                
                # 绘制手柄
                # 手柄大小需要反向缩放以保持视觉大小一致
                handle_size = self.HANDLE_SIZE / self.scale_factor
                painter.setBrush(QBrush(QColor("white")))
                painter.setPen(QPen(QColor("#4CAF50"), 1 / self.scale_factor))
                
                # 获取各个点
                points = [
                    self.get_handle_rect(self.HANDLE_TOP_LEFT, rect),
                    self.get_handle_rect(self.HANDLE_TOP_RIGHT, rect),
                    self.get_handle_rect(self.HANDLE_BOTTOM_LEFT, rect),
                    self.get_handle_rect(self.HANDLE_BOTTOM_RIGHT, rect),
                    self.get_handle_rect(self.HANDLE_TOP, rect),
                    self.get_handle_rect(self.HANDLE_BOTTOM, rect),
                    self.get_handle_rect(self.HANDLE_LEFT, rect),
                    self.get_handle_rect(self.HANDLE_RIGHT, rect),
                ]
                
                for pt in points:
                    # pt是中心点，绘制以其为中心的矩形
                    h_rect = QRectF(pt.x() - handle_size/2, pt.y() - handle_size/2, handle_size, handle_size)
                    painter.drawRect(h_rect)
                
                # 绘制尺寸标签
                painter.resetTransform()
                
                size_text = f"{w} x {h}"
                painter.setPen(QColor("white"))
                painter.setFont(QFont("Arial", 10, QFont.Weight.Bold))
                
                # 计算屏幕坐标上的文字位置
                screen_rect_x = (x + w/2) * self.scale_factor
                screen_rect_y = (y + h/2) * self.scale_factor
                
                text_rect = painter.fontMetrics().boundingRect(size_text)
                text_bg = QRect(int(screen_rect_x - text_rect.width() // 2 - 5),
                               int(screen_rect_y - text_rect.height() // 2 - 3),
                               text_rect.width() + 10, text_rect.height() + 6)
                painter.fillRect(text_bg, QColor(0, 0, 0, 180))
                painter.drawText(text_bg, Qt.AlignmentFlag.AlignCenter, size_text)
        else:
            # 没有图片时显示提示
            painter.setPen(QColor("#999"))
            painter.drawText(self.rect(), Qt.AlignmentFlag.AlignCenter, "等待截图...")
