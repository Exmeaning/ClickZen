# Credits & Third-Party Licenses (`video.mp4`)

All visual artwork, character designs, variable-width brush strokes, line-boil displacement fields, smear frames, cel FX (smoke puffs, liquid splashes, lightning arcs, speed lines), paper grain textures, musical score, and foley sound effects in `video.mp4` were **100% procedurally authored and deterministically rendered from code** for **ClickZen 2.1**.

## Fonts
- **Sarasa Gothic SC (`更纱黑体 SC` — Regular / SemiBold / Bold)**
  - **Author**: Belleve Invis (`be5invis/Sarasa-Gothic`, packaged via `@fontpkg/sarasa-gothic-sc`)
  - **License**: **SIL Open Font License 1.1 (OFL-1.1)** — permissive commercial & non-commercial redistribution and embedding in rendered media.

## Audio & Voiceover
- **Musical Score & Cel Foley (`promo/build_cel_audio.py`)**: 100% original procedural synthesis in Python / NumPy (144 BPM jazz pizzicato upright bass, rosewood marimba, vibraphone, wire-brush percussion, and frame-locked cartoon cel foley), mastered to `-14.0 LUFS` integrated / `-1.3 dBTP` true peak (`48 kHz` stereo).
- **Mandarin Voiceover**: Synthesized specifically for this project via Arena speech synthesis (`voice-00`).

## Rendering & Encoding Toolchain
- **`@napi-rs/canvas`** (`Skia` 2D rendering engine bindings for Node.js) — **MIT License**
- **`FFmpeg 7.0.2`** (`libx264` + native `aac` + `loudnorm` / `ebur128`, via `imageio-ffmpeg`) — **GPLv3 / LGPLv2.1+**
- **Subject Project**: **ClickZen 2 (`Exmeaning/ClickZen`)** — **GNU AGPL v3.0**

## Long-form introduction
- `promo/ClickZen-Cel-Long.mp4`: 114-second procedural cel-style illustration, narration, music and effects; the voiceover recordings are in `promo/voiceover/`.
- The illustrations are conceptual, not recordings of the application. Background window capture depends on the emulator and capture environment.
- `promo/build_long_audio.py` and `promo/long_cel_film.mjs` build the extended version using the same tools and fonts listed above. The final encoded long version measured approximately -14.3 LUFS and -1.5 dBTP.
