"""窗口选择对话框 - 用于选择任意窗口进行捕获"""
from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *
from core.window_capture import WindowCapture
from utils.theme import theme


class WindowSelectorDialog(QDialog):
    """窗口选择对话框"""
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.selected_hwnd = None
        self.selected_title = None
        self.init_ui()
        self.load_windows()
        
    def init_ui(self):
        """初始化UI"""
        self.setWindowTitle("选择窗口")
        self.setMinimumSize(600, 500)
        
        layout = QVBoxLayout(self)
        layout.setSpacing(15)
        
        # 说明
        info_label = QLabel("请选择要捕获的窗口：")
        info_label.setStyleSheet("font-size: 14px; font-weight: bold;")
        layout.addWidget(info_label)
        
        # 刷新按钮
        refresh_btn = QPushButton("🔄 刷新窗口列表")
        refresh_btn.clicked.connect(self.load_windows)
        layout.addWidget(refresh_btn)
        
        # 窗口列表
        self.window_list = QListWidget()
        self.window_list.setMinimumHeight(300)
        self.window_list.itemDoubleClicked.connect(self.on_item_double_clicked)
        self.window_list.currentItemChanged.connect(self.on_selection_changed)
        t = theme.colors
        self.window_list.setStyleSheet(f"""
            QListWidget {{
                font-size: 13px;
                border: 2px solid {t['border']};
                border-radius: 6px;
                background-color: {t['input_bg']};
                color: {t['text']};
            }}
            QListWidget::item {{
                padding: 8px;
                border-bottom: 1px solid {t['border_subtle']};
            }}
            QListWidget::item:selected {{
                background-color: {t['selection_bg']};
                color: {t['selection_text']};
            }}
            QListWidget::item:hover {{
                background-color: {t['hover_bg']};
            }}
        """)
        layout.addWidget(self.window_list)
        
        # 预览区域
        preview_group = QGroupBox("窗口预览")
        preview_layout = QVBoxLayout()
        
        self.preview_label = QLabel("选择窗口查看预览")
        self.preview_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.preview_label.setMinimumHeight(150)
        self.preview_label.setStyleSheet(f"""
            QLabel {{
                background-color: {t['surface_muted']};
                color: {t['text_secondary']};
                border: 1px dashed {t['border']};
                border-radius: 4px;
            }}
        """)
        preview_layout.addWidget(self.preview_label)
        preview_group.setLayout(preview_layout)
        layout.addWidget(preview_group)
        
        # 警告提示
        warning_label = QLabel("⚠️ 提示：选择窗口后将进入裁剪设置，请不要改变窗口大小")
        warning_label.setStyleSheet(f"color: {theme.color('warning_text')}; font-size: 12px;")
        warning_label.setWordWrap(True)
        layout.addWidget(warning_label)
        
        # 按钮
        btn_layout = QHBoxLayout()
        
        self.select_btn = QPushButton("选择并裁剪")
        self.select_btn.setMinimumHeight(40)
        self.select_btn.setEnabled(False)
        self.select_btn.setStyleSheet(f"""
            QPushButton {{
                background-color: {t['success']};
                color: {t['accent_text']};
                font-size: 14px;
                font-weight: bold;
                border: none;
                border-radius: 6px;
            }}
            QPushButton:hover {{
                background-color: {t['success_hover']};
            }}
            QPushButton:disabled {{
                background-color: {t['disabled_bg']};
                color: {t['text_disabled']};
            }}
        """)
        self.select_btn.clicked.connect(self.accept)
        
        cancel_btn = QPushButton("取消")
        cancel_btn.setMinimumHeight(40)
        cancel_btn.clicked.connect(self.reject)
        
        btn_layout.addWidget(self.select_btn)
        btn_layout.addWidget(cancel_btn)
        layout.addLayout(btn_layout)
        
    def load_windows(self):
        """加载窗口列表"""
        self.window_list.clear()
        windows = WindowCapture.get_all_visible_windows()
        
        for hwnd, title, class_name in windows:
            # 跳过自己的窗口
            if "选择窗口" in title or "ClickZen" in title:
                continue
                
            item = QListWidgetItem()
            item.setText(f"{title}")
            item.setToolTip(f"类名: {class_name}\n句柄: {hwnd}")
            item.setData(Qt.ItemDataRole.UserRole, (hwnd, title, class_name))
            self.window_list.addItem(item)
            
        if self.window_list.count() == 0:
            item = QListWidgetItem("未找到可用窗口")
            item.setFlags(item.flags() & ~Qt.ItemFlag.ItemIsSelectable)
            self.window_list.addItem(item)
            
    def on_selection_changed(self, current, previous):
        """选择改变时更新预览"""
        if current:
            data = current.data(Qt.ItemDataRole.UserRole)
            if data:
                hwnd, title, class_name = data
                self.selected_hwnd = hwnd
                self.selected_title = title
                self.select_btn.setEnabled(True)
                self.update_preview(hwnd)
            else:
                self.select_btn.setEnabled(False)
                
    def on_item_double_clicked(self, item):
        """双击选择"""
        data = item.data(Qt.ItemDataRole.UserRole)
        if data:
            self.accept()
            
    def update_preview(self, hwnd):
        """更新预览图"""
        try:
            img = WindowCapture.capture_window_by_hwnd(hwnd)
            if img:
                # 缩放到预览大小
                img.thumbnail((400, 200))
                
                # 转换为QPixmap - 使用更安全的方法
                if img.mode != 'RGB':
                    img = img.convert('RGB')
                
                data = img.tobytes("raw", "RGB")
                qimg = QImage(data, img.width, img.height,
                             img.width * 3, QImage.Format.Format_RGB888)
                # 必须复制并保持引用
                self._preview_qimage = qimg.copy()
                pixmap = QPixmap.fromImage(self._preview_qimage)
                self.preview_label.setPixmap(pixmap)
            else:
                self.preview_label.setText("无法预览此窗口")
        except Exception as e:
            self.preview_label.setText(f"预览失败: {str(e)[:50]}")
            
    def get_selected_window(self):
        """获取选中的窗口信息
        
        Returns:
            tuple: (hwnd, title) 或 (None, None)
        """
        if self.selected_hwnd:
            return self.selected_hwnd, self.selected_title
        return None, None
