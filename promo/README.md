# ClickZen 手绘动效宣传片

项目包含 10 秒短片和 114 秒中文配音介绍片的渲染源码、配音和封面。成片体积较大，不提交 Git，可在 [Google Drive 文件夹](https://drive.google.com/drive/folders/1jtU9fidGnzOoz_bisEL3scW4qsgU6uiO) 查看或下载。画面是功能示意动画，并非实际操作录屏；模拟器后台捕获的效果依环境而异。

长片章节：00:00 痛点、00:12 原生重写、00:25 投屏录制回放、00:40 模拟器、00:54 视觉自动化、01:09 多设备、01:23 MCP、01:42 开源收尾。字幕按片段近似分配时间，未做逐字强制对齐。

## 重建

在仓库根目录运行（Node.js、Python 3、NumPy、imageio-ffmpeg 和 FFmpeg 所需环境）：

```sh
cd promo && npm ci && cd ..
python3 -m pip install numpy imageio-ffmpeg
python3 promo/build_long_audio.py
FFMPEG_BIN="$(python3 -c 'import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())')" node promo/long_cel_film.mjs render
# 关键帧检查：node promo/long_cel_film.mjs stills
```

长片写入 `promo/ClickZen-Cel-Long.mp4`；中间文件放在被忽略的 `promo/.render/`，可用 `CZ_BUILD_DIR` 覆盖。短片可用 `python3 promo/build_cel_audio.py` 后运行 `FFMPEG_BIN=... node promo/cel_film_engine.mjs render`。音轨采用已生成的中文旁白，见 `voiceover/`；更换配音时须重新调整分段时间。源码使用确定性视觉扰动及固定随机种子的合成配乐，结果可能随编码器/平台版本有所差异。

长片为 1920×1080，24 fps、2736 帧；主要画面为一拍二，章节切换使用手绘线条、残影和烟雾。长片并非每一帧都人工手绘，而是基于程序化笔压、线条沸腾与运动设计生成的示意动画。最终 MP4 实测综合响度约 -14.3 LUFS，真峰值 -1.5 dBTP；短片实测 -14.0 LUFS、-1.3 dBTP。详见根目录 `CREDITS.md`。
