#!/usr/bin/env python3
"""Assemble generated Mandarin narration and deterministic score for the long film.
Requires generated narration in VO_DIR/seg_01.mp3 ... seg_08.mp3 and FFmpeg.
"""
import os, subprocess, wave, json, re
from pathlib import Path
import numpy as np
import imageio_ffmpeg
import build_cel_audio as cel

SR=48000; DURATION=114; N=SR*DURATION
VO_DIR=os.environ.get('VO_DIR',str(Path(__file__).parent / 'voiceover'))
BUILD_DIR=Path(os.environ.get('CZ_BUILD_DIR','promo/.render'))
BUILD_DIR.mkdir(parents=True,exist_ok=True)
OUT=os.environ.get('LONG_AUDIO',str(BUILD_DIR / 'long_master.wav'))
FF=os.environ.get('FFMPEG_BIN',imageio_ffmpeg.get_ffmpeg_exe())
starts=[10,298,623,969,1325,1689,2008,2470]
trims=[.147,.100,.153,.144,.149,.137,.117,.105]
ends=[11.276,12.801,13.697,14.142,14.445,12.560,18.499,8.934]
cel.N_SAMPLES=N
music=np.zeros((N,2),dtype=np.float64)
# Gentle, repeating but varied 144 BPM arrangement, fully original synthesis.
beat=60/144
bass=[36,43,45,40,41,36,38,43]
mel=[72,76,79,81,79,76,74,67,69,72,76,79,77,74,72,67]
for k in range(int(DURATION/beat)+1):
    t=k*beat
    cel.mix_at(music,cel.synth_upright_bass(bass[(k//2)%len(bass)],.30),t,.28, -.12)
    cel.mix_at(music,cel.synth_brush_hat(.075),t,.11,.25)
    if k%2==0: cel.mix_at(music,cel.synth_marimba(mel[(k//2)%len(mel)],.30),t,.15, .35 if k%4==0 else -.35)
    if k%4==0: cel.mix_at(music,cel.synth_warm_kick(.14),t,.10)
for f in starts[1:]:
    cel.mix_at(music,cel.sfx_cork_pop(),(f-10)/24,.18)
    cel.mix_at(music,cel.sfx_whip_whoosh(.25),(f-12)/24,.12)
voice=np.zeros((N,2),dtype=np.float64)
for i,(f,a,b) in enumerate(zip(starts,trims,ends),1):
    raw=subprocess.check_output([FF,'-v','error','-i',f'{VO_DIR}/seg_{i:02}.mp3','-ss',str(a),'-t',str(b-a),'-f','f32le','-ar',str(SR),'-ac','2','-'])
    clip=np.frombuffer(raw,dtype='<f4').reshape(-1,2)
    cel.mix_at(voice,clip,f/24,.72)
# Constant bed deliberately quiet beneath speech; fade to silence at end.
mix=music+voice
fade=int(SR*1.3); mix[-fade:]*=np.linspace(1,0,fade)[:,None]
raw=str(BUILD_DIR / 'long_mix.wav')
with wave.open(raw,'wb') as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes((np.clip(mix,-.98,.98)*32767).astype('<i2').tobytes())
analysis=subprocess.run([FF,'-hide_banner','-i',raw,'-af','loudnorm=I=-14:TP=-1.8:LRA=11:print_format=json','-f','null','-'],capture_output=True,text=True,check=True)
stats=json.loads(re.search(r'\{[\s\S]*\}',analysis.stderr).group())
fil=(f"loudnorm=I=-14:TP=-1.8:LRA=11:measured_I={stats['input_i']}:"
     f"measured_TP={stats['input_tp']}:measured_LRA={stats['input_lra']}:"
     f"measured_thresh={stats['input_thresh']}:offset={stats['target_offset']}:linear=true")
subprocess.run([FF,'-y','-v','error','-i',raw,'-af',fil,'-ar','48000','-c:a','pcm_s16le',OUT],check=True)
print('Audio ready:',OUT,flush=True)
