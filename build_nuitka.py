#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
Nuitka打包脚本 - Phone Controller（多核优化版）
支持崩溃时生成错误报告，并充分利用多线程加快构建。
"""
import datetime
import os
import sys
import shutil
import subprocess
from pathlib import Path

# 项目信息
PROJECT_NAME = "ClickZen"
VERSION = "1.3.0"
MAIN_SCRIPT = "main.py"
ICON_FILE = "resources/icon.ico"  # 如果有图标文件


def clean_build():
    """清理之前的构建"""
    dirs_to_remove = [
        "build",
        "dist",
        f"{PROJECT_NAME}.build",
        f"{PROJECT_NAME}.dist",
    ]

    for dir_name in dirs_to_remove:
        path = Path(dir_name)
        if path.exists():
            shutil.rmtree(path)
            print(f"已清理: {dir_name}")


def build_with_nuitka():
    """使用 Nuitka 构建（启用多线程与速度优化）"""

    cpu_cores = os.cpu_count() or 4  # 保底
    print(f"检测到 CPU 核心数: {cpu_cores}，将启用并行编译。")

    nuitka_args = [
        sys.executable, "-m", "nuitka",

        # ===== 基本参数 =====
        "--standalone",
        "--onefile",
        f"--output-filename={PROJECT_NAME}.exe",

        # ===== Windows特定 =====
        "--windows-console-mode=force",
        # "--windows-uac-admin",  # 保持禁用避免UAC

        # ===== 性能与构建优化 =====
        "--assume-yes-for-downloads",
        f"--jobs={cpu_cores}",               # 最大化多线程编译
        "--prefer-source-code",
        "--no-deployment-flag=self-execution",

        # ===== 包含模块 =====
        "--include-qt-plugins=all",
        "--include-qt-plugins=platforms",
        "--include-qt-plugins=styles",
        "--include-qt-plugins=iconengines",
        "--include-package=PyQt6",
        "--include-package=PIL",
        "--include-package=cv2",
        "--include-package-data=cv2",
        "--include-package=numpy",
        "--include-package=win32gui",
        "--include-package=win32api",
        "--include-package=win32con",
        "--include-package=win32ui",
        "--include-package=win32timezone",
        "--include-package=ppadb",
        "--include-package=mss",

        # ===== 数据文件 =====
        "--include-data-dir=resources=resources",

        # ===== 插件 =====
        "--enable-plugin=pyqt6",
        "--enable-plugin=numpy",

        # ===== 错误日志路径 =====
        "--force-stderr-spec=%TEMP%\\phone_controller_error_%TIME%.log".replace(
            "%TIME%", datetime.datetime.now().strftime("%H%M%S")
        ),

        MAIN_SCRIPT
    ]

    # 可选图标
    if Path(ICON_FILE).exists():
        nuitka_args.insert(3, f"--windows-icon-from-ico={ICON_FILE}")

    print("\n开始构建Nuitka项目...")
    print("命令：", " ".join(nuitka_args), "\n")

    result = subprocess.run(nuitka_args)

    if result.returncode == 0:
        print("\n✅ 构建成功！")
        print(f"输出文件：{PROJECT_NAME}.exe")
    else:
        print("\n❌ 构建失败！")
        sys.exit(1)


def create_crash_handler():
    """创建崩溃处理模块"""

    crash_handler_code = '''
import sys
import traceback
import datetime
import os
from pathlib import Path

def setup_crash_handler():
    """设置全局崩溃处理器"""

    def handle_exception(exc_type, exc_value, exc_traceback):
        if issubclass(exc_type, KeyboardInterrupt):
            sys.__excepthook__(exc_type, exc_value, exc_traceback)
            return

        timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
        error_dir = Path.home() / ".phone_controller" / "crash_reports"
        error_dir.mkdir(parents=True, exist_ok=True)

        error_file = error_dir / f"crash_{timestamp}.txt"

        with open(error_file, 'w', encoding='utf-8') as f:
            f.write(f"=== Phone Controller Crash Report ===\\n")
            f.write(f"Time: {datetime.datetime.now()}\\n")
            f.write(f"Version: {VERSION}\\n")
            f.write(f"Python: {sys.version}\\n")
            f.write(f"OS: {os.name} {sys.platform}\\n\\n=== Error Details ===\\n")
            traceback.print_exception(exc_type, exc_value, exc_traceback, file=f)

        try:
            from PyQt6.QtWidgets import QApplication, QMessageBox
            if QApplication.instance():
                QMessageBox.critical(
                    None,
                    "程序崩溃",
                    f"程序已崩溃！\\n\\n"
                    f"类型: {exc_type.__name__}\\n"
                    f"信息: {exc_value}\\n\\n"
                    f"报告保存位置:\\n{error_file}"
                )
        except Exception:
            print(f"错误报告已保存到: {error_file}")

    sys.excepthook = handle_exception
'''

    with open("crash_handler.py", "w", encoding="utf-8") as f:
        f.write(crash_handler_code.replace("{VERSION}", VERSION))

    print("已创建崩溃处理模块。")


def create_requirements():
    """创建 requirements.txt"""
    requirements = """
PyQt6>=6.4.0
PyQt6-Qt6>=6.4.0
PyQt6-sip>=13.4.0
pillow>=9.0.0
opencv-python>=4.5.0
numpy>=1.20.0
pywin32>=300
mss>=6.1.0
pure-python-adb>=0.3.0.dev0
requests>=2.25.0
"""
    with open("requirements.txt", "w", encoding="utf-8") as f:
        f.write(requirements.strip())
    print("已创建 requirements.txt。")


def main():
    """主函数"""
    print(f"=== {PROJECT_NAME} Nuitka 构建脚本（多核优化） ===\n")

    # 检查 Nuitka 是否安装
    try:
        subprocess.run([sys.executable, "-m", "nuitka", "--version"],
                       capture_output=True, check=True)
    except:
        print("❌ 错误: Nuitka 未安装！请运行: pip install nuitka")
        sys.exit(1)

    # 清理旧构建
    clean_build()
    # 创建崩溃处理模块
    create_crash_handler()
    # 创建依赖说明
    create_requirements()
    # 开始构建
    build_with_nuitka()

    print("\n=== 构建完成 ===")
    print(f"可执行文件: {PROJECT_NAME}.exe")
    print("崩溃报告将保存至: %USERPROFILE%\\.phone_controller\\crash_reports\\")
    print("🔥 多核极速编译模式已启用 🔥")


if __name__ == "__main__":
    main()