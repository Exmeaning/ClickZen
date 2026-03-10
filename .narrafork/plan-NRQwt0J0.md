# ClickZen Root 模式实施计划

## 概述

为 ClickZen 增加 Root 模式支持，使已 Root 的设备可以使用更高效的模拟点击方法（通过 `sendevent` / `input` 以 root 权限执行），并将右侧面板的 ADB 命令栏改为 "ADB Root" 模式，同时在 UI 上对用户进行必要的提示（如需在 Root 管理器中授权）。

---

## 一、核心层改动

### 1. `core/adb_manager.py` — 增加 Root 相关方法

- **新增 `root_mode` 属性**：`self.root_mode = False`，标记当前是否启用 Root 模式。
- **新增 `check_root_access()` 方法**：
  - 执行 `adb shell su -c 'id'`，检查返回中是否包含 `uid=0(root)`。
  - 返回 `(bool, str)` — 是否有 root 权限 + 提示信息。
- **新增 `enable_root_mode()` 方法**：
  - 调用 `check_root_access()`，成功则设置 `self.root_mode = True`。
  - 失败则返回提示信息（如"请在 Magisk/SuperSU 中授权 ADB Shell"）。
- **新增 `root_shell(command)` 方法**：
  - 当 `root_mode=True` 时，使用 `su -c '{command}'` 包装命令执行。
  - 否则回退到普通 `shell()`。
- **新增 Root 模式点击方法**：
  - `root_tap(x, y)` — 使用 `su -c 'input tap {x} {y}'`，比普通 `input tap` 更快（绕过权限检查）。
  - `root_swipe(x1, y1, x2, y2, duration)` — 使用 `su -c 'input swipe ...'`。
  - `root_keyevent(keycode)` — 使用 `su -c 'input keyevent ...'`。
  - `root_text(text)` — 使用 `su -c 'input text ...'`。
  - `root_sendevent_tap(x, y)` — 使用 `sendevent` 直接写入触摸设备节点，实现更低延迟的点击（需要先获取触摸设备路径）。
- **修改现有 `shell()` 方法**：已有 `root` 参数支持，保持不变。

### 2. `core/device_controller.py` — 适配 Root 模式

- **新增 `root_mode` 属性**：`self.root_mode = False`。
- **新增 `set_root_mode(enabled)` 方法**：
  - 设置 `self.root_mode = enabled`。
  - 同步到 `self.adb.root_mode`。
- **修改 `click()` / `long_click()` / `swipe()` / `input_text()` / `press_back()` / `press_home()` / `press_recent()`**：
  - 在每个方法中，检查 `self.root_mode`：
    - 若为 True，调用 `self.adb.root_tap()` / `self.adb.root_swipe()` 等 root 版本方法。
    - 若为 False，保持原有 `self.adb.tap()` 等调用。
- **修改 `_execute_action()` 中的播放逻辑**：同样根据 `root_mode` 选择对应方法。

---

## 二、UI 层改动

### 3. `gui/left_panel.py` — 操作模式增加 Root 选项

- **修改 `create_mode_selector()` 中的 `mode_combo`**：
  - 新增第三个选项：`"🔓 Root 设备模式"`, data=`"root_device"`。
- **修改 `on_mode_changed()`**：
  - 新增 `root_device` 分支：
    - 显示设备管理区域（同设备模式）。
    - 按钮文字改为 `"🔓 启动 Scrcpy (Root)"`。
    - 按钮颜色使用橙色系（区分普通设备模式的绿色）。
    - 发射 `simulator_mode_changed.emit(False)` 以退出模拟器模式。
- **新增信号 `root_mode_changed = pyqtSignal(bool)`**：通知主窗口 Root 模式状态变化。

### 4. `gui/right_panel.py` — ADB 命令栏改为 ADB Root

- **修改 `create_adb_widget()`**：
  - 新增 Root 模式切换复选框：`self.root_check = QCheckBox("🔓 Root 模式")`。
  - 放在命令输入框上方。
  - 勾选时，placeholder 文字改为 `"输入ADB Root Shell命令... (以su权限执行)"`。
  - 取消勾选时恢复原文字。
- **新增信号 `root_mode_toggled = pyqtSignal(bool)`**：当 Root 复选框状态变化时发射。
- **修改快捷命令按钮区域**：
  - 新增 `"🔓 Root检测"` 按钮，用于快速检测设备 Root 状态。

### 5. `gui/main_window.py` — 连接 Root 模式逻辑

- **`connect_panel_signals()`** 中新增：
  - 连接 `self.left_panel.root_mode_changed` → `self.on_root_mode_changed()`。
  - 连接 `self.right_panel.root_mode_toggled` → `self.on_root_toggle()`。
- **新增 `on_root_mode_changed(is_root)` 方法**：
  - 调用 `self.adb.enable_root_mode()` 或禁用。
  - 成功时：
    - 同步到 `self.controller.set_root_mode(True)`。
    - 同步右侧面板的 Root 复选框状态。
    - 日志提示 "Root 模式已启用"。
  - 失败时：
    - 弹出提示对话框，告知用户需要在 Root 管理器（Magisk/KernelSU/SuperSU）中授权。
    - 回退模式选择到普通设备模式。
    - 日志提示 "Root 权限获取失败"。
- **新增 `on_root_toggle(enabled)` 方法**：
  - 处理右侧面板 Root 复选框的切换。
  - 逻辑同上。
- **修改 `execute_adb_command()`**：
  - 检查右侧面板的 Root 复选框状态。
  - 若启用 Root，使用 `self.adb.root_shell(command)` 执行。
  - 否则使用 `self.adb.shell(command)`。
- **修改 `on_mode_changed` 相关逻辑**：
  - 当切换到 `root_device` 模式时，自动检测 Root 权限。
  - 首次启用时弹出提示对话框：
    ```
    ⚠️ Root 模式使用须知：
    
    1. 请确保设备已 Root（Magisk / KernelSU / SuperSU）
    2. 首次使用时，请在 Root 管理器中允许 "Shell" 或 "ADB" 的超级用户权限
    3. Root 模式下的点击命令将以 su 权限执行，延迟更低
    4. 如果弹出授权请求，请在手机上点击"允许"
    ```

### 6. `gui/settings_dialog.py` — 设置中增加 Root 配置

- **在 "其他设置" 选项卡中新增 "Root 设置" 分组**：
  - `QCheckBox("默认启用 Root 模式")` — 启动时自动尝试 Root。
  - `QComboBox` Root 点击方式选择：
    - `"su -c input"` — 通过 su 执行 input 命令（兼容性好）。
    - `"sendevent"` — 直接写入设备节点（延迟最低，需要知道触摸设备路径）。
  - `QLabel` 提示文字：说明 Root 模式的前提条件。

### 7. `settings.json` — 新增 Root 配置项

```json
{
  "root": {
    "enabled": false,
    "click_method": "su_input",
    "auto_detect": true
  }
}
```

---

## 三、Root 权限提示流程

### 用户首次启用 Root 模式时的完整流程：

1. 用户在左侧面板选择 "🔓 Root 设备模式"。
2. 系统自动执行 `adb shell su -c 'id'` 检测 Root 权限。
3. **如果检测成功**（返回 `uid=0`）：
   - 日志显示 "✓ Root 权限验证成功"。
   - 启用 Root 模式，右侧 ADB 栏自动切换到 Root 模式。
4. **如果检测失败**：
   - 弹出 `QMessageBox` 提示对话框：
     ```
     🔓 Root 权限获取失败
     
     请检查以下事项：
     ① 设备是否已 Root（安装 Magisk / KernelSU / SuperSU）
     ② 请打开 Root 管理器 App，找到 "Shell" 或 "com.android.shell" 应用
     ③ 将其超级用户权限设置为"允许"
     ④ 如果手机上弹出了授权弹窗，请点击"允许"后重试
     
     设置完成后，请重新选择 Root 模式。
     ```
   - 模式自动回退到普通 "📱 设备模式"。

---

## 四、文件修改清单

| 文件 | 改动类型 | 说明 |
|------|---------|------|
| `core/adb_manager.py` | 新增方法 | `root_mode` 属性、`check_root_access()`、`enable_root_mode()`、`root_shell()`、`root_tap()`、`root_swipe()`、`root_keyevent()`、`root_text()` |
| `core/device_controller.py` | 修改 + 新增 | `root_mode` 属性、`set_root_mode()`、修改 `click/swipe/long_click` 等方法适配 root 调用 |
| `gui/left_panel.py` | 修改 | `mode_combo` 增加 Root 选项、`on_mode_changed()` 增加 root_device 分支、新增 `root_mode_changed` 信号 |
| `gui/right_panel.py` | 修改 | ADB 栏增加 Root 复选框、新增 Root 检测快捷按钮、新增 `root_mode_toggled` 信号 |
| `gui/main_window.py` | 修改 + 新增 | 连接 Root 信号、`on_root_mode_changed()`、`on_root_toggle()`、修改 `execute_adb_command()` |
| `gui/settings_dialog.py` | 修改 | 新增 Root 设置分组 |
| `settings.json` | 修改 | 新增 `root` 配置节 |

---

## 五、实施顺序

1. **`core/adb_manager.py`** — 先实现底层 Root 方法（基础能力）
2. **`core/device_controller.py`** — 适配 Root 模式调用
3. **`settings.json`** — 新增配置项
4. **`gui/left_panel.py`** — 增加 Root 模式选项
5. **`gui/right_panel.py`** — ADB 栏改造
6. **`gui/settings_dialog.py`** — 设置页面增加 Root 配置
7. **`gui/main_window.py`** — 连接所有信号，实现完整流程和提示逻辑
