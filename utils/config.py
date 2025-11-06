import os
import json
from pathlib import Path

VERSION = "1.0.0"


class Config:
    def __init__(self):
        self.app_dir = Path.home() / ".phone_controller"
        self.app_dir.mkdir(exist_ok=True)

        self.config_file = self.app_dir / "config.json"
        self.scrcpy_dir = self.app_dir / "scrcpy"
        self.scrcpy_dir.mkdir(exist_ok=True)

        self.default_config = {
            "adb_path": r"C:\adb\adb.exe",
            "scrcpy_path": str(self.scrcpy_dir / "scrcpy.exe"),
            "scrcpy_version": "3.3.3",  # 更新到最新版本支持Android 16
            "scrcpy_download_url": "https://github.com/Genymobile/scrcpy/releases/download/v{version}/scrcpy-win64-v{version}.zip",
            "window_size": [400, 800],
            "bitrate": "8M",  # 这个参数名不变，但使用时会转换
            "max_fps": 60,
            "use_root": True,
            "always_on_top": True,
            "window_x": 100,
            "window_y": 100,
            "auto_update_scrcpy": True  # 自动检查更新
        }

        self.load()

    def load(self):
        if self.config_file.exists():
            with open(self.config_file, 'r', encoding='utf-8') as f:
                loaded = json.load(f)
                self.config = {**self.default_config, **loaded}
        else:
            self.config = self.default_config
            self.save()

    def save(self):
        with open(self.config_file, 'w', encoding='utf-8') as f:
            json.dump(self.config, f, indent=2, ensure_ascii=False)

    def get(self, key, default=None):
        return self.config.get(key, default)

    def set(self, key, value):
        self.config[key] = value
        self.save()


config = Config()