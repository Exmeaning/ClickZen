from PyQt6.QtWidgets import *
from PyQt6.QtCore import *
from PyQt6.QtGui import *
import json
import os

class SimulatorConfigDialog(QDialog):
    """模拟器配置对话框: 设置目标分辨率和管理的配置文件"""
    
    def __init__(self, crop_rect, window_title="", current_resolution=None, parent=None):
        super().__init__(parent)
        self.crop_rect = crop_rect
        self.window_title = window_title
        # 默认分辨率为 ADB读取的 或者 1440x3200
        self.current_resolution = current_resolution or (1440, 3200)
        self.should_save = False
        self.result_resolution = self.current_resolution
        
        self.initUI()
        self.load_history()
        
    def initUI(self):
        self.setWindowTitle("确认模拟器参数")
        self.setMinimumWidth(400)
        
        layout = QVBoxLayout(self)
        
        # 1. 裁剪区域显示
        info_group = QGroupBox("当前采集信息")
        info_layout = QFormLayout()
        
        crop_w, crop_h = 0, 0
        if self.crop_rect:
            x, y, crop_w, crop_h = self.crop_rect
            info_layout.addRow("窗口标题:", QLabel(self.window_title[:30] + "..."))
            info_layout.addRow("裁剪尺寸:", QLabel(f"{crop_w} x {crop_h}"))
        
        info_group.setLayout(info_layout)
        layout.addWidget(info_group)
        
        # 2. 目标分辨率设置
        res_group = QGroupBox("目标设备分辨率")
        res_layout = QFormLayout()
        
        self.width_spin = QSpinBox()
        self.width_spin.setRange(100, 10000)
        self.width_spin.setValue(self.current_resolution[0])
        self.width_spin.setSuffix(" px")
        
        self.height_spin = QSpinBox()
        self.height_spin.setRange(100, 10000)
        self.height_spin.setValue(self.current_resolution[1])
        self.height_spin.setSuffix(" px")
        
        res_layout.addRow("设备宽度:", self.width_spin)
        res_layout.addRow("设备高度:", self.height_spin)
        
        # 预设按钮
        preset_layout = QHBoxLayout()
        preset_720 = QPushButton("720P (720x1280)")
        preset_720.clicked.connect(lambda: self.set_resolution(720, 1280))
        preset_1080 = QPushButton("1080P (1080x1920)")
        preset_1080.clicked.connect(lambda: self.set_resolution(1080, 1920))
        preset_crop = QPushButton("使用裁剪尺寸")
        preset_crop.clicked.connect(lambda: self.set_resolution(crop_w, crop_h))
        
        preset_layout.addWidget(preset_720)
        preset_layout.addWidget(preset_1080)
        preset_layout.addWidget(preset_crop)
        res_layout.addRow("常用预设:", preset_layout)
        
        # 提示
        tips = QLabel("说明: 此分辨率即为程序认为的'真实设备大小'。\n如果模拟器画面有黑边或者并未1:1显示，请在此处修正为实际游戏/应用的分辨率。")
        tips.setStyleSheet("color: gray; font-size: 11px;")
        tips.setWordWrap(True)
        res_layout.addRow(tips)
        
        res_group.setLayout(res_layout)
        layout.addWidget(res_group)
        
        # 3. 保存选项
        self.save_check = QCheckBox("保存此模拟器配置 (下次自动加载)")
        self.save_check.setChecked(True)
        layout.addWidget(self.save_check)
        
        # 按钮
        btn_box = QDialogButtonBox(QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel)
        btn_box.accepted.connect(self.on_accept)
        btn_box.rejected.connect(self.reject)
        layout.addWidget(btn_box)
        
    def set_resolution(self, w, h):
        self.width_spin.setValue(w)
        self.height_spin.setValue(h)
        
    def load_history(self):
        # 尝试从history中找
        # 逻辑由外部Main Window调用前其实已经处理，但这里可以再次确认
        pass
        
    def on_accept(self):
        self.result_resolution = (self.width_spin.value(), self.height_spin.value())
        self.should_save = self.save_check.isChecked()
        self.accept()
        
    def get_result(self):
        return self.result_resolution, self.should_save
