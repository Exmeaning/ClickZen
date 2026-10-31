#!/usr/bin/env python3
"""
Deterministic 48 kHz Stereo Audio Composer, Procedural Cel Foley & EBU R128 Master
for the 10.000s (240 frames @ 24.000 fps) ClickZen Hand-Drawn Cel Film.

- Tempo: 144.0 BPM (= exact 10.0 frames / beat @ 24 fps, 24 beats across 10.000s)
- Instrumentation: Warm plucked upright pizzicato bass, rosewood marimba/xylophone,
  acoustic jazz brush kit, glockenspiel/vibraphone, and tactile cel foley SFX
- Voiceover: Frame-locked 5-phrase studio cut of vo_10s_a.mp3 with smooth sidechain ducking
- Mastering: Two-pass EBU R128 loudnorm targeting -14.0 LUFS integrated, <= -1.2 dBTP true peak
"""

import math
import os
from pathlib import Path
import imageio_ffmpeg
import subprocess
import numpy as np
import wave

FFMPEG = os.environ.get("FFMPEG_BIN", imageio_ffmpeg.get_ffmpeg_exe())
BUILD_DIR = Path(os.environ.get("CZ_BUILD_DIR", "promo/.render"))
BUILD_DIR.mkdir(parents=True, exist_ok=True)
SR = 48000
FPS = 24.0
DURATION = 10.000
N_SAMPLES = int(SR * DURATION)  # Exact 480,000 samples

RNG = np.random.default_rng(20261031)


def f2s(frame: float) -> float:
    return frame / FPS


def midi_to_hz(m: float) -> float:
    return 440.0 * (2.0 ** ((m - 69.0) / 12.0))


def pan_stereo(mono: np.ndarray, pan: float = 0.0) -> np.ndarray:
    """Equal-power stereo panner (-1.0 left .. +1.0 right)."""
    angle = (pan + 1.0) * (math.pi / 4.0)
    gl = math.cos(angle)
    gr = math.sin(angle)
    out = np.zeros((len(mono), 2), dtype=np.float64)
    out[:, 0] = mono * gl
    out[:, 1] = mono * gr
    return out


def mix_at(buf: np.ndarray, clip: np.ndarray, start_s: float, gain: float = 1.0, pan: float = 0.0):
    idx = int(round(start_s * SR))
    if clip.ndim == 1:
        st = pan_stereo(clip * gain, pan)
    else:
        st = clip * gain
    end = min(N_SAMPLES, idx + len(st))
    if idx < 0:
        st = st[-idx:]
        idx = 0
    if idx >= N_SAMPLES or len(st) == 0:
        return
    n = min(len(st), N_SAMPLES - idx)
    buf[idx : idx + n] += st[:n]


# ============================================================================
# ACOUSTIC INSTRUMENT SYNTHESIS (Warm Plucked Bass, Rosewood Marimba, Vibes, Brushes)
# ============================================================================
def synth_upright_bass(midi: float, dur: float) -> np.ndarray:
    n = int(SR * dur)
    t = np.arange(n) / SR
    f0 = midi_to_hz(midi)
    # Warm woody plucked string: slight pitch drop on pluck + rapidly damping upper harmonics
    f_inst = f0 * (1.0 + 0.018 * np.exp(-t * 45.0))
    phase = 2.0 * np.pi * np.cumsum(f_inst) / SR
    sig = (
        1.00 * np.sin(phase)
        + 0.52 * np.sin(2 * phase) * np.exp(-t * 5.5)
        + 0.24 * np.sin(3 * phase) * np.exp(-t * 11.0)
        + 0.10 * np.sin(4 * phase) * np.exp(-t * 18.0)
    )
    # Finger-board woody thump transient
    noise = RNG.standard_normal(n)
    thump = np.convolve(noise, np.ones(18) / 18.0, mode="same") * np.exp(-t * 95.0) * 0.28
    env = (1.0 - np.exp(-t * 260.0)) * np.exp(-t * 4.2)
    env[-int(0.015 * SR) :] *= np.linspace(1.0, 0.0, int(0.015 * SR))
    return (sig * env + thump) * 0.45


def synth_marimba(midi: float, dur: float) -> np.ndarray:
    """Rosewood bar ratios (1 : 3.98 : 9.2) for authentic Giant Ant playful mallet tone!"""
    n = int(SR * dur)
    t = np.arange(n) / SR
    f0 = midi_to_hz(midi)
    sig = (
        1.00 * np.sin(2 * np.pi * f0 * t) * np.exp(-t * 8.5)
        + 0.34 * np.sin(2 * np.pi * (f0 * 3.98) * t) * np.exp(-t * 28.0)
        + 0.11 * np.sin(2 * np.pi * (f0 * 9.18) * t) * np.exp(-t * 65.0)
    )
    mallet = RNG.standard_normal(n) * np.exp(-t * 180.0) * 0.12
    attack = 1.0 - np.exp(-t * 480.0)
    return (sig * attack + mallet) * 0.38


def synth_vibraphone_chord(midis: list[float], dur: float, trem_hz: float = 5.2) -> np.ndarray:
    n = int(SR * dur)
    t = np.arange(n) / SR
    out = np.zeros((n, 2), dtype=np.float64)
    for i, m in enumerate(midis):
        f0 = midi_to_hz(m)
        trem = 0.82 + 0.18 * np.sin(2 * np.pi * trem_hz * t + i * 0.7)
        tone = (
            np.sin(2 * np.pi * f0 * t) * np.exp(-t * 1.9)
            + 0.22 * np.sin(2 * np.pi * f0 * 4.0 * t) * np.exp(-t * 7.5)
        )
        attack = 1.0 - np.exp(-t * 220.0)
        pan = -0.35 + (i / max(1, len(midis) - 1)) * 0.70
        out += pan_stereo(tone * attack * trem * (0.24 / math.sqrt(len(midis))), pan)
    fade = min(n, int(0.18 * SR))
    out[-fade:] *= np.linspace(1.0, 0.0, fade)[:, None]
    return out


def synth_brush_hat(dur: float = 0.09, open_amt: float = 0.0) -> np.ndarray:
    n = int(SR * dur)
    t = np.arange(n) / SR
    white = RNG.standard_normal(n)
    # High-passed crisp wire-brush texture via first difference
    hp = np.diff(white, prepend=0.0)
    hp = np.diff(hp, prepend=0.0) * 0.35
    decay = 14.0 if open_amt > 0.5 else 38.0
    env = (1.0 - np.exp(-t * 400.0)) * np.exp(-t * decay)
    return hp * env * 0.26


def synth_woodblock(freq: float = 880.0, dur: float = 0.075) -> np.ndarray:
    n = int(SR * dur)
    t = np.arange(n) / SR
    sig = np.sin(2 * np.pi * freq * t) + 0.42 * np.sin(2 * np.pi * freq * 2.42 * t) * np.exp(-t * 70.0)
    env = (1.0 - np.exp(-t * 900.0)) * np.exp(-t * 48.0)
    return sig * env * 0.35


def synth_warm_kick(dur: float = 0.18) -> np.ndarray:
    n = int(SR * dur)
    t = np.arange(n) / SR
    f_inst = 54.0 + 95.0 * np.exp(-t * 42.0)
    phase = 2 * np.pi * np.cumsum(f_inst) / SR
    sig = np.sin(phase) * np.exp(-t * 16.0)
    click = RNG.standard_normal(n) * np.exp(-t * 220.0) * 0.08
    return (sig + click) * 0.52


# ============================================================================
# PROCEDURAL HAND-CRAFTED CEL FOLEY SFX (Frame-Locked to Every Smear, Pop & Burst!)
# ============================================================================
def sfx_whip_whoosh(dur: float = 0.22, f_start: float = 320.0, f_end: float = 1450.0) -> np.ndarray:
    """Filtered air-swish for smear frames."""
    n = int(SR * dur)
    t = np.arange(n) / SR
    u = t / dur
    noise = RNG.standard_normal(n)
    # Time-varying resonant swept band-pass via windowed chirp modulation
    fc = f_start * ((f_end / f_start) ** (u**0.85))
    phase = 2 * np.pi * np.cumsum(fc) / SR
    reson = np.convolve(noise, np.hanning(24) / 8.0, mode="same") * (0.65 + 0.55 * np.sin(phase))
    env = np.sin(np.pi * (u**0.75)) ** 1.4
    return reson * env * 0.42


def sfx_cork_pop() -> np.ndarray:
    """Juicy Giant Ant lip/cork POP + soft airy smoke poof for Act II transition (f 45)!"""
    dur = 0.26
    n = int(SR * dur)
    t = np.arange(n) / SR
    # Rapid upward pitch pop (190 Hz -> 680 Hz in 38ms)
    tp = np.minimum(t, 0.045)
    f_pop = 180.0 + 620.0 * ((tp / 0.045) ** 1.7)
    ph = 2 * np.pi * np.cumsum(f_pop) / SR
    pop = np.sin(ph) * np.exp(-t * 58.0) * 0.75
    # Soft velvety cel smoke 'poof'
    smoke = np.convolve(RNG.standard_normal(n), np.hanning(42) / 14.0, mode="same")
    smoke_env = (1.0 - np.exp(-t * 90.0)) * np.exp(-t * 13.0)
    return pop + smoke * smoke_env * 0.28


def sfx_rubber_boing(f0: float = 210.0, f1: float = 390.0, dur: float = 0.22) -> np.ndarray:
    n = int(SR * dur)
    t = np.arange(n) / SR
    f = f0 + (f1 - f0) * (1.0 - np.exp(-t * 22.0)) + 18.0 * np.sin(2 * np.pi * 24.0 * t) * np.exp(-t * 12.0)
    ph = 2 * np.pi * np.cumsum(f) / SR
    sig = (np.sin(ph) + 0.3 * np.sin(2 * ph)) * np.exp(-t * 14.0) * (1.0 - np.exp(-t * 300.0))
    return sig * 0.34


def sfx_water_droplet(f_base: float = 780.0) -> np.ndarray:
    dur = 0.095
    n = int(SR * dur)
    t = np.arange(n) / SR
    f = f_base * (1.0 + 0.85 * ((t / dur) ** 1.6))
    ph = 2 * np.pi * np.cumsum(f) / SR
    env = np.sin(np.pi * (t / dur)) * np.exp(-t * 22.0)
    return np.sin(ph) * env * 0.28


def sfx_electric_cel_zap(f_root: float = 440.0, dur: float = 0.16) -> np.ndarray:
    """Warm cartoon electric arc crackle (filtered pulse bursts, never harsh buzz)."""
    n = int(SR * dur)
    t = np.arange(n) / SR
    f = f_root * (1.0 + 0.35 * np.sin(2 * np.pi * 38.0 * t))
    ph = 2 * np.pi * np.cumsum(f) / SR
    tone = (np.sin(ph) + 0.45 * np.sin(3 * ph) + 0.25 * np.sin(5 * ph)) * 0.35
    crackle = (RNG.random(n) > 0.965).astype(np.float64) * RNG.uniform(-0.6, 0.6, n)
    crackle = np.convolve(crackle, np.hanning(9) / 3.0, mode="same")
    env = (1.0 - np.exp(-t * 350.0)) * np.exp(-t * 18.0)
    return (tone + crackle) * env * 0.36


def sfx_climax_firework() -> np.ndarray:
    """f 174 (7.25s) Hero Cel Firework Detonation: sub-bloom + chime cascade + paper crackle!"""
    dur = 0.95
    n = int(SR * dur)
    t = np.arange(n) / SR
    out = np.zeros((n, 2), dtype=np.float64)

    # 1. Warm sub-boom bloom
    f_sub = 48.0 + 110.0 * np.exp(-t * 28.0)
    sub = np.sin(2 * np.pi * np.cumsum(f_sub) / SR) * np.exp(-t * 7.5) * 0.55
    out += pan_stereo(sub, 0.0)

    # 2. Sparkling glockenspiel major-9th arpeggio cascade across stereo field
    sparkle_notes = [72, 76, 79, 83, 86, 88, 91, 96]  # Cmaj9 -> C7
    for i, m in enumerate(sparkle_notes):
        delay = int(i * 0.028 * SR)
        rem = n - delay
        if rem <= 0:
            continue
        ts = np.arange(rem) / SR
        hz = midi_to_hz(m)
        chime = (
            np.sin(2 * np.pi * hz * ts) * np.exp(-ts * 5.5)
            + 0.28 * np.sin(2 * np.pi * hz * 2.76 * ts) * np.exp(-ts * 16.0)
        ) * (1.0 - np.exp(-ts * 600.0))
        pan = -0.65 + (i / (len(sparkle_notes) - 1)) * 1.30
        out[delay:] += pan_stereo(chime * 0.16, pan)

    # 3. Festive cel paper-firework crackle
    pop_mask = (RNG.random(n) > 0.992).astype(np.float64) * np.exp(-t * 4.5)
    pops = np.convolve(pop_mask * RNG.uniform(-1, 1, n), np.hanning(14) / 4.0, mode="same")
    out += pan_stereo(pops * 0.35, 0.1)
    return out


# ============================================================================
# MAIN SCORE, FOLEY & STUDIO VOICEOVER ASSEMBLY
# ============================================================================
def build_mix():
    music = np.zeros((N_SAMPLES, 2), dtype=np.float64)
    sfx = np.zeros((N_SAMPLES, 2), dtype=np.float64)
    vo = np.zeros((N_SAMPLES, 2), dtype=np.float64)

    beat_s = 10.0 / FPS  # 10 frames/beat = 0.416667s (144 BPM)

    # --- 1. MUSICAL SCORE (6 Bars @ 144 BPM = 24 Beats = 240 Frames) ---
    # Act I (Beats 0..4, f 0..44): Quirky chromatic-jazz walking pizzicato bass + rim ticks
    act1_bass = [
        (0.0, 45, 0.38),  # A2
        (1.0, 46, 0.38),  # Bb2
        (2.0, 47, 0.38),  # B2
        (3.0, 48, 0.38),  # C3
        (3.5, 50, 0.35),  # D3
        (4.0, 52, 0.25),  # E3 tension before POP!
    ]
    for b, m, d in act1_bass:
        mix_at(music, synth_upright_bass(m, d), b * beat_s, gain=0.85, pan=-0.08)

    # Act II & III Upbeat Groove (Beats 4.5 .. 17.2, f 45 .. 172): C6/9 -> Fmaj7 -> Dm9 -> G13!
    walking_bass = [
        (4.5, 48), (5.5, 52), (6.5, 55), (7.5, 57),  # C -> E -> G -> A
        (8.5, 53), (9.5, 57), (10.5, 60), (11.5, 55),  # F -> A -> C -> G
        (12.5, 50), (13.5, 53), (14.5, 57), (15.5, 43),  # D -> F -> A -> G2
        (16.2, 47), (16.8, 50),  # B2 -> D3 leading into f 174 climax!
    ]
    for b, m in walking_bass:
        mix_at(music, synth_upright_bass(m, 0.42), b * beat_s, gain=0.92, pan=-0.05)

    # Playful Giant Ant Rosewood Marimba syncopated melody!
    marimba_notes = [
        (4.5, 72, -0.25), (5.0, 76, 0.20), (5.5, 79, -0.15), (6.25, 81, 0.25),
        (7.0, 79, 0.0), (7.5, 76, -0.2),
        (8.6, 77, 0.22), (9.1, 81, -0.22), (9.6, 84, 0.18), (10.25, 79, -0.15),
        (11.3, 76, -0.3), (11.8, 79, 0.0), (12.3, 81, 0.3),
        (13.0, 84, -0.18), (13.5, 81, 0.18), (14.0, 79, 0.0),
        (14.6, 74, -0.25), (15.1, 77, 0.25), (15.6, 83, -0.2), (16.2, 86, 0.22),
        (16.7, 88, 0.0), (17.0, 91, 0.15),
    ]
    for b, m, pan in marimba_notes:
        mix_at(music, synth_marimba(m, 0.28), b * beat_s, gain=0.78, pan=pan)

    # Light Jazz Brush Groove + Kick on beats 4.5 .. 17.0
    b = 4.5
    step_idx = 0
    while b < 17.2:
        if step_idx % 2 == 0:
            mix_at(music, synth_warm_kick(), b * beat_s, gain=0.72, pan=0.0)
        mix_at(music, synth_brush_hat(0.08, 1.0 if step_idx % 2 == 1 else 0.0), b * beat_s, gain=0.68, pan=0.18)
        # Swing offbeat brush tick
        mix_at(music, synth_brush_hat(0.05, 0.0), (b + 0.66) * beat_s, gain=0.42, pan=-0.18)
        b += 1.0
        step_idx += 1

    # Warm Vibraphone Harmonic Pads on Act III-B (f 146) and Act IV Resolution (f 195)!
    mix_at(music, synth_vibraphone_chord([55, 59, 62, 65, 69], 1.15), f2s(146), gain=0.85)  # G13 lift!
    # Final Hero Lockup C6/9 Resolution Chord at f 195 (8.125s -> decays cleanly by 9.95s!)
    mix_at(music, synth_vibraphone_chord([48, 55, 64, 69, 74, 79], 1.78, trem_hz=4.5), f2s(195), gain=1.18)
    mix_at(music, synth_upright_bass(36, 1.72), f2s(195), gain=1.05, pan=0.0)  # Deep C2 root!
    mix_at(music, synth_marimba(72, 0.45), f2s(195), gain=0.85, pan=-0.2)
    mix_at(music, synth_marimba(79, 0.45), f2s(195.6), gain=0.82, pan=0.2)

    # --- 2. FRAME-LOCKED HAND-CRAFTED CEL FOLEY SFX ---
    # f 05: Smear #1 stylus whip-strike!
    mix_at(sfx, sfx_whip_whoosh(0.14, 480, 1650), f2s(4.5), gain=0.95, pan=0.25)
    # f 07, 15, 23, 31: Cartoon finger pokes on overheated phone!
    for idx, hf in enumerate([7, 15, 23, 31]):
        mix_at(sfx, synth_woodblock(690 + idx * 55), f2s(hf), gain=0.85, pan=0.08)
        mix_at(sfx, synth_warm_kick(0.11), f2s(hf), gain=0.55, pan=0.0)

    # f 38..44: Pre-pop swelling tension rise
    mix_at(sfx, sfx_rubber_boing(180, 520, 0.28), f2s(38), gain=0.72, pan=0.0)

    # f 45 (1.875s): Cork POP + Cel Smoke Burst!
    mix_at(sfx, sfx_cork_pop(), f2s(45), gain=1.15, pan=0.0)
    # f 52 (2.167s): Cursor Spirit squash-landing boing!
    mix_at(sfx, sfx_rubber_boing(250, 430, 0.18), f2s(52), gain=0.80, pan=0.0)

    # f 81: Coil-back + f 86: SMEAR #2 Supersonic Emerald Whip-Dash!
    mix_at(sfx, sfx_rubber_boing(420, 220, 0.16), f2s(81), gain=0.65, pan=-0.25)
    mix_at(sfx, sfx_whip_whoosh(0.20, 290, 1850), f2s(85.5), gain=1.15, pan=0.1)
    # f 90, 94, 98: Cel liquid water-splash droplets!
    for i, wf in enumerate([90, 94, 99]):
        mix_at(sfx, sfx_water_droplet(720 + i * 140), f2s(wf), gain=0.85, pan=-0.15 + i * 0.2)

    # f 113, 116, 119: Three OpenCV device cards pop in + f 123 landscape flip spin!
    for i, cf in enumerate([113, 116, 119]):
        mix_at(sfx, synth_woodblock(820 + i * 110), f2s(cf), gain=0.78, pan=-0.45 + i * 0.45)
    mix_at(sfx, sfx_whip_whoosh(0.16, 520, 1280), f2s(123), gain=0.75, pan=0.0)

    # f 150, 152, 154: Three MCP Cel Lightning Arcs!
    for i, zf in enumerate([150, 152, 154]):
        mix_at(sfx, sfx_electric_cel_zap(420 + i * 95, 0.14), f2s(zf), gain=0.82, pan=-0.4 + i * 0.4)

    # f 169: Smear #3 inward spiral -> f 174 (7.250s): GIANT CEL FIREWORK DETONATION!
    mix_at(sfx, sfx_whip_whoosh(0.18, 360, 1600), f2s(169), gain=0.88, pan=0.0)
    mix_at(sfx, sfx_climax_firework(), f2s(174), gain=1.18)

    # f 190..195: Reverse implosion whoosh -> f 195 tactile ClickZen wood-stamp lockup!
    mix_at(sfx, sfx_whip_whoosh(0.19, 1450, 340), f2s(190), gain=0.92, pan=0.0)
    mix_at(sfx, synth_woodblock(1040, 0.06), f2s(195), gain=0.95, pan=-0.12)
    mix_at(sfx, synth_warm_kick(0.16), f2s(195), gain=0.88, pan=0.0)

    # f 216 (9.000s): Tiny cute Android 'n' eyelash blink tick during the 3s hold!
    mix_at(sfx, synth_woodblock(1480, 0.045), f2s(216), gain=0.45, pan=0.28)

    # --- 3. PRECISION 5-PHRASE VOICEOVER PLACEMENT + SMOOTH SIDECHAIN DUCKING ---
    raw = subprocess.check_output([
        FFMPEG, "-v", "error", "-i", str(Path(__file__).parent / "voiceover/short.mp3"),
        "-f", "f32le", "-ac", "1", "-ar", str(SR), "-"
    ])
    vo_src = np.frombuffer(raw, dtype=np.float32).astype(np.float64)

    # Clean natural silence boundaries verified from envelope analysis:
    phrases = [
        # (src_start_s, src_end_s, dest_start_s)
        (0.150, 1.240, f2s(9)),     # f 09..35  (0.375s..1.465s): "告别机械狂点！"
        (1.610, 2.930, f2s(47)),    # f 47..79  (1.958s..3.278s): "ClickZen 二点一，"
        (3.350, 4.130, f2s(88)),    # f 88..107 (3.667s..4.447s): "毫秒投屏、"
        (4.430, 5.230, f2s(115)),   # f 115..134(4.792s..5.592s): "视觉识图，"
        (5.870, 7.860, f2s(146)),   # f 146..194(6.083s..8.073s): "让 AI 一句话接管安卓！"
    ]
    fade_len = int(0.018 * SR)
    fade_in = 0.5 - 0.5 * np.cos(np.linspace(0, np.pi, fade_len))
    fade_out = fade_in[::-1]

    for s0, s1, dst in phrases:
        seg = vo_src[int(s0 * SR) : int(s1 * SR)].copy()
        seg[:fade_len] *= fade_in
        seg[-fade_len:] *= fade_out
        mix_at(vo, seg, dst, gain=1.35, pan=0.0)

    # Smooth sidechain envelope on music & non-transient SFX whenever voiceover speaks!
    vo_mono_abs = np.abs(vo[:, 0])
    win = int(0.065 * SR)
    vo_env = np.convolve(vo_mono_abs, np.hanning(win) / np.sum(np.hanning(win)), mode="same")
    duck_music = 1.0 - 0.58 * np.clip(vo_env / 0.08, 0.0, 1.0)
    duck_sfx = 1.0 - 0.35 * np.clip(vo_env / 0.09, 0.0, 1.0)

    master = music * duck_music[:, None] * 0.72 + sfx * duck_sfx[:, None] * 0.78 + vo * 1.15

    # Clean 15ms micro-fade at 0.000s and smooth 60ms fade-out at 9.940s..10.000s (no click/cut!)
    fi = int(0.015 * SR)
    fo = int(0.060 * SR)
    master[:fi] *= np.linspace(0.0, 1.0, fi)[:, None]
    master[-fo:] *= np.linspace(1.0, 0.0, fo)[:, None]

    # Pre-normalize safely to -3 dBFS before FFmpeg EBU R128 2-pass loudnorm
    peak = np.max(np.abs(master)) or 1.0
    master = master / peak * 0.707

    pre_wav = str(BUILD_DIR / "cel_pre_master.wav")
    out_wav = str(BUILD_DIR / "cel_master_48k.wav")
    pcm16 = np.clip(master * 32767.0, -32768, 32767).astype(np.int16)
    with wave.open(pre_wav, "wb") as wf:
        wf.setnchannels(2)
        wf.setsampwidth(2)
        wf.setframerate(SR)
        wf.writeframes(pcm16.tobytes())

    # Two-pass EBU R128 loudness normalization to -14.0 LUFS integrated, TP <= -1.2 dBTP!
    import json, re
    p1 = subprocess.run(
        [
            FFMPEG, "-y", "-i", pre_wav,
            "-af", "loudnorm=I=-14.0:TP=-1.3:LRA=9.0:print_format=json",
            "-f", "null", "-"
        ],
        capture_output=True, text=True, check=True
    )
    m = re.search(r"\{[\s\S]*\}", p1.stderr)
    stats = json.loads(m.group(0))
    ln_filter = (
        f"loudnorm=I=-14.0:TP=-1.3:LRA=9.0:"
        f"measured_I={stats['input_i']}:"
        f"measured_TP={stats['input_tp']}:"
        f"measured_LRA={stats['input_lra']}:"
        f"measured_thresh={stats['input_thresh']}:"
        f"offset={stats['target_offset']}:linear=true,"
        f"aresample={SR},apad=whole_dur=10.000,atrim=0:10.000"
    )
    subprocess.run(
        [FFMPEG, "-y", "-i", pre_wav, "-af", ln_filter, "-ar", str(SR), "-ac", "2", "-c:a", "pcm_s16le", out_wav],
        check=True
    )
    print("Mastered WAV written to:", out_wav)


if __name__ == "__main__":
    build_mix()
