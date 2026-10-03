# Third-party notices

ClickZen is licensed under the GNU Affero General Public License v3.0 (see `LICENSE`).
It redistributes or depends on the following third-party components.

## Bundled binaries (shipped in the `ThirdParty` folder)

Taken unmodified from the official scrcpy v4.1 Windows release
(`scrcpy-win64-v4.1.zip`, SHA-256 `5b12172b3264b2889f4583ee64752ce832e29bc8b1089dca81093459697165db`).

| Component | Files | License |
|---|---|---|
| scrcpy-server (Genymobile) | `scrcpy-server` | Apache License 2.0 – <https://github.com/Genymobile/scrcpy> (full text in `ThirdParty/LICENSE.txt`) |
| Android Debug Bridge (AOSP) | `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll` | Apache License 2.0 – <https://source.android.com/> |
| FFmpeg 8.1.2 | `avcodec-62.dll`, `avutil-60.dll`, `swresample-6.dll` | GNU LGPL v2.1 or later, dynamically linked and unmodified – <https://ffmpeg.org/>. Build configuration and sources: <https://github.com/Genymobile/scrcpy/tree/v4.1/app/deps> |

## NuGet packages

| Package | License |
|---|---|
| Microsoft.WindowsAppSDK, Microsoft.Windows.SDK.BuildTools | Microsoft Software License Terms (redistributable) |
| Microsoft.Graphics.Win2D | MIT |
| CommunityToolkit.Mvvm, CommunityToolkit.WinUI.* | MIT |
| WinUIEx | MIT |
| Microsoft.Extensions.* | MIT |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File, Serilog.Sinks.Async | Apache License 2.0 |
| AdvancedSharpAdbClient | Apache License 2.0 |
| FFmpeg.AutoGen | GNU LGPL v3 |
| OpenCvSharp4, OpenCvSharp4.runtime.win | Apache License 2.0 (OpenCV: Apache License 2.0) |
| Microsoft.Windows.CsWin32 | MIT |
| Vortice.Direct3D11, Vortice.DXGI | MIT |
| xunit.v3 (tests only) | Apache License 2.0 |
