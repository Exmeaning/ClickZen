# ClickZen MCP 服务器

ClickZen 2.1 内置了一个 [MCP（Model Context Protocol）](https://modelcontextprotocol.io/) 服务器。Claude Code、Cursor、VS Code、Claude Desktop、Codex 等 AI 客户端连接后，就能直接使用 ClickZen：

- **查看**：设备列表、截图、界面元素树（控件文字与坐标）、找图、取色；
- **操作**：点击、长按、滑动、滚动、按键、输入文字（含中文）、启动或关闭应用；
- **自动化**：打开方案、在一台或多台设备上运行和停止方案、启用或禁用任务、读取运行日志；
- **录制回放**：列出 `.czrec` 录制并在设备上回放；
- **变量**：读取和修改自动化变量，与正在运行的方案共享；
- **ADB Shell**：在设备上执行任意 shell 命令。

AI 通过 ClickZen 已经建立的连接工作：已连接的真机、无线设备、模拟器和模拟器窗口都可以直接使用，不需要另外配置 adb 或 scrcpy。

## 快速开始

1. 打开 ClickZen，进入左侧导航的 **MCP** 页，打开 **MCP 服务器** 开关。
2. 在「连接 AI 客户端」中选择你的客户端，点击 **复制配置**，粘贴到客户端里（见下文）。
3. 在 AI 客户端里试一句：「列出 ClickZen 的设备，截一张图，然后打开设置应用」。

页面下方的「最近调用」会实时显示 AI 调用的每一个工具、参数、耗时和结果。

## 服务参数

| 项目 | 默认值 | 说明 |
|---|---|---|
| 地址 | `http://127.0.0.1:9528/mcp` | Streamable HTTP 传输（无状态），协议版本 2024-11-05 至 2026-07-28 |
| 监听范围 | 仅本机 | 打开「允许局域网访问」后监听所有网卡 |
| 鉴权 | Bearer 令牌 | 请求头 `Authorization: Bearer <令牌>`（也接受 `X-ClickZen-Token`）。令牌在首次启用时生成，DPAPI 加密保存，可随时重新生成 |
| 服务状态 | 默认关闭 | 开关状态会保存，下次启动 ClickZen 时自动恢复 |

修改端口或局域网访问后点击 **应用**，服务会自动重启。重新生成令牌后旧令牌立即失效，请同步更新客户端配置。

## 连接各客户端

下面示例中的 `<令牌>` 请替换为 MCP 页上显示的令牌。页面上「复制配置」生成的内容已经填好令牌和地址，直接粘贴即可。

### Claude Code

```bash
claude mcp add --transport http clickzen http://127.0.0.1:9528/mcp --header "Authorization: Bearer <令牌>"
```

### Cursor

`~/.cursor/mcp.json`（全局）或项目内 `.cursor/mcp.json`：

```json
{
  "mcpServers": {
    "clickzen": {
      "url": "http://127.0.0.1:9528/mcp",
      "headers": { "Authorization": "Bearer <令牌>" }
    }
  }
}
```

### VS Code（GitHub Copilot Agent 模式）

`.vscode/mcp.json`：

```json
{
  "servers": {
    "clickzen": {
      "type": "http",
      "url": "http://127.0.0.1:9528/mcp",
      "headers": { "Authorization": "Bearer <令牌>" }
    }
  }
}
```

### Claude Desktop（stdio 桥接）

Claude Desktop 只能启动本地命令，因此使用 ClickZen 自带的 stdio 桥接。编辑 `%APPDATA%\Claude\claude_desktop_config.json`：

```json
{
  "mcpServers": {
    "clickzen": {
      "command": "C:\\Apps\\ClickZen\\ClickZen.exe",
      "args": ["--mcp-stdio"]
    }
  }
}
```

### Codex CLI（stdio 桥接）

`~/.codex/config.toml`：

```toml
[mcp_servers.clickzen]
command = "C:\\Apps\\ClickZen\\ClickZen.exe"
args = ["--mcp-stdio"]
```

### 其他客户端

支持 Streamable HTTP 的客户端填写地址 `http://127.0.0.1:9528/mcp`，并加上请求头 `Authorization: Bearer <令牌>`。只支持 stdio 的客户端使用 `ClickZen.exe --mcp-stdio`。

## stdio 桥接

`ClickZen.exe --mcp-stdio` 不显示界面，通过 stdin/stdout 提供 MCP 服务，并把调用转发给正在运行的 ClickZen：

- **配置里不需要令牌**：端口和令牌从本机 ClickZen 设置中读取，只能由当前 Windows 用户解密。
- **自动启动**：ClickZen 没有运行时，第一次工具调用会自动启动它，最多等待约 25 秒。前提是 MCP 页的开关已经打开。
- **离线也能列出工具**：连不上 ClickZen 时，工具列表照常返回，客户端不会因此报错；调用工具会返回说明，告诉你该如何处理。
- stdout 只输出协议消息，诊断信息写到 stderr。设置环境变量 `CLICKZEN_MCP_DEBUG=1` 可以输出更详细的日志。

| 环境变量 | 作用 |
|---|---|
| `CLICKZEN_DATA_DIR` | 使用指定的数据目录（与主程序一致） |
| `CLICKZEN_MCP_NO_LAUNCH=1` | ClickZen 未运行时不要自动启动它 |
| `CLICKZEN_MCP_DEBUG=1` | 输出调试日志到 stderr |

## 允许 AI 使用的功能

MCP 页上可以分组开关工具。关闭的分组不会出现在工具列表中，即使客户端缓存了工具列表，调用也会被拒绝。修改即时生效，无需重启服务。

| 分组 | 工具 |
|---|---|
| 查看 | `get_status` `list_devices` `connect_device` `list_emulators` `connect_emulator` `bind_emulator_window` `take_screenshot` `dump_ui` `find_image` `get_pixel_color` `wait_for_text` `list_apps` |
| 操作 | `tap` `tap_element` `swipe` `scroll` `press_key` `input_text` `launch_app` `stop_app` |
| 自动化 | `get_scheme` `list_schemes` `open_scheme` `start_automation` `stop_automation` `get_automation_log` `set_task_enabled` |
| 录制回放 | `list_recordings` `play_recording` `stop_playback` |
| 变量 | `get_variables` `set_variable` |
| ADB Shell | `adb_shell` |

## 坐标

- 所有输入工具的坐标都是**设备像素**，以设备**当前方向**为准，范围是 `list_devices` 返回的 `screenWidth × screenHeight`。
- `take_screenshot` 默认把长边缩放到 1280 像素，以节省 AI 的上下文。在截图上量取坐标后，调用 `tap` / `swipe` / `get_pixel_color` 时把截图尺寸作为 `refWidth` / `refHeight` 一起传入，ClickZen 会自动换算成设备坐标。截图返回的文字说明里会写出具体参数。
- `dump_ui` 返回的 `@(x,y)` 中心点和边界已经是设备坐标，可以直接用于 `tap`；更推荐用 `tap_element` 按文字点击。
- 模拟器窗口设备的坐标，以档案中设置的参考分辨率为准；`dump_ui` 读取的是关联 adb 设备的控件树，坐标会自动换算到窗口设备的坐标空间。

## 工具参考

所有工具的 `device` 参数都可以省略，省略时使用 ClickZen 标题栏中选中的当前设备。也可以传 `list_devices` 返回的 `id`（adb 序列号，或窗口设备的 `window:…`），或者设备名称。

### 查看

| 工具 | 主要参数 | 说明 |
|---|---|---|
| `get_status` | — | 版本、adb 是否可用、当前设备、打开的方案、自动化/录制/回放状态、已启用的工具分组 |
| `list_devices` | — | 设备 id、名称、连接方式、状态、屏幕尺寸、密度、是否有实时画面、使用的输入方式 |
| `connect_device` | `device`、`makeCurrent` | 启动投屏或窗口捕获，并等待画面就绪；可以同时设为当前设备 |
| `list_emulators` | — | 检测这台电脑上正在运行的模拟器（MuMu、雷电、夜神、逍遥、蓝叠、Android Studio），返回每个实例的 id、准确的 adb 地址、端口来源、是否已连接、是否已绑定窗口；没有运行中的模拟器时列出已安装的。只读取进程和模拟器配置，不需要 adb。详见[模拟器助手](emulators.md) |
| `connect_emulator` | `emulator`（`list_emulators` 返回的 id、实例名或名称；只运行一个时可省略）、`makeCurrent`（默认 true） | 按准确端口 `adb connect`，之后就能像普通设备一样使用 |
| `bind_emulator_window` | `emulator` | 一键把模拟器窗口绑定为窗口设备：自动定位 Android 画面、关联 adb、保存档案，返回窗口设备 id（`window:…`）和定位方法。无法可靠定位时返回错误，请到 ClickZen 设备页确认 |
| `take_screenshot` | `format`（`jpeg`/`png`）、`maxSize`（默认 1280，0 = 原始分辨率）、`quality`、`x/y/width/height`（裁剪区域） | 返回图片和坐标换算说明。没有画面时会自动开始投屏；截图会等待最近一次 AI 操作之后的新帧 |
| `dump_ui` | `filter`、`maxNodes`（默认 250）、`includeAll` | 原生界面元素：类名、文字、描述、资源 id、可点击/可滚动/勾选等状态，以及中心点和边界。游戏、视频、画布类界面返回的内容很少 |
| `find_image` | `template`（方案中的模板名）或 `imageBase64`、`imageScale`、`threshold`（默认 0.85）、`grayscale`、`multiScale`、搜索区域 | OpenCV 模板匹配，返回是否找到、相似度、位置和中心点。`imageBase64` 若是从缩放后的截图上裁下来的，需要传 `imageScale` = 设备宽度 ÷ 截图宽度（截图返回的文字说明里会直接给出这个值），否则尺寸对不上会找不到 |
| `get_pixel_color` | `x`、`y`、`refWidth/refHeight` | 返回 `#RRGGBB` 和 r/g/b |
| `wait_for_text` | `text`、`timeoutMs`（默认 10000，上限 60000）、`gone` | 轮询控件树，直到指定文字出现（或在 `gone=true` 时消失） |
| `list_apps` | `filter`、`includeSystem` | 已安装应用的包名 |

### 操作

| 工具 | 主要参数 | 说明 |
|---|---|---|
| `tap` | `x`、`y`、`durationMs`（500 以上为长按）、`refWidth/refHeight` | 点击或长按 |
| `tap_element` | `text` 或 `resourceId`、`exact`、`index`、`longPress` | 按文字、描述或资源 id 找到控件并点击其中心；找不到时返回屏幕上可见的文字 |
| `swipe` | `x1,y1,x2,y2`、`durationMs`（默认 300） | 直线滑动；短时长相当于快速甩动，长时长适合精确拖动 |
| `scroll` | `direction`（up/down/left/right）、`distance`（0.1–0.9）、可选区域 | `down` 表示查看下方内容（手指向上滑） |
| `press_key` | `key`（名称、`KEYCODE_*` 或数字）、`repeat` | 支持 home、back、recents、enter、delete、tab、space、escape、menu、search、power、wakeup、sleep、音量键、方向键、翻页键、媒体键等 |
| `input_text` | `text`（最多 4000 字）、`pressEnter` | 向当前焦点输入框输入文字。非 ASCII 文字（如中文）通过 scrcpy 剪贴板粘贴，需要 adb 设备处于投屏状态 |
| `launch_app` | `package` | 启动应用的启动器 Activity |
| `stop_app` | `package` | 强制停止应用（`am force-stop`） |

### 自动化

| 工具 | 主要参数 | 说明 |
|---|---|---|
| `get_scheme` | — | 当前打开的方案：名称、文件、参考分辨率、任务（含启用状态）、模板名、变量声明、内嵌录制、校验问题；运行中时包含各设备的状态和轮数 |
| `list_schemes` | — | `文档\ClickZen\Schemes` 中的 `.czscheme` 文件 |
| `open_scheme` | `path`（完整路径或文件名）、`discardChanges` | 打开方案。当前方案有未保存修改或正在运行时会拒绝，除非 `discardChanges=true` |
| `start_automation` | `devices`（可选） | 在指定设备上运行；不传时使用自动化页选定的目标设备，否则使用当前设备 |
| `stop_automation` | — | 停止所有引擎并等待它们结束 |
| `get_automation_log` | `max`（默认 50，上限 500） | 运行日志（任务触发、警告、错误） |
| `set_task_enabled` | `task`（名称或 id）、`enabled` | 启用或禁用任务；方案会被标记为未保存，下次运行时生效 |

### 录制回放

| 工具 | 主要参数 | 说明 |
|---|---|---|
| `list_recordings` | — | `文档\ClickZen\Recordings` 中的 `.czrec` 文件 |
| `play_recording` | `recording`（路径、文件名或方案内嵌录制 id）、`speed`（0.1–10）、`loops`、`randomize`、`wait` | 按原节奏回放，坐标自动缩放到设备屏幕。默认立即返回；`wait=true` 时等回放结束再返回（最长 10 分钟） |
| `stop_playback` | — | 停止 AI 或录制页发起的回放 |

### 变量

| 工具 | 主要参数 | 说明 |
|---|---|---|
| `get_variables` | — | 全局变量，以及每台运行中设备的方案变量 |
| `set_variable` | `name`、`value`（数字/布尔/字符串）、`device` | 不传 `device` 时写入全局变量（所有方案可见）；传 `device` 时写入该设备运行中方案的方案变量。已声明的变量保持声明时的类型 |

### ADB Shell

| 工具 | 主要参数 | 说明 |
|---|---|---|
| `adb_shell` | `command`、`asRoot`、`timeoutSeconds`（默认 30，上限 300） | 返回 stdout 和 stderr，超过 64 KB 的部分会截断。命令会写入 ClickZen 日志 |

工具失败时返回 `isError: true`，文本中说明原因和处理办法，例如「设备未连接」「坐标超出屏幕」「该工具已在 ClickZen 中关闭」。

## 使用建议

- 先 `dump_ui` 再操作：原生应用用 `tap_element` 按文字点击，比根据截图估计坐标更准确，也更省上下文。
- 游戏和画布类界面用 `take_screenshot`、`find_image` 和 `get_pixel_color`。需要看清小字时，使用 `format=png` 或裁剪区域。
- 每次操作后再截一次图，确认结果。
- 重复性的流程可以在 ClickZen 中做成自动化方案，让 AI 用 `start_automation` 启动，再用 `get_variables` / `get_automation_log` 跟踪进度，比逐步点击更稳定、更快。

## 安全

- 默认只监听 `127.0.0.1`，并且所有请求都必须带令牌。
- 带 `Origin` 请求头的请求必须来自本机页面（局域网模式下也接受与请求主机相同的来源），以防止网页借助 DNS rebinding 访问本机服务。仅本机模式下还会校验 `Host`。
- **服务不提供 TLS**：打开局域网访问后，令牌和截图会以明文在网络中传输，只应在可信网络中使用，切勿暴露到公网。
- AI 可以执行的操作，等同于你在 ClickZen 中可以做的操作。`adb_shell` 能修改系统设置、删除数据，不需要时请在 MCP 页关闭对应分组。很多 AI 客户端会在调用标记为破坏性的工具前请求确认，`adb_shell`、`stop_app`、`open_scheme` 都带有破坏性标记。
- 令牌用 Windows DPAPI 加密保存在 `%LocalAppData%\ClickZen\settings.json` 中，只有当前 Windows 用户能解密。

## 故障排查

| 现象 | 处理 |
|---|---|
| 客户端报 401 | 令牌错误或已重新生成：重新复制配置 |
| 客户端报 403 | 请求来自浏览器页面或非本机地址：本机客户端请使用 `127.0.0.1`；局域网访问需要打开「允许局域网访问」 |
| 连接被拒绝 | ClickZen 未运行、MCP 开关未打开，或端口不一致；查看 MCP 页的状态栏 |
| 启动失败：端口被占用 | 修改端口后点击「应用」，并同步更新客户端配置 |
| stdio 客户端提示 MCP 已关闭 | 在 ClickZen 的 MCP 页打开开关后重试 |
| 截图超时 | 设备未授权 USB 调试，或模拟器窗口被最小化：先在 ClickZen 的投屏页确认画面正常 |
| `dump_ui` 没有内容 | 当前界面是游戏、视频或 WebView 画布：改用截图和找图 |
| 中文输入失败 | 需要 adb 设备处于 scrcpy 投屏状态（先调用 `connect_device`）；也可以改用 `adb_shell` 配合 ADB 输入法 |

## 自检

```powershell
$env:CLICKZEN_DATA_DIR = "$env:TEMP\cz-mcp-selftest"
& .\ClickZen.exe --selftest-mcp
```

自检会在随机端口上启动 MCP 服务，用官方 SDK 客户端经 HTTP 调用工具，检查无令牌请求被拒绝、关闭能力后工具被隐藏等行为，再启动 `ClickZen.exe --mcp-stdio` 子进程验证 stdio 桥接。全部通过时退出码为 0。自检不需要设备，也不会启动 adb。
