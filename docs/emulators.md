# 模拟器助手

ClickZen 会自动检测这台电脑上正在运行的 Android 模拟器，从模拟器自己的配置里读出每个实例**准确的 ADB 端口**，然后在设备页一键连接、一键绑定窗口。AI 助手也可以通过 MCP 工具完成这些操作。

## 使用

打开「设备」页时会自动检测一次，也可以点右上角的「检测模拟器」。检测只读取进程信息和模拟器的配置文件，不调用 adb，也不运行模拟器自带的命令行工具，耗时通常在 1 秒以内。

「检测到的模拟器」区域会列出每个正在运行的实例：

- **名称**：模拟器种类和实例名，例如「MuMu 模拟器 · Android Device」。
- **地址与端口来源**：例如 `127.0.0.1:16384 · 端口来自配置`。端口来源有三种：
  - **配置**：从模拟器自己的配置文件读取，最可靠。
  - **运行信息**：从模拟器运行时写出的文件读取（Android Studio 模拟器）。
  - **按规则推断**：读不到配置时，按实例序号和模拟器公布的端口规则计算。
- **状态**：已连接、未连接、离线或未授权、模拟器未开启 ADB、端口未知。
- **连接**：执行 `adb connect 127.0.0.1:端口`。Android Studio 和雷电的实例会被 adb 自动识别为 `emulator-NNNN`，此时会直接选中，不再重复连接。MuMu 的端口被其他程序占用时会自动顺延，连接失败后会依次尝试端口 +1、+2。
- **全部连接**：连接所有尚未连接的实例。
- **绑定窗口**：一键把模拟器窗口绑定为窗口设备，详见下一节。

没有检测到运行中的模拟器时，会列出这台电脑上已安装的模拟器（来自注册表）。

「添加无线设备 → 扫描模拟器」也改成了先检测、后扫描：先按检测到的准确端口连接，再对检测不到的模拟器尝试常见端口。

## 一键绑定窗口

「绑定窗口」会自动完成原来绑定向导里的四个步骤：

1. 如果还没连接 ADB，先连接。参考分辨率和点击输入都需要 ADB。
2. 截取一张窗口画面，按下面的顺序定位 Android 画面所在的区域：
   1. 整个窗口的宽高比和设备屏幕（`wm size`）一致：整个客户区就是画面。
   2. 模拟器的渲染子窗口，例如雷电的 `RenderWindow`、MuMu 的 `nemudisplay`。
   3. 用 `adb screencap` 取一张设备截图，按几种可能的缩放比例在窗口画面中做模板匹配。
   4. 去掉画面四周颜色单一的标题栏、工具栏和黑边。
3. 用 `wm size` 作为参考分辨率，并按画面方向自动转置。
4. 保存模拟器档案，把这个窗口设备设为当前设备并开始捕获。

同一个实例再次绑定时，会更新原来的档案，不会重复添加。档案里记录了实例标识（`emulatorKey`）。

如果无法可靠地定位画面（例如画面全黑，或正在播放动画），会打开预填好的绑定向导，你只需要确认画面区域后保存。窗口最小化时无法绑定，请先还原窗口。

## 支持的模拟器

| 模拟器 | 识别的进程 | 端口来源 | 验证状态 |
|---|---|---|---|
| MuMu 模拟器 12（4.x / 5.x，含国际版） | `MuMuNxDevice.exe` / `MuMuPlayer.exe`，以及 `MuMuVMMHeadless.exe --comment <实例>` | `vms\<实例>\configs\vm_config.json` 的 `vm.nat.port_forward.adb.host_port`；4.x 读 `.nemu` 中的转发规则；读不到时按 `16384 + 32 × 序号` 推算 | 配置解析已用本机 5.28 国际版的真实文件验证；运行时检测尚未实机验证 |
| 雷电模拟器 3 / 4 / 9 / 14 | `dnplayer.exe`，以及 `Ld9BoxHeadless.exe --comment leidian<N>` | 没有端口配置，按 `5555 + 2 × 序号` 推算（adb 会自动识别为 `emulator-(5554 + 2N)`）；ADB 开关读 `vms\config\leidian<N>.config` 的 `basicSettings.adbDebug` | 安装检测与配置格式已用本机雷电 14 验证；运行时检测尚未实机验证 |
| 夜神模拟器 | `Nox.exe -clone:<实例>` | `bin\BignoxVMS\<实例>\<实例>.vbox` 的转发规则；读不到时按 62001、62025、62026… 推算 | 尚未实机验证 |
| 逍遥模拟器 | `MEmuHeadless.exe --comment <实例>`、`MEmu.exe` | `MemuHyperv VMs\<实例>\<实例>.memu` 的转发规则；读不到时按 `21503 + 10 × 序号` 推算 | 尚未实机验证 |
| 蓝叠 5（国际版 / 中国版） | `HD-Player.exe --instance <实例>` | 注册表 `UserDefinedDir` 下的 `bluestacks.conf`，取 `bst.instance.<实例>.status.adb_port`；`bst.enable_adb_access` 为 0 时提示需要开启 ADB | 尚未实机验证 |
| Android Studio 模拟器 | `qemu-system-*.exe` | `%TEMP%\avd\running\pid_<PID>.ini` 的 `port.adb`；读不到时按命令行 `-port` 推算 | 已在本机 AVD 上实机验证（检测、连接、一键绑定） |

进程名、配置格式和端口规则参考了各模拟器的官方文档，以及 [MaaFramework](https://github.com/MaaXYZ/MaaFramework)、[MAA](https://github.com/MaaAssistantArknights/MaaAssistantArknights)、[ALAS](https://github.com/LmeSzinc/AzurLaneAutoScript)、[StarRailCopilot](https://github.com/LmeSzinc/StarRailCopilot) 的检测代码。标注「尚未实机验证」的模拟器，检测逻辑已用单元测试覆盖，但没有在真机上跑过。如果遇到识别不准确的情况，欢迎提交 Issue，并附上日志中以 `Emulator detected` 开头的行。

## 常见问题

**状态显示「模拟器未开启 ADB」**

- 蓝叠：设置 → 高级 → 打开「Android 调试桥（ADB）」。
- 雷电：设置 → 其他设置 → 把「ADB 调试」设为「开启本地连接」。

改完后点「检测模拟器」刷新状态。

**连接失败（connection refused）**

模拟器可能还在启动，请等 Android 进入桌面后再试。MuMu 开启网络桥接后，ADB 地址会变成局域网 IP，需要在「添加无线设备」里手动输入。

**端口显示「按规则推断」**

这说明没有读到配置文件，常见原因是模拟器以管理员权限运行，ClickZen 读不到它的命令行，或者模拟器的版本较旧。推断的端口在大多数情况下仍然是正确的；连不上时，请在模拟器的设置里查看实际端口，然后手动连接。

**多个 adb 互相冲突**

不同版本的 adb 启动时会结束对方的 adb server。ClickZen 始终使用自带的 adb，不会调用模拟器自带的 adb；如果同时使用其他工具，请尽量让它们使用同一个 adb。

**AI 助手怎么用**

参见 [MCP 文档](mcp.md) 中的 `list_emulators`、`connect_emulator`、`bind_emulator_window`。

## 开发者自检

```powershell
$env:CLICKZEN_DATA_DIR = "$env:TEMP\cz-emulator-selftest"
& .\ClickZen.exe --selftest-emulator
```

需要一个正在运行、带窗口的 Android Studio 模拟器。自检会依次执行：检测 → 一键连接 → 一键绑定 → 再次绑定，并检查端口来源、设备序列号、窗口画面宽高比和屏幕尺寸，以及再次绑定时更新的是不是同一个档案。结束后删除测试档案。退出码含义：0 表示通过，2 表示失败，4 表示没有运行中的模拟器。
