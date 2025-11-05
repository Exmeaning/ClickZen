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

1. **AI代码风险提示**：本项目部分代码由AI辅助生成，可能存在潜在的bug或安全问题。使用前请仔细审查代码，风险自负。
2. **作者能力有限**：本人编程水平有限，代码质量可能不高。作者 Python 水平约等于 Hello World → 欢迎 PR 教我写 class。
3. **使用责任**：请合理使用本工具，遵守相关法律法规。因使用本工具产生的任何问题，作者不承担责任。

---

## ✨ 主要功能

- 🖥️ **设备投屏控制**：通过 Scrcpy 实现低延迟投屏
- 🎬 **操作录制回放**：精确记录和回放用户操作
- 🎯 **图像识别自动化**：基于模板匹配的自动化任务（类Klick’r）
- 🎲 **防检测机制**：随机化操作模拟人工行为
- 📊 **变量系统**：支持条件判断和动态变量
- 🔧 **易用的GUI**：直观的图形界面操作

---
## 📥 资源下载

## 🎥 截图示例
![mainGUI](https://github.com/Exmeaning/Exmeaning-Image-hosting/blob/main/ClickZen/readme/mainGUI.png)

## 🔧 技术栈
- **GUI**: PyQt6
- **设备通信**: ADB (Android Debug Bridge)
- **投屏**: Scrcpy
- **图像识别**: OpenCV
- **截图**: mss, win32api

## 📖 使用教程

### 🧩 基础使用

1. 连接手机并开启 **USB 调试**（小米设备需要额外开启 **USB 调试（安全模式）**）。
2. 点击 **“刷新设备”** 识别设备。
3. 启动 **scrcpy**。

---

### ⚙️ 进阶功能

#### 🎬 录制脚本
- 点击 **“录制”** 会从第一个操作开始录制。
- 点击 **“结束录制”** 完成录制。
- 可以执行录制好的文件——**记得先保存！**

#### 🤖 类 Klick'r 的自动化监控

1. 创建任务，设置好变量和条件（复杂监控方案若需要）。
2. 点击 **“监控区域”** → 选择区域后点击 **“截取区域”**，系统会自动以截取好的区域作为检测模板。
3. 选择匹配阈值与冷却时间。  
   🔸 *冷却时间* 指重复检测时，执行操作中的频率将具有一定冷却 CD，而不是检测的CD。
4. 添加想要执行的动作。  
   可执行操作包括：
   - 变量更改  
   - 点击、滑动、等待等物理操作  
   - 调用你录制好的脚本
   
![autoclickGUI](https://github.com/Exmeaning/Exmeaning-Image-hosting/blob/main/ClickZen/readme/autoclickGUI.png)
----
## 🚀 快速开始

### 环境要求

- Windows 10/11 (64位)
- Python 3.8+
- Android 设备（需开启USB调试 小米设备需要开启ADB安全模式）

### 安装步骤

1. 克隆项目
```bash
git clone https://github.com/Exmeaning/ClickZen.git
cd ClickZen
```

2. 安装依赖
```bash
pip install -r requirements.txt
```

3. 运行程序
```bash
python main.py
```

首次运行时会自动下载 ADB 和 Scrcpy 工具。

### 使用说明

1. **连接设备**：通过USB连接Android设备，开启USB调试
2. **启动投屏**：点击"启动Scrcpy"按钮
3. **录制操作**：点击"开始录制"，在投屏窗口操作
4. **保存/加载**：可保存录制脚本供后续使用
5. **自动化任务**：创建基于图像识别的监控任务

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

1. Fork 本项目
2. 创建功能分支 (`git checkout -b feature/AmazingFeature`)
3. 提交更改 (`git commit -m 'Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 提交 Pull Request

### 需要帮助的方面

- 🐛 Bug修复
- 📝 文档改进
- 🎨 UI优化
- ⚡ 性能优化
- 🌍 国际化支持

---

## 📄 开源协议

本项目采用 MIT 协议开源 - 查看 [LICENSE](LICENSE) 文件了解详情

## 🙏 致谢

- [Scrcpy](https://github.com/Genymobile/scrcpy) - 优秀的Android投屏工具
- [Pure-python-adb](https://github.com/Swind/pure-python-adb) - Python ADB客户端
- [Klick'r](https://github.com/Nain57/Smart-AutoClicker)-项目功能主要参考的安卓客户端
- 所有贡献者和用户

## 📞 联系方式

- GitHub Issues: [提交问题](https://github.com/Exmeaning/ClickZen/issues)
- Pull Requests: [贡献代码](https://github.com/Exmeaning/ClickZen/pulls)

---
**免责声明**：本软件不提供任何形式的保证。作者不对使用本软件导致的任何损失负责。