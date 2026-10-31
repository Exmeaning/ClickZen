import fs from "node:fs";
import path from "node:path";
import { spawn } from "node:child_process";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { createCanvas, GlobalFonts } = require("@napi-rs/canvas");

const FONT_DIR = path.dirname(require.resolve("@fontpkg/sarasa-gothic-sc/package.json"));
const BUILD_DIR = process.env.CZ_BUILD_DIR || path.resolve("promo/.render");
fs.mkdirSync(BUILD_DIR, { recursive: true });
GlobalFonts.registerFromPath(path.join(FONT_DIR, "sarasa-gothic-sc-regular.ttf"), "Sarasa");
GlobalFonts.registerFromPath(path.join(FONT_DIR, "sarasa-gothic-sc-semibold.ttf"), "SarasaSemi");
GlobalFonts.registerFromPath(path.join(FONT_DIR, "sarasa-gothic-sc-bold.ttf"), "SarasaBold");

// ============================================================================
// FIXED BENCHMARK OUTPUT SPEC: 1920x1080, 24.000 fps, 240 frames = 10.000s
// ============================================================================
const W = 1920;
const H = 1080;
const FPS = 24;
const TOTAL_FRAMES = 240;

// Strictly Disciplined 4-Color Cel / Risograph Palette (+ Cream Paper)
const PAL = {
  paper: "#F5EDE0",
  paperDark: "#E6DAC6",
  ink: "#191B22",
  emerald: "#0BB87A",
  emeraldLight: "#6EE7B7",
  tomato: "#EE4B36",
  marigold: "#F5B731",
  cream: "#FFFDF8",
};

// ============================================================================
// DETERMINISTIC MATH, EASING & SEEDED PRNG (Zero Math.random / Zero Clocks!)
// ============================================================================
function mulberry32(seed) {
  let a = (seed >>> 0) + 0x6d2b79f5;
  return function () {
    a = Math.imul(a ^ (a >>> 15), a | 1);
    a ^= a + Math.imul(a ^ (a >>> 7), a | 61);
    return ((a ^ (a >>> 14)) >>> 0) / 4294967296;
  };
}

const clamp = (v, lo = 0, hi = 1) => Math.max(lo, Math.min(hi, v));
const lerp = (a, b, t) => a + (b - a) * t;
const prog = (f, fStart, fDur) => clamp((f - fStart) / Math.max(0.0001, fDur));

const easeOutCubic = (x) => 1 - Math.pow(1 - clamp(x), 3);
const easeOutQuint = (x) => 1 - Math.pow(1 - clamp(x), 5);
const easeInCubic = (x) => Math.pow(clamp(x), 3);
const easeInOutCubic = (x) => {
  const c = clamp(x);
  return c < 0.5 ? 4 * c * c * c : 1 - Math.pow(-2 * c + 2, 3) / 2;
};
const easeOutBack = (x, overshoot = 1.85) => {
  const c = clamp(x);
  const c3 = overshoot + 1;
  return 1 + c3 * Math.pow(c - 1, 3) + overshoot * Math.pow(c - 1, 2);
};
const elasticOut = (x) => {
  const c = clamp(x);
  if (c === 0 || c === 1) return c;
  return Math.pow(2, -9 * c) * Math.sin((c * 10 - 0.75) * ((2 * Math.PI) / 3)) + 1;
};

// Quantize frame index for true cel stepping: 1s (fast), 2s (standard cel), 3s (composed hold)
const stepFrame = (f, hold = 2) => Math.floor(f / hold) * hold;
const boilVariant = (f, hold = 2, variants = 4) => Math.floor(f / hold) % variants;

// ============================================================================
// PRE-BAKED PROCEDURAL PAPER TEXTURE + GRAIN MULTIPLY LAYER
// ============================================================================
function createPaperLayers() {
  // Base warm cream sheet with subtle warm corner vignette
  const baseCanvas = createCanvas(W, H);
  const bctx = baseCanvas.getContext("2d");
  bctx.fillStyle = PAL.paper;
  bctx.fillRect(0, 0, W, H);

  const vig = bctx.createRadialGradient(W * 0.5, H * 0.48, H * 0.28, W * 0.5, H * 0.5, W * 0.68);
  vig.addColorStop(0, "rgba(255, 252, 245, 0.25)");
  vig.addColorStop(1, "rgba(214, 196, 168, 0.34)");
  bctx.fillStyle = vig;
  bctx.fillRect(0, 0, W, H);

  // Subtle cotton paper flecks on base
  const rng = mulberry32(20261031);
  for (let i = 0; i < 2400; i++) {
    const x = rng() * W;
    const y = rng() * H;
    const len = 2 + rng() * 8;
    const ang = rng() * Math.PI;
    bctx.strokeStyle = rng() > 0.5 ? "rgba(180, 162, 136, 0.14)" : "rgba(255, 255, 255, 0.22)";
    bctx.lineWidth = 0.8 + rng() * 0.9;
    bctx.beginPath();
    bctx.moveTo(x, y);
    bctx.lineTo(x + Math.cos(ang) * len, y + Math.sin(ang) * len);
    bctx.stroke();
  }

  // 2 Boil-able Overlays for subtle living paper grain (Multiply ~12%)
  const grainCanvases = [0, 1].map((idx) => {
    const gc = createCanvas(W, H);
    const gctx = gc.getContext("2d");
    gctx.fillStyle = "#ffffff";
    gctx.fillRect(0, 0, W, H);
    const grng = mulberry32(9901 + idx * 777);
    // Cold-press dimples & fine organic grain
    for (let i = 0; i < 16000; i++) {
      const x = grng() * W;
      const y = grng() * H;
      const r = 0.7 + grng() * 1.9;
      const shade = 212 + Math.floor(grng() * 34); // 212..246 -> ~12% multiply darkening!
      gctx.fillStyle = `rgb(${shade},${shade - 3},${shade - 8})`;
      gctx.fillRect(x, y, r, r);
    }
    return gc;
  });

  return { baseCanvas, grainCanvases };
}

// ============================================================================
// CUSTOM VARIABLE-WIDTH BRUSH RENDERER + LINE BOIL + OFF-REGISTER CEL FILL
// ============================================================================
function resamplePolyline(pts, closed = false, spacing = 7) {
  const src = closed ? [...pts, pts[0]] : pts;
  const out = [src[0]];
  for (let i = 0; i < src.length - 1; i++) {
    const [x0, y0] = src[i];
    const [x1, y1] = src[i + 1];
    const dist = Math.hypot(x1 - x0, y1 - y0);
    const steps = Math.max(1, Math.ceil(dist / spacing));
    for (let s = 1; s <= steps; s++) {
      const u = s / steps;
      out.push([lerp(x0, x1, u), lerp(y0, y1, u)]);
    }
  }
  return out;
}

function displaceBoilPoints(pts, seed, variant, boilAmp = 2.4, closed = false) {
  const rng = mulberry32(seed * 31 + variant * 997 + 13);
  const ph1 = rng() * Math.PI * 2;
  const ph2 = rng() * Math.PI * 2;
  const ph3 = rng() * Math.PI * 2;
  const f1 = 2.0 + Math.floor(rng() * 3); // integer cycles for seamless closed loops!
  const f2 = 5.0 + Math.floor(rng() * 4);
  const n = pts.length;

  return pts.map(([x, y], i) => {
    const prev = pts[closed ? (i - 1 + n) % n : Math.max(0, i - 1)];
    const next = pts[closed ? (i + 1) % n : Math.min(n - 1, i + 1)];
    let tx = next[0] - prev[0];
    let ty = next[1] - prev[1];
    const tl = Math.hypot(tx, ty) || 1;
    tx /= tl;
    ty /= tl;
    const nx = -ty;
    const ny = tx;

    const s = i / Math.max(1, n - 1);
    const wave =
      0.62 * Math.sin(2 * Math.PI * f1 * s + ph1) +
      0.38 * Math.sin(2 * Math.PI * f2 * s + ph2);
    const tangWave = 0.25 * Math.cos(2 * Math.PI * f1 * s + ph3);
    // Pin open endpoints slightly less or let them breathe naturally
    const d = boilAmp * wave;
    const dt = boilAmp * 0.35 * tangWave;
    return {
      x: x + nx * d + tx * dt,
      y: y + ny * d + ty * dt,
      nx,
      ny,
      s,
    };
  });
}

/**
 * Core Variable-Width Ink Brush Renderer:
 * Converts centerline + calligraphic pressure profile + 4-variant boil into a filled outline polygon!
 */
function drawBrushStroke(ctx, rawPts, opts = {}) {
  const {
    width = 6.5,
    color = PAL.ink,
    closed = false,
    taperStart = closed ? 0 : 0.65,
    taperEnd = closed ? 0 : 0.65,
    boilAmp = 2.2,
    seed = 1,
    variant = 0,
    spacing = 6,
  } = opts;

  if (rawPts.length < 2) return;
  const sampled = resamplePolyline(rawPts, closed, spacing);
  const dpts = displaceBoilPoints(sampled, seed, variant, boilAmp, closed);
  const rng = mulberry32(seed * 17 + variant * 131);
  const wPhase = rng() * Math.PI * 2;

  const left = [];
  const right = [];
  for (let i = 0; i < dpts.length; i++) {
    const p = dpts[i];
    const s = p.s;
    let pressure = 1.0 + 0.18 * Math.sin(2 * Math.PI * 3 * s + wPhase);
    if (!closed) {
      if (s < 0.18 && taperStart > 0) {
        pressure *= lerp(1 - taperStart, 1, easeOutCubic(s / 0.18));
      }
      if (s > 0.82 && taperEnd > 0) {
        pressure *= lerp(1 - taperEnd, 1, easeOutCubic((1 - s) / 0.18));
      }
    }
    const hw = Math.max(0.8, (width * 0.5) * pressure);
    left.push([p.x + p.nx * hw, p.y + p.ny * hw]);
    right.push([p.x - p.nx * hw, p.y - p.ny * hw]);
  }

  ctx.save();
  ctx.fillStyle = color;
  ctx.beginPath();
  if (closed) {
    // Two opposite-winding closed subpaths = true hollow ink brush ring with zero seam/loop!
    ctx.moveTo(left[0][0], left[0][1]);
    for (let i = 1; i < left.length; i++) ctx.lineTo(left[i][0], left[i][1]);
    ctx.closePath();
    ctx.moveTo(right[right.length - 1][0], right[right.length - 1][1]);
    for (let i = right.length - 2; i >= 0; i--) ctx.lineTo(right[i][0], right[i][1]);
    ctx.closePath();
    ctx.fill("evenodd");
  } else {
    ctx.moveTo(left[0][0], left[0][1]);
    for (let i = 1; i < left.length; i++) ctx.lineTo(left[i][0], left[i][1]);
    for (let i = right.length - 1; i >= 0; i--) ctx.lineTo(right[i][0], right[i][1]);
    ctx.closePath();
    ctx.fill();

    // Round brush nib caps on open strokes
    const p0 = dpts[0];
    const pN = dpts[dpts.length - 1];
    const r0 = Math.max(1, width * 0.5 * (1 - taperStart * 0.85));
    const rN = Math.max(1, width * 0.5 * (1 - taperEnd * 0.85));
    ctx.beginPath();
    ctx.arc(p0.x, p0.y, r0, 0, Math.PI * 2);
    ctx.arc(pN.x, pN.y, rN, 0, Math.PI * 2);
    ctx.fill();
  }
  ctx.restore();
}

/**
 * Off-Register Cel Fill + Boiling Brush Outline (Signature Giant Ant / Buck Hybrid!)
 */
function drawCelPolygon(ctx, rawPts, opts = {}) {
  const {
    fill = PAL.emerald,
    stroke = PAL.ink,
    strokeWidth = 6.5,
    regOffset = [4.5, 3.5], // Misregistration offset in px!
    boilAmp = 2.2,
    seed = 10,
    variant = 0,
    closed = true,
  } = opts;

  if (fill) {
    const sampledFill = resamplePolyline(rawPts, closed, 7);
    const fillPts = displaceBoilPoints(sampledFill, seed + 97, variant, boilAmp * 1.15, closed);
    ctx.save();
    ctx.fillStyle = fill;
    ctx.beginPath();
    ctx.moveTo(fillPts[0].x + regOffset[0], fillPts[0].y + regOffset[1]);
    for (let i = 1; i < fillPts.length; i++) {
      ctx.lineTo(fillPts[i].x + regOffset[0], fillPts[i].y + regOffset[1]);
    }
    ctx.closePath();
    ctx.fill();
    ctx.restore();
  }

  if (stroke && strokeWidth > 0) {
    drawBrushStroke(ctx, rawPts, {
      width: strokeWidth,
      color: stroke,
      closed,
      boilAmp,
      seed,
      variant,
    });
  }
}

// Geometric Polygon Generators (Circles, Rounded Rects, Bezier Curves, Arcs)
function makeEllipsePts(cx, cy, rx, ry, segments = 32, startAng = 0, endAng = Math.PI * 2) {
  const pts = [];
  const full = Math.abs(endAng - startAng - Math.PI * 2) < 0.01;
  const count = full ? segments : segments + 1;
  for (let i = 0; i < count; i++) {
    const a = lerp(startAng, endAng, i / segments);
    pts.push([cx + Math.cos(a) * rx, cy + Math.sin(a) * ry]);
  }
  return pts;
}

function makeRoundRectPts(x, y, w, h, r, segPerCorner = 6) {
  const rr = Math.min(r, w / 2, h / 2);
  const corners = [
    [x + w - rr, y + rr, -Math.PI / 2, 0],
    [x + w - rr, y + h - rr, 0, Math.PI / 2],
    [x + rr, y + h - rr, Math.PI / 2, Math.PI],
    [x + rr, y + rr, Math.PI, Math.PI * 1.5],
  ];
  const pts = [];
  for (const [cx, cy, a0, a1] of corners) {
    for (let i = 0; i <= segPerCorner; i++) {
      const a = lerp(a0, a1, i / segPerCorner);
      pts.push([cx + Math.cos(a) * rr, cy + Math.sin(a) * rr]);
    }
  }
  return pts;
}

function makeQuadPts(p0, p1, p2, steps = 20) {
  const pts = [];
  for (let i = 0; i <= steps; i++) {
    const t = i / steps;
    const u = 1 - t;
    pts.push([
      u * u * p0[0] + 2 * u * t * p1[0] + t * t * p2[0],
      u * u * p0[1] + 2 * u * t * p1[1] + t * t * p2[1],
    ]);
  }
  return pts;
}

// ============================================================================
// SIGNATURE CEL FX PRIMITIVES: SMOKE PUFFS, WATER SPLASHES, SMEARS, LIGHTNING
// ============================================================================

// 1. Hand-Drawn Boiling Cel Smoke Puff (Cloud -> Curling Donut Ring -> Dissolving Wisps)
function drawCelSmokePuff(ctx, cx, cy, maxR, p, seed, variant, fill = PAL.cream) {
  if (p <= 0 || p >= 1) return;
  const rng = mulberry32(seed);
  const baseRot = rng() * Math.PI * 2;
  const r = maxR * (0.32 + 0.68 * easeOutCubic(p));

  ctx.save();
  ctx.translate(cx, cy);
  ctx.rotate(baseRot + p * 0.55);

  if (p < 0.72) {
    // Scalloped 6-lobe cloud silhouette
    const lobes = 6;
    const pts = [];
    const steps = 42;
    for (let i = 0; i < steps; i++) {
      const a = (i / steps) * Math.PI * 2;
      const bump = 1 + 0.22 * Math.abs(Math.cos((lobes * a) / 2)) * (1 - p * 0.45);
      pts.push([Math.cos(a) * r * bump, Math.sin(a) * r * bump]);
    }
    drawCelPolygon(ctx, pts, {
      fill,
      stroke: PAL.ink,
      strokeWidth: lerp(6.5, 3.2, p),
      regOffset: [3.5, 3.0],
      boilAmp: 2.6,
      seed,
      variant,
    });

    // Inner hollow crescent cutout (expands from p=0.14..0.72 to create classic cel donut smoke!)
    if (p > 0.14) {
      const hp = (p - 0.14) / 0.58;
      const holeR = r * 0.76 * easeOutCubic(hp);
      const holePts = makeEllipsePts(r * 0.14, -r * 0.08, holeR, holeR * 0.88, 24);
      drawCelPolygon(ctx, holePts, {
        fill: PAL.paper,
        stroke: PAL.ink,
        strokeWidth: lerp(4.5, 2.5, hp),
        regOffset: [0, 0],
        boilAmp: 2.0,
        seed: seed + 33,
        variant,
      });
    }
  } else {
    // Break into 3 tiny evaporating wisps!
    const wp = (p - 0.72) / 0.28;
    for (let k = 0; k < 3; k++) {
      const ka = (k / 3) * Math.PI * 2 + 0.4;
      const wr = r * 0.26 * (1 - wp);
      if (wr < 2.5) continue;
      const wx = Math.cos(ka) * (r * (0.85 + wp * 0.35));
      const wy = Math.sin(ka) * (r * (0.85 + wp * 0.35));
      const wpts = makeEllipsePts(wx, wy, wr * 1.2, wr * 0.85, 16);
      drawCelPolygon(ctx, wpts, {
        fill,
        stroke: PAL.ink,
        strokeWidth: 3.0 * (1 - wp * 0.5),
        regOffset: [2, 2],
        boilAmp: 1.8,
        seed: seed + k * 19,
        variant,
      });
    }
  }
  ctx.restore();
}

// 2. Hand-Drawn Liquid Splash / Spark Teardrops (水花 / 火花飞溅 — aspect-aware so nothing clips!)
function drawCelSplashBurst(ctx, cx, cy, minR, maxR, p, count, color, seed, variant, yAspect = 0.72) {
  if (p <= 0 || p >= 1) return;
  const rng = mulberry32(seed);
  for (let i = 0; i < count; i++) {
    const ang = (i / count) * Math.PI * 2 + (rng() - 0.5) * 0.32;
    const speedMul = 0.72 + rng() * 0.45;
    const ep = easeOutQuint(p);
    const dist = lerp(minR, maxR * speedMul, ep);
    const len = Math.sin(p * Math.PI) * (34 + rng() * 30);
    const thick = Math.sin(p * Math.PI) * (9 + rng() * 6.5);
    if (thick < 1.2) continue;

    const x = cx + Math.cos(ang) * dist;
    const y = cy + Math.sin(ang) * dist * yAspect + p * p * 20;
    // Keep strictly inside 5% safe frame margins
    if (x < 75 || x > W - 75 || y < 65 || y > H - 65) continue;

    const tangentAng = Math.atan2(Math.sin(ang) * yAspect, Math.cos(ang));
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(tangentAng);
    const tdPts = [
      [-len * 0.8, 0],
      [-len * 0.1, -thick * 0.65],
      [len * 0.42, -thick * 0.92],
      [len * 0.68, 0],
      [len * 0.42, thick * 0.92],
      [-len * 0.1, thick * 0.65],
    ];
    drawCelPolygon(ctx, tdPts, {
      fill: color,
      stroke: PAL.ink,
      strokeWidth: 3.6,
      regOffset: [2.5, 2.0],
      boilAmp: 1.4,
      seed: seed + i * 11,
      variant,
    });
    ctx.restore();
  }
}

// 3. 4-Pointed Hand-Drawn Cel Star Sparkle
function drawCelStar(ctx, cx, cy, r, rot, color, seed, variant) {
  if (r < 2) return;
  ctx.save();
  ctx.translate(cx, cy);
  ctx.rotate(rot);
  const pts = [];
  for (let i = 0; i < 8; i++) {
    const a = (i * Math.PI) / 4;
    const rad = i % 2 === 0 ? r : r * 0.28;
    pts.push([Math.cos(a) * rad, Math.sin(a) * rad]);
  }
  drawCelPolygon(ctx, pts, {
    fill: color,
    stroke: PAL.ink,
    strokeWidth: Math.max(3, r * 0.13),
    regOffset: [3, 2.5],
    boilAmp: 1.6,
    seed,
    variant,
  });
  ctx.restore();
}

// 4. SMEAR FRAME GENERATOR (Organic Teardrop-Tapered 250%+ Motion Smear + Speed Slits!)
function drawCurvedSmear(ctx, p0, pCtrl, p2, wStart, wEnd, fillColor, seed, variant, arrowHead = false) {
  const spine = makeQuadPts(p0, pCtrl, p2, 26);
  const left = [];
  const right = [];
  for (let i = 0; i < spine.length; i++) {
    const u = i / (spine.length - 1);
    const prev = spine[Math.max(0, i - 1)];
    const next = spine[Math.min(spine.length - 1, i + 1)];
    let tx = next[0] - prev[0];
    let ty = next[1] - prev[1];
    const tl = Math.hypot(tx, ty) || 1;
    const nx = -ty / tl;
    const ny = tx / tl;
    // Organic comet profile: pointed tail at u=0, full belly at u=0.72, rounded nose taper at u=1!
    const env = Math.sin(Math.pow(u, 0.68) * Math.PI);
    const hw = Math.max(3, lerp(wStart * 0.15, wEnd, env) * 0.52);
    left.push([spine[i][0] + nx * hw, spine[i][1] + ny * hw]);
    right.push([spine[i][0] - nx * hw, spine[i][1] - ny * hw]);
  }
  const poly = [...left, ...right.reverse()];
  drawCelPolygon(ctx, poly, {
    fill: fillColor,
    stroke: PAL.ink,
    strokeWidth: 6.5,
    regOffset: [5.5, 4],
    boilAmp: 2.5,
    seed,
    variant,
  });

  // Optional stretched cursor arrowhead leading the smear!
  if (arrowHead) {
    const prev = spine[spine.length - 3];
    const tip = spine[spine.length - 1];
    const ang = Math.atan2(tip[1] - prev[1], tip[0] - prev[0]) + Math.PI / 2;
    ctx.save();
    ctx.translate(tip[0], tip[1]);
    ctx.rotate(ang);
    ctx.scale(1.15, 1.45);
    drawCelPolygon(
      ctx,
      [[0, -72], [58, 22], [0, 8], [-58, 22]],
      { fill: fillColor, stroke: PAL.ink, strokeWidth: 6.5, regOffset: [4, 3], boilAmp: 2, seed: seed + 9, variant }
    );
    ctx.restore();
  }

  // Classic cel negative-space speed-slit streaks inside the smear!
  for (const offsetFrac of [-0.18, 0.18]) {
    const slit = spine.slice(5, 21).map((pt, idx) => {
      const i = idx + 5;
      const prev = spine[i - 1];
      const next = spine[i + 1];
      const tl = Math.hypot(next[0] - prev[0], next[1] - prev[1]) || 1;
      const nx = -(next[1] - prev[1]) / tl;
      const ny = (next[0] - prev[0]) / tl;
      const hw = wEnd * offsetFrac;
      return [pt[0] + nx * hw, pt[1] + ny * hw];
    });
    drawBrushStroke(ctx, slit, {
      width: 6.5,
      color: PAL.paper,
      taperStart: 0.92,
      taperEnd: 0.92,
      boilAmp: 1.4,
      seed: seed + 51,
      variant,
    });
  }

  // Trailing dry-brush speed lines behind smear
  for (let k = -1; k <= 1; k++) {
    const tail = makeQuadPts(
      [p0[0] - 55, p0[1] + k * 30],
      [lerp(p0[0], pCtrl[0], 0.45), lerp(p0[1], pCtrl[1], 0.45) + k * 24],
      [lerp(p0[0], pCtrl[0], 0.85), lerp(p0[1], pCtrl[1], 0.85) + k * 18],
      10
    );
    drawBrushStroke(ctx, tail, {
      width: 4.5,
      color: PAL.ink,
      taperStart: 0.95,
      taperEnd: 0.88,
      boilAmp: 2.0,
      seed: seed + 80 + k,
      variant,
    });
  }
}

// 5. Hand-Drawn Jagged Cel Lightning Bolt with Sharp Electric Sub-Branches (手绘分叉闪电)
function drawCelLightning(ctx, x1, y1, x2, y2, jagAmp, color, seed, variant) {
  const rng = mulberry32(seed + variant * 71);
  const dx = x2 - x1;
  const dy = y2 - y1;
  const len = Math.hypot(dx, dy) || 1;
  const nx = -dy / len;
  const ny = dx / len;
  const kinks = 7;
  const spine = [[x1, y1]];
  for (let i = 1; i < kinks; i++) {
    const u = i / kinks;
    const side = (i % 2 === 0 ? 1 : -1) * jagAmp * (0.65 + rng() * 0.75);
    spine.push([x1 + dx * u + nx * side, y1 + dy * u + ny * side]);
  }
  spine.push([x2, y2]);

  // Chiseled sharp-angled cel lightning bolt (crisp zig-zag silhouette, not rounded sausage!)
  const left = [];
  const right = [];
  for (let i = 0; i < spine.length; i++) {
    const u = i / (spine.length - 1);
    const w = Math.sin(u * Math.PI) * 15 + 2.5;
    left.push([spine[i][0] + nx * w, spine[i][1] + ny * w]);
    right.push([spine[i][0] - nx * w * 0.75, spine[i][1] - ny * w * 0.75]);
  }
  drawCelPolygon(ctx, [...left, ...right.reverse()], {
    fill: color,
    stroke: PAL.ink,
    strokeWidth: 4.5,
    regOffset: [3.5, 3],
    boilAmp: 1.2,
    seed,
    variant,
  });

  // Two electric cel sub-branches forking off kink #2 and kink #4!
  for (const kIdx of [2, 4]) {
    const base = spine[kIdx];
    const sign = kIdx === 2 ? 1 : -1;
    const bMid = [base[0] + dx * 0.11 + nx * sign * 38, base[1] + dy * 0.11 + ny * sign * 38];
    const bTip = [bMid[0] + dx * 0.09 + nx * sign * 18, bMid[1] + dy * 0.09 + ny * sign * 18];
    drawBrushStroke(ctx, [base, bMid, bTip], {
      width: 5.5,
      color: PAL.ink,
      taperStart: 0.1,
      taperEnd: 0.95,
      boilAmp: 1.5,
      seed: seed + kIdx * 19,
      variant,
    });
  }
}

// ============================================================================
// HERO CHARACTERS: CLICKZEN CURSOR SPIRIT & BOILING ANDROID SPIRITS
// ============================================================================
function drawCursorSpirit(ctx, x, y, opts = {}) {
  const {
    rot = -0.35, // Classic cursor tilt (~ -20 deg)
    sx = 1,
    sy = 1,
    scale = 1,
    stride = 0, // Rubber-hose leg dance phase
    wink = false,
    excited = false,
    variant = 0,
    seed = 500,
  } = opts;

  ctx.save();
  ctx.translate(x, y);
  ctx.rotate(rot);
  ctx.scale(sx * scale, sy * scale);

  // Rubber-hose legs dancing beneath the cursor body!
  const legSwing = Math.sin(stride) * 28;
  const legLift = Math.max(0, Math.cos(stride)) * 18;
  const leftHip = [-18, 68];
  const leftFoot = [-36 + legSwing, 134 - legLift];
  const rightHip = [22, 62];
  const rightFoot = [40 - legSwing, 130 - Math.max(0, -Math.cos(stride)) * 18];

  drawBrushStroke(
    ctx,
    makeQuadPts(leftHip, [-38, 102], leftFoot, 10),
    { width: 8.5, color: PAL.ink, boilAmp: 1.8, seed: seed + 1, variant }
  );
  drawBrushStroke(
    ctx,
    makeQuadPts(rightHip, [42, 98], rightFoot, 10),
    { width: 8.5, color: PAL.ink, boilAmp: 1.8, seed: seed + 2, variant }
  );

  // Cute chunky ink boots
  drawCelPolygon(ctx, makeEllipsePts(leftFoot[0] - 8, leftFoot[1] + 4, 18, 10, 14), {
    fill: PAL.tomato,
    stroke: PAL.ink,
    strokeWidth: 5,
    regOffset: [2.5, 2],
    seed: seed + 3,
    variant,
  });
  drawCelPolygon(ctx, makeEllipsePts(rightFoot[0] + 10, rightFoot[1] + 4, 18, 10, 14), {
    fill: PAL.tomato,
    stroke: PAL.ink,
    strokeWidth: 5,
    regOffset: [2.5, 2],
    seed: seed + 4,
    variant,
  });

  // Iconic ClickZen Arrow Body (plump, tactile rounded arrow polygon!)
  const arrowPts = [
    [0, -118],    // Tip of cursor!
    [92, 26],     // Right wing
    [34, 28],     // Right notch
    [54, 92],     // Stem bottom-right
    [12, 106],    // Stem bottom-left
    [-8, 42],     // Left notch
    [-64, 68],    // Left wing
  ];
  drawCelPolygon(ctx, arrowPts, {
    fill: PAL.emerald,
    stroke: PAL.ink,
    strokeWidth: 8,
    regOffset: [6, 4.5],
    boilAmp: 2.3,
    seed: seed + 10,
    variant,
  });

  // Expressive Cartoon Eyes on Cream Patch!
  const eyeY = -12;
  drawCelPolygon(ctx, makeEllipsePts(-8, eyeY, 15, 20, 16), {
    fill: PAL.cream,
    stroke: PAL.ink,
    strokeWidth: 4.5,
    regOffset: [1.5, 1.5],
    boilAmp: 1.2,
    seed: seed + 20,
    variant,
  });
  drawCelPolygon(ctx, makeEllipsePts(24, eyeY - 2, 15, 20, 16), {
    fill: PAL.cream,
    stroke: PAL.ink,
    strokeWidth: 4.5,
    regOffset: [1.5, 1.5],
    boilAmp: 1.2,
    seed: seed + 21,
    variant,
  });

  if (wink) {
    drawBrushStroke(ctx, [[-17, eyeY], [-7, eyeY - 7], [2, eyeY]], {
      width: 5,
      boilAmp: 1,
      seed: seed + 22,
      variant,
    });
  } else {
    ctx.fillStyle = PAL.ink;
    ctx.beginPath();
    ctx.arc(-5, eyeY, 7.5, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = PAL.cream;
    ctx.beginPath();
    ctx.arc(-2.5, eyeY - 3, 2.6, 0, Math.PI * 2);
    ctx.fill();
  }
  ctx.fillStyle = PAL.ink;
  ctx.beginPath();
  ctx.arc(26, eyeY - 2, 7.5, 0, Math.PI * 2);
  ctx.fill();
  ctx.fillStyle = PAL.cream;
  ctx.beginPath();
  ctx.arc(28.5, eyeY - 5, 2.6, 0, Math.PI * 2);
  ctx.fill();

  // Happy open grin!
  if (excited) {
    drawCelPolygon(
      ctx,
      [[-4, 18], [24, 16], [19, 36], [1, 37]],
      { fill: PAL.tomato, stroke: PAL.ink, strokeWidth: 4.2, regOffset: [1.5, 1.5], boilAmp: 1, seed: seed + 25, variant }
    );
  } else {
    drawBrushStroke(ctx, [[-3, 20], [10, 29], [24, 18]], {
      width: 4.8,
      boilAmp: 1.1,
      seed: seed + 26,
      variant,
    });
  }

  // Iconic 3 Click Rays radiating off the cursor tip!
  const rays = [
    [[-24, -132], [-52, -158]],
    [[0, -140], [2, -176]],
    [[26, -130], [54, -152]],
  ];
  rays.forEach((r, idx) => {
    drawBrushStroke(ctx, r, {
      width: 8,
      color: PAL.marigold,
      boilAmp: 1.8,
      seed: seed + 30 + idx,
      variant,
    });
    drawBrushStroke(ctx, r, {
      width: 4.2,
      color: PAL.ink,
      boilAmp: 1.8,
      seed: seed + 40 + idx,
      variant,
    });
  });

  ctx.restore();
}

// Cute Boiling Android Dome Buddy (Echoing the Android 'n' in ClickZen!)
function drawAndroidBuddy(ctx, x, y, opts = {}) {
  const {
    rot = 0,
    sx = 1,
    sy = 1,
    scale = 1,
    fill = PAL.emerald,
    blink = false,
    antennaWiggle = 0,
    variant = 0,
    seed = 700,
  } = opts;

  ctx.save();
  ctx.translate(x, y);
  ctx.rotate(rot);
  ctx.scale(sx * scale, sy * scale);

  // Springy Antennae
  drawBrushStroke(
    ctx,
    [[-34, -44], [-56 + antennaWiggle, -84]],
    { width: 8, color: PAL.ink, boilAmp: 1.8, seed: seed + 1, variant }
  );
  drawBrushStroke(
    ctx,
    [[34, -44], [56 + antennaWiggle, -84]],
    { width: 8, color: PAL.ink, boilAmp: 1.8, seed: seed + 2, variant }
  );

  // Arch / Dome n-shaped body!
  const archPts = [];
  for (let i = 0; i <= 16; i++) {
    const a = Math.PI + (i / 16) * Math.PI;
    archPts.push([Math.cos(a) * 68, Math.sin(a) * 62]);
  }
  archPts.push([68, 58], [28, 58]);
  for (let i = 0; i <= 10; i++) {
    const a = (i / 10) * -Math.PI;
    archPts.push([Math.cos(a) * 28, 58 + Math.sin(a) * 44]);
  }
  archPts.push([-28, 58], [-68, 58]);

  drawCelPolygon(ctx, archPts, {
    fill,
    stroke: PAL.ink,
    strokeWidth: 7.5,
    regOffset: [5, 4],
    boilAmp: 2.2,
    seed: seed + 5,
    variant,
  });

  // Eyes
  if (blink) {
    drawBrushStroke(ctx, [[-34, -22], [-18, -22]], { width: 5, seed: seed + 8, variant });
    drawBrushStroke(ctx, [[18, -22], [34, -22]], { width: 5, seed: seed + 9, variant });
  } else {
    drawCelPolygon(ctx, makeEllipsePts(-26, -24, 9, 10, 12), {
      fill: PAL.cream,
      stroke: PAL.ink,
      strokeWidth: 4,
      regOffset: [1, 1],
      boilAmp: 1,
      seed: seed + 11,
      variant,
    });
    drawCelPolygon(ctx, makeEllipsePts(26, -24, 9, 10, 12), {
      fill: PAL.cream,
      stroke: PAL.ink,
      strokeWidth: 4,
      regOffset: [1, 1],
      boilAmp: 1,
      seed: seed + 12,
      variant,
    });
  }
  ctx.restore();
}

// Hand-Drawn Boiling Smartphone Frame (supports Portrait & Landscape morph!)
function drawCelPhone(ctx, x, y, opts = {}) {
  const {
    w = 220,
    h = 390,
    rot = 0,
    faceRot = 0,
    sx = 1,
    sy = 1,
    bodyFill = PAL.cream,
    screenFill = PAL.emerald,
    stressed = false,
    seed = 300,
    variant = 0,
  } = opts;

  ctx.save();
  ctx.translate(x, y);
  ctx.rotate(rot);
  ctx.scale(sx, sy);

  const outer = makeRoundRectPts(-w / 2, -h / 2, w, h, 32, 6);
  drawCelPolygon(ctx, outer, {
    fill: bodyFill,
    stroke: PAL.ink,
    strokeWidth: 8,
    regOffset: [5, 4],
    boilAmp: stressed ? 4.2 : 2.3,
    seed,
    variant,
  });

  const pad = 20;
  const inner = makeRoundRectPts(-w / 2 + pad, -h / 2 + pad + 12, w - pad * 2, h - pad * 2 - 24, 18, 5);
  drawCelPolygon(ctx, inner, {
    fill: screenFill,
    stroke: PAL.ink,
    strokeWidth: 5.5,
    regOffset: [4, 3],
    boilAmp: stressed ? 3.8 : 1.9,
    seed: seed + 15,
    variant,
  });

  // Speaker earpiece notch
  drawBrushStroke(ctx, [[-22, -h / 2 + 16], [22, -h / 2 + 16]], {
    width: 4.5,
    color: PAL.ink,
    boilAmp: 1.0,
    seed: seed + 18,
    variant,
  });

  ctx.save();
  ctx.rotate(faceRot);
  if (stressed) {
    drawBrushStroke(ctx, [[-48, -55], [-18, -25]], { width: 7, seed: seed + 30, variant });
    drawBrushStroke(ctx, [[-18, -55], [-48, -25]], { width: 7, seed: seed + 31, variant });
    drawBrushStroke(ctx, [[18, -55], [48, -25]], { width: 7, seed: seed + 32, variant });
    drawBrushStroke(ctx, [[48, -55], [18, -25]], { width: 7, seed: seed + 33, variant });
    drawBrushStroke(
      ctx,
      [[-46, 36], [-22, 18], [0, 38], [22, 18], [46, 36]],
      { width: 6.5, seed: seed + 34, variant }
    );
  } else {
    drawBrushStroke(ctx, [[-38, -22], [-26, -34], [-14, -22]], { width: 6, seed: seed + 40, variant });
    drawBrushStroke(ctx, [[14, -22], [26, -34], [38, -22]], { width: 6, seed: seed + 41, variant });
    drawBrushStroke(ctx, [[-24, 14], [0, 34], [24, 14]], { width: 6, seed: seed + 42, variant });
  }
  ctx.restore();

  ctx.restore();
}

// ============================================================================
// HAND-INKED BOILING `ClickZen` HERO EMBLEM & EDITORIAL TYPOGRAPHY (ACT IV)
// ============================================================================
function drawBoilingHeroLockup(ctx, cx, cy, scale, variant, blinkN = false, antennaWig = 0) {
  ctx.save();
  ctx.translate(cx, cy);
  ctx.scale(scale, scale);

  // Tight, balanced Pentagram/Buck logotype kerning centered at x = 0!
  const cX = -445;
  const cY = -6;
  const cOuter = makeEllipsePts(cX, cY, 106, 106, 26, 0.68, Math.PI * 2 - 0.65);
  const cInner = makeEllipsePts(cX, cY, 64, 64, 26, 0.68, Math.PI * 2 - 0.65).reverse();
  drawCelPolygon(ctx, [...cOuter, ...cInner], {
    fill: PAL.ink,
    stroke: PAL.ink,
    strokeWidth: 4.5,
    regOffset: [0, 0],
    boilAmp: 2.2,
    seed: 901,
    variant,
  });

  // Off-register Emerald Cursor clicking inside the 'C'!
  ctx.save();
  ctx.translate(cX + 28, cY + 34);
  ctx.rotate(-0.42);
  const miniArrow = [
    [0, -56],
    [48, 16],
    [18, 18],
    [28, 56],
    [4, 64],
    [-6, 26],
    [-34, 40],
  ];
  drawCelPolygon(ctx, miniArrow, {
    fill: PAL.emerald,
    stroke: PAL.ink,
    strokeWidth: 5.5,
    regOffset: [4.5, 3.5],
    boilAmp: 1.9,
    seed: 902,
    variant,
  });
  [
    [[-16, -66], [-34, -84]],
    [[0, -70], [0, -94]],
    [[18, -64], [36, -80]],
  ].forEach((r, i) => {
    drawBrushStroke(ctx, r, { width: 8, color: PAL.emerald, boilAmp: 1.6, seed: 910 + i, variant });
    drawBrushStroke(ctx, r, { width: 3.8, color: PAL.ink, boilAmp: 1.6, seed: 920 + i, variant });
  });
  ctx.restore();

  const bOffX = ((variant % 2) - 0.5) * 2.4;
  const bOffY = (Math.floor(variant / 2) - 0.5) * 2.2;

  ctx.save();
  ctx.font = "206px SarasaBold";
  ctx.textBaseline = "middle";
  ctx.textAlign = "left";

  // "lick" flush next to C
  ctx.fillStyle = "rgba(238, 75, 54, 0.22)";
  ctx.fillText("lick", -322 + 5, -2 + 4);
  ctx.fillStyle = PAL.ink;
  ctx.fillText("lick", -322 + bOffX, -2 + bOffY);

  // "Ze" flush next to k!
  const zeX = 24;
  ctx.fillStyle = PAL.emerald;
  ctx.fillText("Ze", zeX + 6 - bOffX, -2 + 5 + bOffY);
  ctx.lineWidth = 9;
  ctx.lineJoin = "round";
  ctx.strokeStyle = PAL.ink;
  ctx.strokeText("Ze", zeX + bOffX, -2 - bOffY);
  ctx.fillStyle = PAL.emerald;
  ctx.fillText("Ze", zeX + bOffX * 0.5, -2);
  ctx.restore();

  // Hand-brushed ink underline swash beneath ClickZen!
  drawBrushStroke(
    ctx,
    makeQuadPts([-515, 120], [-10, 140], [492, 116], 24),
    { width: 9.5, color: PAL.ink, taperStart: 0.75, taperEnd: 0.85, boilAmp: 2.6, seed: 940, variant }
  );
  drawBrushStroke(
    ctx,
    makeQuadPts([-380, 136], [50, 152], [440, 132], 18),
    { width: 6, color: PAL.emerald, taperStart: 0.85, taperEnd: 0.85, boilAmp: 2.4, seed: 941, variant }
  );

  // 3. The Iconic Android 'n' Buddy kerning cleanly next to 'e' (+388)!
  drawAndroidBuddy(ctx, 388, 20, {
    scale: 1.12,
    fill: PAL.emerald,
    blink: blinkN,
    antennaWiggle: antennaWig,
    variant,
    seed: 950,
  });

  // Hand-drawn Marigold Version Stamp Pill at top-right of logo
  ctx.save();
  ctx.translate(502, -98);
  ctx.rotate(0.11);
  drawCelPolygon(ctx, makeRoundRectPts(-68, -26, 136, 52, 24, 5), {
    fill: PAL.marigold,
    stroke: PAL.ink,
    strokeWidth: 5,
    regOffset: [3.5, 3],
    boilAmp: 1.8,
    seed: 960,
    variant,
  });
  ctx.fillStyle = PAL.ink;
  ctx.font = "28px SarasaBold";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillText("v2.1", 0, 1);
  ctx.restore();

  ctx.restore();
}

// ============================================================================
// EDITORIAL HAND-INKED CAPTION BANNER (Matches voiceover phrases cleanly)
// ============================================================================
const SUBTITLE_CUES = [
  { f0: 11, f1: 42, text: "告别机械狂点！", accent: PAL.tomato },
  { f0: 45, f1: 82, text: "ClickZen 2.1 · 手感觉醒", accent: PAL.emerald },
  { f0: 85, f1: 112, text: "毫秒投屏 · 丝滑跟手", accent: PAL.emerald },
  { f0: 113, f1: 144, text: "OpenCV 视觉识图 · 横竖屏自适应", accent: PAL.marigold },
  { f0: 145, f1: 196, text: "内置 MCP 服务 · 让 AI 一句话接管安卓！", accent: PAL.tomato },
];

function drawEditorialCaption(ctx, f) {
  let cue = null;
  for (const c of SUBTITLE_CUES) {
    if (f >= c.f0 && f <= c.f1) {
      cue = c;
      break;
    }
  }
  if (!cue) return;

  const qf = stepFrame(f, 2);
  const bv = boilVariant(f, 2);
  const pop = easeOutBack(prog(qf, cue.f0, 5), 1.6);

  ctx.save();
  ctx.font = "40px SarasaBold";
  const tw = ctx.measureText(cue.text).width;
  const bw = tw + 74;
  const bh = 70;

  ctx.translate(W / 2, 968);
  ctx.scale(0.88 + 0.12 * pop, 0.88 + 0.12 * pop);

  // Off-register accent plate + cream paper card + boiling ink border!
  drawCelPolygon(ctx, makeRoundRectPts(-bw / 2 + 6, -bh / 2 + 6, bw, bh, 18, 5), {
    fill: cue.accent,
    stroke: null,
    regOffset: [0, 0],
    boilAmp: 1.8,
    seed: 1200 + cue.f0,
    variant: bv,
  });
  drawCelPolygon(ctx, makeRoundRectPts(-bw / 2, -bh / 2, bw, bh, 18, 5), {
    fill: PAL.cream,
    stroke: PAL.ink,
    strokeWidth: 5.5,
    regOffset: [0, 0],
    boilAmp: 1.8,
    seed: 1250 + cue.f0,
    variant: bv,
  });

  ctx.fillStyle = PAL.ink;
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillText(cue.text, ((bv % 2) - 0.5) * 1.4, 2);
  ctx.restore();
}

// ============================================================================
// MASTER 240-FRAME CHOREOGRAPHY (PURE FUNCTION OF FRAME f ∈ [0..239])
// ============================================================================
function renderFilmFrame(ctx, f, paperLayers) {
  // 1. Warm Cream Paper Ground
  ctx.drawImage(paperLayers.baseCanvas, 0, 0);

  // --------------------------------------------------------------------------
  // ACT I (f 000..047 | 0.00s–1.96s): MANUAL TAPPING NIGHTMARE + SMEAR #1!
  // --------------------------------------------------------------------------
  if (f < 50) {
    const qf = stepFrame(f, 2);
    const bv2 = boilVariant(f, 2);
    const bv1 = boilVariant(f, 1);

    // Tangled ADB cables & boiling "? : ?" port bubbles around the stressed phone
    const swell = f >= 34 ? easeInCubic(prog(f, 34, 12)) : 0;
    const hitSquash =
      f === 7 || f === 15 || f === 23 || f === 31
        ? 0.24
        : f === 8 || f === 16 || f === 24 || f === 32
        ? -0.14
        : 0;

    // Tangled cord loops on paper
    drawBrushStroke(
      ctx,
      makeQuadPts([310, 680], [560, 420 + Math.sin(qf) * 28], [820, 560], 18),
      { width: 9, color: PAL.ink, boilAmp: 3.8, seed: 101, variant: bv2 }
    );
    drawBrushStroke(
      ctx,
      makeQuadPts([1610, 650], [1340, 390 - Math.sin(qf) * 26], [1100, 550], 18),
      { width: 9, color: PAL.ink, boilAmp: 3.8, seed: 102, variant: bv2 }
    );

    // Floating comic port error tags ("ADB : ???", "×999")
    const jiggleX = Math.sin(qf * 1.9) * (6 + swell * 18);
    ctx.save();
    ctx.translate(540 + jiggleX, 345);
    ctx.rotate(-0.14);
    drawCelPolygon(ctx, makeRoundRectPts(-135, -36, 270, 72, 18, 4), {
      fill: PAL.marigold,
      stroke: PAL.ink,
      strokeWidth: 5.5,
      regOffset: [4, 3],
      boilAmp: 2.5,
      seed: 105,
      variant: bv2,
    });
    ctx.fillStyle = PAL.ink;
    ctx.font = "30px SarasaBold";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText(`端口 : ${16380 + ((qf * 7) % 89)} ?`, 0, 2);
    ctx.restore();

    ctx.save();
    ctx.translate(1390 - jiggleX, 360);
    ctx.rotate(0.13);
    drawCelPolygon(ctx, makeRoundRectPts(-125, -36, 250, 72, 18, 4), {
      fill: PAL.tomato,
      stroke: PAL.ink,
      strokeWidth: 5.5,
      regOffset: [4, 3],
      boilAmp: 2.5,
      seed: 106,
      variant: bv2,
    });
    ctx.fillStyle = PAL.cream;
    ctx.font = "30px SarasaBold";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText(`连点 × ${128 + qf * 23}`, 0, 2);
    ctx.restore();

    // Central Overheated Smartphone (on 1s during impact frames, 2s otherwise!)
    if (f < 46) {
      const shake = (f >= 7 ? 7 : 3) + swell * 22;
      const px = W / 2 + Math.sin(f * 2.7) * shake;
      const py = 515 + hitSquash * 42;
      const psx = (1 + hitSquash * 1.15) * (1 + swell * 0.28);
      const psy = (1 - hitSquash * 0.9) * (1 + swell * 0.32);

      drawCelPhone(ctx, px, py, {
        w: 250,
        h: 430,
        rot: Math.sin(qf * 0.9) * 0.06,
        sx: psx,
        sy: psy,
        bodyFill: PAL.cream,
        screenFill: PAL.tomato,
        stressed: true,
        seed: 110,
        variant: bv2,
      });

      // Comic sweat & steam teardrops flying off the stressed phone!
      drawCelSplashBurst(ctx, px, py - 150, 90, 270, ((f % 12) + 1) / 12, 8, PAL.marigold, 120, bv2);
    }

    // *** SMEAR FRAME #1 (f 05..06, ON 1s!): Giant Stylus/Finger Whip-Strike! ***
    if (f === 5 || f === 6) {
      const p = f === 5 ? 0.58 : 0.96;
      drawCurvedSmear(
        ctx,
        [1540, 80],
        [1250, 185],
        [lerp(1220, 980, p), lerp(210, 460, p)],
        42,
        122,
        PAL.tomato,
        140 + f,
        bv1
      );
    } else if (f >= 7 && f < 40) {
      // Cartoon Gloved Hand / Finger rhythmically jabbing the overheated screen!
      const pokeCycle = ((f - 7) % 8) / 8;
      const jab = Math.pow(Math.cos(pokeCycle * Math.PI * 2) * 0.5 + 0.5, 1.8);
      const hx = lerp(1180, 1025, jab);
      const hy = lerp(240, 415, jab);
      ctx.save();
      ctx.translate(hx, hy);
      ctx.rotate(-0.68);
      // Pointing cartoon finger + cuff
      drawCelPolygon(
        ctx,
        [
          [0, -78],
          [22, -70],
          [24, -10],
          [56, 2],
          [54, 65],
          [-38, 65],
          [-38, 0],
          [-16, -12],
          [-16, -70],
        ],
        { fill: PAL.cream, stroke: PAL.ink, strokeWidth: 6.5, regOffset: [4, 3], boilAmp: 2.2, seed: 148, variant: bv2 }
      );
      drawCelPolygon(ctx, makeRoundRectPts(-44, 58, 106, 36, 12, 4), {
        fill: PAL.tomato,
        stroke: PAL.ink,
        strokeWidth: 5.5,
        regOffset: [3, 3],
        seed: 149,
        variant: bv2,
      });
      ctx.restore();
    }

    // Impact Starbursts on Strike Beats (f 7..11, 15..18, 23..26, 31..34)
    for (const hitF of [7, 15, 23, 31]) {
      const hp = prog(f, hitF, 6);
      if (hp > 0 && hp < 1) {
        drawCelStar(ctx, W / 2 + (hitF % 2 === 0 ? 35 : -35), 460, 95 * Math.sin(hp * Math.PI), hitF, PAL.marigold, 150 + hitF, bv1);
        drawCelSplashBurst(ctx, W / 2, 470, 50, 320, hp, 10, PAL.tomato, 160 + hitF, bv1);
      }
    }
  }

  // --------------------------------------------------------------------------
  // ACT II (f 045..113 | 1.88s–4.71s): POP! CURSOR SPIRIT BIRTH, DANCE & SMEAR #2!
  // --------------------------------------------------------------------------
  if (f >= 45 && f < 116) {
    const qf = stepFrame(f, 2);
    const bv2 = boilVariant(f, 2);
    const bv1 = boilVariant(f, 1);

    // Element-Carried Burst Transition (f 45..64): Curling Cel Smoke Puffs!
    const smokeP = prog(qf, 45, 22);
    if (smokeP > 0 && smokeP < 1) {
      const puffOffsets = [
        [-130, -80, 125],
        [140, -70, 130],
        [-165, 95, 115],
        [155, 100, 120],
        [0, -145, 135],
        [0, 125, 110],
      ];
      puffOffsets.forEach(([ox, oy, mr], idx) => {
        drawCelSmokePuff(
          ctx,
          W / 2 + ox * (0.4 + smokeP * 1.15),
          500 + oy * (0.4 + smokeP * 1.15),
          mr,
          clamp(smokeP * 1.15 - idx * 0.04),
          200 + idx * 17,
          bv2
        );
      });
      drawCelSplashBurst(ctx, W / 2, 490, 80, 480, smokeP, 14, PAL.emerald, 240, bv2);
    }

    // Sub-Act II-A (f 46..85): Cursor Spirit Leaps Out, Squash-Lands & Dances on 2s!
    if (f < 86) {
      let cx = W / 2;
      let cy = 480;
      let sx = 1;
      let sy = 1;
      let rot = -0.32;

      if (f < 52) {
        // Rocket up out of smoke with stretch -> land!
        const jp = prog(f, 46, 6);
        cy = lerp(570, 430, Math.sin(jp * Math.PI * 0.75));
        sx = 0.78;
        sy = 1.32;
      } else if (f < 56) {
        // Juicy squash landing!
        cy = 510;
        sx = 1.34;
        sy = 0.68;
      } else if (f < 81) {
        // Joyful rubber-hose bounce dance on 2s!
        const dp = (qf - 56) * 0.42;
        const hop = Math.abs(Math.sin(dp));
        cx = W / 2 + Math.sin(dp * 0.5) * 65;
        cy = 500 - hop * 72;
        sx = 1 + (0.5 - hop) * 0.22;
        sy = 1 + (hop - 0.5) * 0.24;
        rot = -0.28 + Math.sin(dp * 0.5) * 0.22;

        // Playful decorative cel stars popping on beat 60 & 70!
        if (qf >= 58 && qf < 80) {
          drawCelStar(ctx, cx - 230, 360, 46, qf * 0.1, PAL.marigold, 260, bv2);
          drawCelStar(ctx, cx + 245, 340, 38, -qf * 0.12, PAL.tomato, 261, bv2);
        }
      } else {
        // f 81..85: Deep Anticipation Coil-Back before Supersonic Dash!
        const ap = easeInOutCubic(prog(f, 81, 4));
        cx = lerp(W / 2, 410, ap);
        cy = lerp(500, 560, ap);
        sx = lerp(1, 1.42, ap);
        sy = lerp(1, 0.62, ap);
        rot = lerp(-0.28, -0.78, ap);
      }

      drawCursorSpirit(ctx, cx, cy, {
        rot,
        sx,
        sy,
        scale: 1.28,
        stride: qf * 0.55,
        excited: f >= 56 && f < 81,
        variant: f < 56 || f >= 81 ? bv1 : bv2,
        seed: 280,
      });
    }

    // *** SMEAR FRAME #2 (f 86..87, ON 1s!): 280% Supersonic Emerald Whip-Dash! ***
    if (f === 86 || f === 87) {
      // Full-frame comic speed streaks + departure shockwave so frame_086 is poster-dense!
      for (const [sx0, sy0, slen] of [
        [180, 230, 520],
        [680, 185, 640],
        [240, 745, 580],
        [860, 785, 560],
      ]) {
        drawBrushStroke(ctx, [[sx0, sy0], [sx0 + slen, sy0 - 24]], {
          width: 6,
          color: PAL.ink,
          taperStart: 0.92,
          taperEnd: 0.92,
          boilAmp: 2.0,
          seed: 301 + sy0,
          variant: bv1,
        });
      }
      drawCelSplashBurst(ctx, 370, 585, 55, 250, f === 86 ? 0.42 : 0.72, 10, PAL.marigold, 308, bv1);
      drawCurvedSmear(
        ctx,
        [320, 600],
        [890, 290],
        [f === 86 ? 1340 : 1520, f === 86 ? 415 : 455],
        62,
        148,
        PAL.emerald,
        310 + f,
        bv1,
        true
      );
      drawCelSmokePuff(ctx, 355, 595, 130, f === 86 ? 0.34 : 0.58, 315, bv1);
      drawCelStar(ctx, f === 86 ? 880 : 1120, 275, 54, f * 0.4, PAL.marigold, 318, bv1);
      drawCelStar(ctx, 520, 690, 42, -f * 0.3, PAL.tomato, 319, bv1);
    }

    // Sub-Act II-B (f 88..115): High-Speed Bezier Mirror Ribbon + Cel Water Splashes!
    if (f >= 88) {
      const rp = easeOutCubic(prog(qf, 88, 16));

      // Bold Hand-Drawn Speed Lines across cream paper!
      const speedY = [235, 345, 680, 775];
      speedY.forEach((sy, idx) => {
        const sx0 = 180 + ((qf * 65 + idx * 290) % 900);
        const len = 220 + (idx % 2) * 140;
        if (f < 108) {
          drawBrushStroke(
            ctx,
            [[sx0, sy], [sx0 + len, sy]],
            { width: 5.5, color: PAL.ink, taperStart: 0.9, taperEnd: 0.9, boilAmp: 1.8, seed: 330 + idx, variant: bv2 }
          );
        }
      });

      // Sleek Boiling Scrcpy Mirror Device on Left + Sweeping Bezier Touch Ribbon!
      const phonePop = elasticOut(prog(f, 88, 14));
      drawCelPhone(ctx, 620, 485, {
        w: 280,
        h: 470,
        rot: -0.08,
        sx: phonePop,
        sy: phonePop,
        bodyFill: PAL.cream,
        screenFill: PAL.emerald,
        stressed: false,
        seed: 340,
        variant: bv2,
      });

      // Touch origin ring on lower half of screen (leaves the cute happy face 100% clear!)
      drawBrushStroke(ctx, makeEllipsePts(655, 595, 28, 28, 18), {
        width: 5.5,
        color: PAL.marigold,
        closed: true,
        boilAmp: 1.8,
        seed: 348,
        variant: bv2,
      });

      // Flowing Marigold Bezier Touch Trail sweeping from lower screen up & across to Cursor Spirit!
      const ribbonPts = makeQuadPts([655, 595], [1015, 650], [1330, 480], Math.max(4, Math.floor(22 * rp)));
      drawBrushStroke(ctx, ribbonPts, {
        width: 24,
        color: PAL.marigold,
        taperStart: 0.65,
        taperEnd: 0.5,
        boilAmp: 2.8,
        seed: 350,
        variant: bv2,
      });
      drawBrushStroke(ctx, ribbonPts, {
        width: 8,
        color: PAL.ink,
        taperStart: 0.78,
        taperEnd: 0.6,
        boilAmp: 2.4,
        seed: 351,
        variant: bv2,
      });

      // Cel Liquid Splashes bursting cleanly in the open canvas space!
      drawCelSplashBurst(ctx, 1020, 565, 45, 240, prog(qf, 89, 20), 12, PAL.emerald, 360, bv2);
      drawCelSplashBurst(ctx, 1330, 470, 55, 230, prog(qf, 92, 18), 9, PAL.tomato, 365, bv2);

      // Cursor Spirit surfing the front of the Bezier wave on the right!
      drawCursorSpirit(ctx, 1365, 465 + Math.sin(qf * 0.5) * 18, {
        rot: 0.22,
        scale: 1.18,
        stride: qf * 0.6,
        wink: qf >= 98 && qf <= 104,
        excited: true,
        variant: bv2,
        seed: 370,
      });
    }
  }

  // --------------------------------------------------------------------------
  // ACT III-A (f 113..146 | 4.71s–6.08s): OPENCV VISION TARGETS & ORIENTATION FLIP!
  // --------------------------------------------------------------------------
  if (f >= 113 && f < 148) {
    const qf = stepFrame(f, 2);
    const bv2 = boilVariant(f, 2);

    // Whip-pan smoke puff carrying transition at f 113..122
    if (f < 124) {
      drawCelSmokePuff(ctx, 1280, 480, 160, prog(qf, 112, 12), 401, bv2);
      drawCelSmokePuff(ctx, 640, 490, 150, prog(qf, 113, 12), 402, bv2);
    }

    // Three Juggled Emulator / Device Cards — Center One Flips Portrait -> Landscape!
    const flipP = easeOutBack(prog(qf, 122, 12), 1.5);
    const phones = [
      { x: 455, y: 495, rot: -0.14, faceRot: 0, w: 210, h: 350, fill: PAL.emerald, tag: "MuMu · 直读端口" },
      {
        x: 960,
        y: 455,
        rot: -Math.PI * 0.5 * clamp(flipP),
        faceRot: Math.PI * 0.5 * clamp(flipP),
        w: 235,
        h: 395,
        fill: PAL.marigold,
        tag: "横竖屏记忆 ✓",
      },
      { x: 1465, y: 495, rot: 0.14, faceRot: 0, w: 210, h: 350, fill: PAL.tomato, tag: "后台窗口抓图" },
    ];

    phones.forEach((ph, idx) => {
      const pop = easeOutBack(prog(qf, 113 + idx * 3, 9), 1.6);
      if (pop <= 0) return;
      drawCelPhone(ctx, ph.x, ph.y + Math.sin(qf * 0.4 + idx) * 12, {
        w: ph.w,
        h: ph.h,
        rot: ph.rot,
        faceRot: ph.faceRot,
        sx: pop,
        sy: pop,
        bodyFill: PAL.cream,
        screenFill: ph.fill,
        stressed: false,
        seed: 420 + idx * 10,
        variant: bv2,
      });

      // Hand-drawn OpenCV Target Reticle locking onto each screen!
      const lockP = elasticOut(prog(qf, 119 + idx * 4, 10));
      if (lockP > 0) {
        ctx.save();
        ctx.translate(ph.x, ph.y);
        ctx.scale(lockP, lockP);
        drawBrushStroke(ctx, makeEllipsePts(0, 0, 74, 74, 22), {
          width: 6,
          color: PAL.ink,
          closed: true,
          boilAmp: 2.2,
          seed: 450 + idx,
          variant: bv2,
        });
        // Crosshair ticks
        [
          [[-98, 0], [-58, 0]],
          [[58, 0], [98, 0]],
          [[0, -98], [0, -58]],
          [[0, 58], [0, 98]],
        ].forEach((tk, ti) =>
          drawBrushStroke(ctx, tk, { width: 6, color: PAL.ink, seed: 460 + ti, variant: bv2 })
        );
        ctx.restore();

        // Crisp Tag Badge above each juggled device
        ctx.save();
        ctx.translate(ph.x, 225);
        drawCelPolygon(ctx, makeRoundRectPts(-118, -28, 236, 56, 16, 4), {
          fill: PAL.cream,
          stroke: PAL.ink,
          strokeWidth: 5,
          regOffset: [3.5, 3],
          boilAmp: 1.8,
          seed: 470 + idx,
          variant: bv2,
        });
        ctx.fillStyle = PAL.ink;
        ctx.font = "24px SarasaBold";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        ctx.fillText(ph.tag, 0, 2);
        ctx.restore();
      }
    });

    // Cursor Spirit conducting at bottom-center!
    drawCursorSpirit(ctx, 960, 748, {
      rot: Math.sin(qf * 0.5) * 0.25,
      scale: 0.92,
      stride: qf * 0.6,
      excited: true,
      variant: bv2,
      seed: 490,
    });
  }

  // --------------------------------------------------------------------------
  // ACT III-B HERO CLIMAX (f 146..196 | 6.08s–8.17s = 61%–81%):
  // MCP AI LIGHTNING CONDUCTOR -> SMEAR #3 -> GIANT CEL FX FIREWORK BURST!
  // --------------------------------------------------------------------------
  if (f >= 146 && f < 198) {
    const qf = stepFrame(f, 2);
    const bv2 = boilVariant(f, 2);
    const bv1 = boilVariant(f, 1);

    if (f < 174) {
      // Central Glowing MCP AI Conductor Halo + Cursor Spirit + Orbiting Android Trio!
      const hubPop = easeOutBack(prog(f, 146, 8), 1.8);

      // Radiant scalloped marigold energy sun behind MCP Hub (raised to y=418 for clean separation!)
      const rays = 12;
      const sunPts = [];
      for (let i = 0; i < rays * 2; i++) {
        const a = (i * Math.PI) / rays + qf * 0.06;
        const r = (i % 2 === 0 ? 172 : 124) * hubPop;
        sunPts.push([W / 2 + Math.cos(a) * r, 418 + Math.sin(a) * r]);
      }
      drawCelPolygon(ctx, sunPts, {
        fill: PAL.marigold,
        stroke: PAL.ink,
        strokeWidth: 6.5,
        regOffset: [6, 5],
        boilAmp: 2.8,
        seed: 510,
        variant: bv2,
      });

      // "MCP · 33 TOOLS" Badge at top center
      ctx.save();
      ctx.translate(W / 2, 168);
      ctx.scale(hubPop, hubPop);
      drawCelPolygon(ctx, makeRoundRectPts(-195, -34, 390, 68, 22, 5), {
        fill: PAL.tomato,
        stroke: PAL.ink,
        strokeWidth: 6,
        regOffset: [4.5, 3.5],
        boilAmp: 2.0,
        seed: 515,
        variant: bv2,
      });
      ctx.fillStyle = PAL.cream;
      ctx.font = "31px SarasaBold";
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText("⚡ AI + MCP · 33 项工具", 0, 2);
      ctx.restore();

      // Hand-Drawn Cel Lightning Bolts arcing to 3 Dancing Android Buddies (zero overlap!)
      const targets = [
        { x: 370, y: 495, c: PAL.emerald, rot: -0.18, sc: 1.15 },
        { x: 1550, y: 495, c: PAL.emerald, rot: 0.18, sc: 1.15 },
        { x: 960, y: 785, c: PAL.tomato, rot: 0, sc: 1.05 },
      ];

      targets.forEach((tg, i) => {
        if (f >= 150 + i * 2) {
          drawCelLightning(
            ctx,
            W / 2,
            418,
            tg.x,
            tg.y - 35,
            26,
            i % 2 === 0 ? PAL.emerald : PAL.tomato,
            520 + i * 13,
            bv1
          );
        }
        const bPop = elasticOut(prog(f, 148 + i * 3, 12));
        const hop = Math.abs(Math.sin(qf * 0.55 + i)) * 20;
        drawAndroidBuddy(ctx, tg.x, tg.y - hop, {
          rot: tg.rot + Math.sin(qf * 0.4 + i) * 0.12,
          scale: tg.sc * bPop,
          fill: tg.c,
          antennaWiggle: Math.sin(qf * 0.9) * 14,
          variant: bv2,
          seed: 540 + i * 10,
        });
      });

      // *** SMEAR #3 (f 169..172, ON 1s!): Trio Spiraling Inward Toward Climax Detonation! ***
      if (f >= 169) {
        const sp = prog(f, 169, 4);
        drawCurvedSmear(ctx, [370, 495], [640, 290], [lerp(680, 960, sp), 430], 36, 96, PAL.emerald, 560 + f, bv1);
        drawCurvedSmear(ctx, [1550, 495], [1280, 610], [lerp(1240, 960, sp), 430], 36, 96, PAL.tomato, 570 + f, bv1);
      }

      // Hero Cursor Spirit in the eye of the electric storm!
      drawCursorSpirit(ctx, W / 2, 418, {
        rot: -0.25 + Math.sin(qf * 0.6) * 0.18,
        scale: 1.12 * hubPop,
        stride: qf * 0.7,
        excited: true,
        variant: bv2,
        seed: 580,
      });
    } else {
      // *** f 174..197 (7.25s–8.21s): UNMISTAKABLE FULL-SCREEN CEL FX FIREWORK & IMPLOSION! ***
      const burstP = prog(f, 174, 18); // 0 -> 1 over 18 frames
      const implodeP = f >= 189 ? easeInOutCubic(prog(f, 189, 9)) : 0;

      // Radial Speed Lines around screen perimeter!
      const nLines = 18;
      for (let i = 0; i < nLines; i++) {
        const a = (i / nLines) * Math.PI * 2 + 0.1;
        const rInner = implodeP > 0 ? lerp(320, 90, implodeP) : lerp(120, 420, easeOutCubic(burstP));
        const rOuter = implodeP > 0 ? lerp(760, 210, implodeP) : lerp(340, 880, easeOutQuint(burstP));
        drawBrushStroke(
          ctx,
          [
            [W / 2 + Math.cos(a) * rInner, 470 + Math.sin(a) * rInner * 0.65],
            [W / 2 + Math.cos(a) * rOuter, 470 + Math.sin(a) * rOuter * 0.65],
          ],
          {
            width: 6.5,
            color: i % 3 === 0 ? PAL.emerald : i % 3 === 1 ? PAL.tomato : PAL.ink,
            taperStart: 0.85,
            taperEnd: 0.85,
            boilAmp: 2.2,
            seed: 600 + i,
            variant: bv1,
          }
        );
      }

      // Central jagged cel flash starburst behind the 8 curling C-crescent smoke puffs!
      if (burstP < 0.72) {
        const flashR = lerp(110, 290, easeOutCubic(burstP)) * (1 - implodeP * 0.6);
        const spikes = 14;
        const flashPts = [];
        for (let i = 0; i < spikes * 2; i++) {
          const fa = (i * Math.PI) / spikes + burstP * 0.4;
          const fr = i % 2 === 0 ? flashR : flashR * 0.52;
          flashPts.push([W / 2 + Math.cos(fa) * fr, 470 + Math.sin(fa) * fr * 0.68]);
        }
        drawCelPolygon(ctx, flashPts, {
          fill: PAL.marigold,
          stroke: PAL.ink,
          strokeWidth: 5.5,
          regOffset: [5, 4],
          boilAmp: 2.4,
          seed: 622,
          variant: bv1,
        });
      }

      // 8 Huge Hand-Drawn Curling Smoke Puffs!
      const ringR = implodeP > 0 ? lerp(430, 80, implodeP) : 430 * easeOutCubic(burstP);
      for (let k = 0; k < 8; k++) {
        const ka = (k / 8) * Math.PI * 2 + burstP * 0.35;
        drawCelSmokePuff(
          ctx,
          W / 2 + Math.cos(ka) * ringR,
          470 + Math.sin(ka) * ringR * 0.62,
          155,
          clamp(burstP * 1.05),
          630 + k * 13,
          bv2,
          k % 2 === 0 ? PAL.cream : PAL.emeraldLight
        );
      }

      // Dual-color Liquid Cel Splashes + Star Bursts!
      drawCelSplashBurst(ctx, W / 2, 470, 60, 680, burstP, 16, PAL.emerald, 660, bv2);
      drawCelSplashBurst(ctx, W / 2, 470, 40, 560, clamp(burstP * 1.1 - 0.08), 14, PAL.tomato, 680, bv2);

      for (let s = 0; s < 6; s++) {
        const sa = (s / 6) * Math.PI * 2 + 0.4;
        const sr = implodeP > 0 ? lerp(360, 40, implodeP) : 360 * easeOutCubic(burstP);
        drawCelStar(
          ctx,
          W / 2 + Math.cos(sa) * sr,
          470 + Math.sin(sa) * sr * 0.62,
          58 * Math.sin(clamp(burstP) * Math.PI),
          burstP * 2 + s,
          s % 2 === 0 ? PAL.marigold : PAL.tomato,
          700 + s,
          bv2
        );
      }

      // Implosion Smear Ribbons snapping into the ClickZen title at f 190..197!
      if (f >= 190) {
        drawCurvedSmear(ctx, [260, 210], [610, 320], [820, 435], 24, 72, PAL.emerald, 720 + f, bv1);
        drawCurvedSmear(ctx, [1660, 720], [1310, 590], [1100, 445], 24, 72, PAL.marigold, 730 + f, bv1);
      }
    }
  }

  // --------------------------------------------------------------------------
  // ACT IV (f 195..239 | 8.13s–10.00s): COMPOSED HERO LOCKUP ON 3s ("一拍三")!
  // --------------------------------------------------------------------------
  if (f >= 195) {
    // 3-frame boil hold ("一拍三"!) after initial elastic snap-in (f 195..204)
    const bvHold = f < 204 ? boilVariant(f, 1) : boilVariant(f, 3);
    const snapScale = f < 205 ? easeOutBack(prog(f, 195, 9), 1.9) : 1.0;

    // Residual evaporating smoke wisps around edges during f 195..216
    if (f < 218) {
      const wp = prog(stepFrame(f, 2), 194, 24);
      drawCelSmokePuff(ctx, 285, 355, 95, 0.55 + wp * 0.44, 801, bvHold);
      drawCelSmokePuff(ctx, 1645, 365, 100, 0.52 + wp * 0.47, 802, bvHold);
    }

    // Living micro-motion during the composed hold: cute Android blink & antenna wiggle!
    const blinkN = f >= 216 && f <= 219;
    const antennaWig = Math.sin((stepFrame(f, 3) - 195) * 0.45) * 9;

    drawBoilingHeroLockup(ctx, W / 2, 415, 0.96 * snapScale, bvHold, blinkN, antennaWig);

    // Clean Editorial Subtitle & Open-Source Tagline (with generous >8% safe margins!)
    const textPop = easeOutCubic(prog(f, 200, 8));
    if (textPop > 0) {
      ctx.save();
      ctx.globalAlpha = textPop;
      const bShift = ((bvHold % 2) - 0.5) * 1.3;
      ctx.fillStyle = PAL.ink;
      ctx.font = "40px SarasaBold";
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(
        "毫秒投屏  ·  OpenCV 视觉自动化  ·  内置 33 工具 MCP 服务",
        W / 2 + bShift,
        638 + (1 - textPop) * 18
      );

      // Hand-drawn Bilibili 三连 + GitHub Open Source Badges at bottom!
      const pills = [
        { x: 560, label: "赞 · 点赞支持", fill: PAL.tomato, tc: PAL.cream },
        { x: 960, label: "★ github.com/Exmeaning/ClickZen", fill: PAL.cream, tc: PAL.ink },
        { x: 1385, label: "币 · 开源免费", fill: PAL.marigold, tc: PAL.ink },
      ];
      pills.forEach((pl, idx) => {
        const pp = easeOutBack(prog(f, 203 + idx * 2, 8), 1.6);
        if (pp <= 0) return;
        const pw = idx === 1 ? 520 : 250;
        ctx.save();
        ctx.translate(pl.x, 758);
        ctx.scale(pp, pp);
        drawCelPolygon(ctx, makeRoundRectPts(-pw / 2, -32, pw, 64, 20, 4), {
          fill: pl.fill,
          stroke: PAL.ink,
          strokeWidth: 5,
          regOffset: [4, 3],
          boilAmp: 1.7,
          seed: 850 + idx,
          variant: bvHold,
        });
        ctx.fillStyle = pl.tc;
        ctx.font = "25px SarasaBold";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        ctx.fillText(pl.label, 0, 2);
        ctx.restore();
      });

      // Two living cel stars gently boiling in upper negative space
      drawCelStar(ctx, 295, 250, 32, 0.2, PAL.marigold, 880, bvHold);
      drawCelStar(ctx, 1635, 520, 28, -0.25, PAL.emerald, 881, bvHold);
      ctx.restore();
    }
  }

  // Synced Editorial Voiceover Caption Banner (Acts I–III)
  drawEditorialCaption(ctx, f);

  // --------------------------------------------------------------------------
  // FINAL FINISHING PASS: 12% PAPER GRAIN MULTIPLY + SUBTLE INK FRAME VIGNETTE
  // --------------------------------------------------------------------------
  ctx.save();
  ctx.globalCompositeOperation = "multiply";
  ctx.drawImage(paperLayers.grainCanvases[boilVariant(f, 3, 2)], 0, 0);
  ctx.restore();
}

// ============================================================================
// CLI ENTRYPOINT: `stills` (Inspect 8 Poster Keyframes) OR `render` (240f MP4)
// ============================================================================
async function main() {
  const mode = process.argv[2] || "stills";
  const canvas = createCanvas(W, H);
  const ctx = canvas.getContext("2d");
  const paperLayers = createPaperLayers();

  if (mode === "stills") {
    const outStillsDir = path.join(BUILD_DIR, "cel_stills");
    fs.mkdirSync(outStillsDir, { recursive: true });
    const keyFrames = [6, 24, 52, 86, 102, 130, 160, 180, 224];
    for (const kf of keyFrames) {
      renderFilmFrame(ctx, kf, paperLayers);
      const p = path.join(outStillsDir, `frame_${String(kf).padStart(3, "0")}.png`);
      fs.writeFileSync(p, canvas.toBuffer("image/png"));
      console.log(`Rendered still frame ${kf} (${(kf / FPS).toFixed(2)}s) -> ${p}`);
    }
    // Export wide-awake frame 224 as our high-art Cel Bilibili Cover!
    renderFilmFrame(ctx, 224, paperLayers);
    fs.writeFileSync("promo/ClickZen-Cel-Cover.png", canvas.toBuffer("image/png"));
    return;
  }

  // Exact 240-frame Full HD 24fps Render -> video.mp4!
  const ffmpegBin = process.env.FFMPEG_BIN || "ffmpeg";
  const audioWav = process.argv[3] || path.join(BUILD_DIR, "cel_master_48k.wav");
  const outMp4 = process.argv[4] || "video.mp4";

  console.log(`Rendering EXACT ${TOTAL_FRAMES} frames (${W}x${H} @ ${FPS}fps = 10.000s) -> ${outMp4}`);

  const ff = spawn(
    ffmpegBin,
    [
      "-y",
      "-f", "rawvideo",
      "-pix_fmt", "rgba",
      "-s", `${W}x${H}`,
      "-r", String(FPS),
      "-i", "pipe:0",
      "-i", audioWav,
      "-frames:v", String(TOTAL_FRAMES),
      "-c:v", "libx264",
      "-preset", "slow",
      "-crf", "16",
      "-pix_fmt", "yuv420p",
      "-c:a", "aac",
      "-ar", "48000",
      "-b:a", "256k",
      "-af", "apad=whole_dur=10.0,atrim=0:10.0",
      "-movflags", "+faststart",
      outMp4,
    ],
    { stdio: ["pipe", "inherit", "inherit"] }
  );

  const t0 = Date.now();
  for (let f = 0; f < TOTAL_FRAMES; f++) {
    renderFilmFrame(ctx, f, paperLayers);
    const ok = ff.stdin.write(canvas.data());
    if (!ok) await new Promise((r) => ff.stdin.once("drain", r));
  }
  ff.stdin.end();
  await new Promise((resolve, reject) => {
    ff.on("close", (c) => (c === 0 ? resolve() : reject(new Error(`FFmpeg exited ${c}`))));
  });
  console.log(`Rendered ${outMp4} in ${((Date.now() - t0) / 1000).toFixed(2)}s`);
}

export { PAL, createPaperLayers, drawCelPolygon, makeRoundRectPts, drawBrushStroke, makeQuadPts, drawCelStar, drawCelPhone, drawCursorSpirit, drawAndroidBuddy, drawBoilingHeroLockup, drawCurvedSmear, drawCelLightning, drawCelSmokePuff, renderFilmFrame, boilVariant, stepFrame };
if (process.argv[1] && path.resolve(process.argv[1]) === new URL(import.meta.url).pathname) {
  main().catch((e) => { console.error(e); process.exit(1); });
}
