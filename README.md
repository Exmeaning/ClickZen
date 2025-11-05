# ClickZen - 基于ADB的Android自动化控制工具

<p align="center">
  <img src="https://img.shields.io/badge/python-3.8+-blue.svg" alt="Python Version">
  <img src="https://img.shields.io/badge/license-MIT-green.svg" alt="License">
  <img src="https://img.shields.io/badge/platform-Windows-lightgrey.svg" alt="Platform">
</p>

---

## 📋 项目简介

ClickZen 是一个基于 Python 开发的 Android 设备自动化控制工具，通过 ADB 和 Scrcpy 实现设备投屏、操作录制回放、图像识别自动化等功能。特别适用于自动化测试、游戏挂机、批量操作等场景。部分自动化灵感来源于手机端的成熟项目"klick‘r"。

---

## ⚠️ 重要声明

1.  **AI代码风险提示**：本项目部分代码由AI辅助生成，可能存在潜在的bug或安全问题。使用前请仔细审查代码，风险自负。
2.  **作者能力有限**：本人编程水平有限，代码质量可能不高。作者 Python 水平约等于 Hello World → 欢迎 PR 教我写 class。
3.  **使用责任**：请合理使用本工具，遵守相关法律法规。因使用本工具产生的任何问题，作者不承担责任。

---

## ✨ 主要功能

- 🖥️ **设备投屏控制**：通过 Scrcpy 实现低延迟投屏
- 🎬 **操作录制回放**：精确记录和回放用户操作
- 🎯 **图像识别自动化**：基于模板匹配的自动化任务（类Klick’r）
- 🎲 **防检测机制**：随机化操作模拟人工行为
- 📊 **变量系统**：支持条件判断和动态变量
- 🔧 **易用的GUI**：直观的图形界面操作

---

## 🚀 快速上手 (面向普通用户)

如果您不了解代码，只想直接使用本软件，请按以下步骤操作：

1.  **下载程序**：
    *   前往 [**Releases 发布页面**](https://github.com/Exmeaning/ClickZen/releases)。
    *   找到最新的版本，下载名为 `ClickZen_vX.X.X.exe` 的可执行程序。

2.  **连接手机**：
    *   在手机上开启 **“开发者选项”**，然后启用 **“USB调试”**。
    *   **（小米/Redmi手机用户注意）**：需要额外在“开发者选项”中开启 **“USB调试（安全设置）”**。
    *   使用数据线将手机连接到电脑。

3.  **运行软件**：
    *   找到并双击 `ClickZen.exe` 即可启动程序。
    *   点击软件界面上的 **“刷新设备”**，如果您的手机出现在列表中，说明连接成功。
    *   点击 **“启动 scrcpy”** 即可在电脑上看到并操作手机屏幕。

---

## 📖 功能指南

### 🧩 基础使用

1.  确保手机已按上述步骤连接成功。
2.  点击 **“刷新设备”** 识别设备。
3.  点击 **“启动 scrcpy”** 开始投屏和操作。

### ⚙️ 进阶功能

#### 🎬 录制脚本
- 点击 **“录制”** 会从第一个操作开始录制。
- 点击 **“结束录制”** 完成录制。
- 可以执行录制好的文件——**记得先保存！**

#### 🤖 类 Klick'r 的自动化监控

1.  创建任务，设置好变量和条件（如果需要复杂的监控方案）。
2.  点击 **“监控区域”** → 在弹出的投屏窗口上框选您想监控的区域 → 点击 **“截取区域”**，系统会自动将这块区域的图像作为后续识别的目标。
3.  选择匹配阈值与冷却时间。  
    🔸 *冷却时间* 指的是当条件满足后，执行相应操作的间隔，而不是检测的间隔。
4.  添加您希望执行的动作。  
    可执行操作包括：
    -   变量更改
    -   点击、滑动、等待等物理操作
    -   调用您已录制好的脚本

![autoclickGUI](https://github.com/Exmeaning/Exmeaning-Image-hosting/blob/main/ClickZen/readme/autoclickGUI.png)

---

## 🎥 截图示例
![mainGUI](https://github.com/Exmeaning/Exmeaning-Image-hosting/blob/main/ClickZen/readme/mainGUI.png)

---

## 👨‍💻 从源码运行 (面向开发者)

### 环境要求

-   Windows 10/11 (64位)
-   Python 3.8+
-   Android 设备（需开启USB调试，小米设备需要开启USB调试安全模式）

### 安装步骤

1.  克隆项目
    ```bash
    git clone https://github.com/Exmeaning/ClickZen.git
    cd ClickZen
    ```

2.  安装依赖
    ```bash
    pip install -r requirements.txt
    ```

3.  运行程序
    ```bash
    python main.py
    ```
    首次运行时会自动下载 ADB 和 Scrcpy 工具。

---

## 🔧 技术栈

-   **GUI**: PyQt6
-   **设备通信**: ADB (Android Debug Bridge)
-   **投屏**: Scrcpy
-   **图像识别**: OpenCV
-   **截图**: mss, win32api

---

## 📁 项目结构

```
ClickZen/
├── core/               # 核心功能模块
│   ├── adb_manager.py      # ADB管理
│   ├── auto_monitor.py     # 自动监控
│   ├── device_controller.py # 设备控制
│   └── ...
├── gui/                # GUI界面
│   ├── main_window.py      # 主窗口
│   └── monitor_dialog.py   # 监控配置
├── utils/              # 工具模块
├── main.py            # 程序入口
└── requirements.txt   # 依赖列表
```

---

## 🤝 贡献指南

非常欢迎您的贡献！请通过以下方式参与：

1.  Fork 本项目
2.  创建功能分支 (`git checkout -b feature/AmazingFeature`)
3.  提交更改 (`git commit -m 'Add some AmazingFeature'`)
4.  推送到分支 (`git push origin feature/AmazingFeature`)
5.  提交 Pull Request

### 需要帮助的方面

-   🐛 Bug修复
-   📝 文档改进
-   🎨 UI优化
-   ⚡ 性能优化
-   🌍 国际化支持

---

## 📄 开源协议

本项目采用 MIT 协议开源 - 查看 [LICENSE](LICENSE) 文件了解详情

## 🙏 致谢

-   [Scrcpy](https://github.com/Genymobile/scrcpy) - 优秀的Android投屏工具
-   [Pure-python-adb](https://github.com/Swind/pure-python-adb) - Python ADB客户端
-   [Klick'r](https://github.com/Nain57/Smart-AutoClicker) - 项目功能主要参考的安卓客户端
-   所有贡献者和用户

## 📞 联系方式

-   GitHub Issues: [提交问题](https://github.com/Exmeaning/ClickZen/issues)
-   Pull Requests: [贡献代码](https://github.com/Exmeaning/ClickZen/pulls)

---
**免责声明**：本软件不提供任何形式的保证。作者不对使用本软件导致的任何损失负责。