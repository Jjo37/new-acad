// trace-image.js — 位图 → CAD 矢量路径（纯 Node，零 npm 依赖）
//
// 支持输入: PNG（8bit, 灰/RGB/调色板/RGBA, 非隔行） / BMP(24/32bpp BI_RGB)
//         其他格式由插件 exportImageGray 转灰度后传入 {gray:{w,h,data}}
//
// 模式 mode:
//   outline   描边（闭合，保笔画粗细）
//   centerline 中轴单线（骨架 → 端点/交点之间走链，开放折线）
//   arc        centerline + 逐段圆弧拟合（AutoCAD bulge）
//   posterize  颜色量化后各色块轮廓（闭合，"把图搬进 CAD"用）
//
// 参数: simplify(RDP容差px) arcThreshold(小于此不弧化) minLength(丢短线px)
//       bridge(端点桥接距离px,0=关) scaleTo(缩放到该宽度,0=原样) offsetX/Y
'use strict';
const fs = require('fs');
const zlib = require('zlib');

// ---------------------------------------------------------------- 解码
function decodePNG(buf) {
  if (buf.readUInt32BE(0) !== 0x89504e47) throw new Error('not PNG');
  let p = 8, w = 0, h = 0, bd = 8, ct = 6, inter = 0, pal = null, trns = null;
  const idat = [];
  while (p < buf.length) {
    const len = buf.readUInt32BE(p);
    const type = buf.toString('ascii', p + 4, p + 8);
    const body = buf.slice(p + 8, p + 8 + len);
    if (type === 'IHDR') {
      w = body.readUInt32BE(0); h = body.readUInt32BE(4); bd = body[8]; ct = body[9]; inter = body[12];
    } else if (type === 'PLTE') pal = Buffer.from(body);
    else if (type === 'tRNS') trns = Buffer.from(body);
    else if (type === 'IDAT') idat.push(body);
    else if (type === 'IEND') break;
    p += 12 + len;
  }
  if (inter) throw new Error('interlaced PNG not supported');
  if (bd !== 8 && bd !== 16) throw new Error('bit depth ' + bd + ' not supported');
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const chans = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 }[ct];
  if (!chans) throw new Error('color type ' + ct + ' not supported');
  const bpp = chans * (bd / 8);
  const stride = w * bpp;
  const out = Buffer.alloc(h * stride);
  let q = 0;
  for (let y = 0; y < h; y++) {
    const f = raw[q++];
    const line = raw.slice(q, q + stride); q += stride;
    const row = out.slice(y * stride, (y + 1) * stride);
    const prev = y > 0 ? out.slice((y - 1) * stride, y * stride) : null;
    for (let i = 0; i < stride; i++) {
      const a = i >= bpp ? row[i - bpp] : 0;
      const b = prev ? prev[i] : 0;
      const c = prev && i >= bpp ? prev[i - bpp] : 0;
      let v = line[i];
      if (f === 1) v = (v + a) & 255;
      else if (f === 2) v = (v + b) & 255;
      else if (f === 3) v = (v + ((a + b) >> 1)) & 255;
      else if (f === 4) {
        const pp = a + b - c, pa = Math.abs(pp - a), pb = Math.abs(pp - b), pc = Math.abs(pp - c);
        v = (v + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c)) & 255;
      }
      row[i] = v;
    }
  }
  // -> RGB8
  const rgb = new Uint8Array(w * h * 3);
  if (ct === 3) {
    for (let i = 0; i < w * h; i++) {
      const idx = out[i];
      rgb[i * 3] = pal[idx * 3]; rgb[i * 3 + 1] = pal[idx * 3 + 1]; rgb[i * 3 + 2] = pal[idx * 3 + 2];
    }
  } else {
    const step = bd === 16 ? 2 : 1;
    for (let i = 0; i < w * h; i++) {
      const o = i * bpp;
      if (ct === 0 || ct === 4) { const g = out[o]; rgb[i * 3] = g; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = g; }
      else { rgb[i * 3] = out[o]; rgb[i * 3 + 1] = out[o + step]; rgb[i * 3 + 2] = out[o + 2 * step]; }
    }
  }
  return { w, h, rgb };
}

function decodeBMP(buf) {
  if (buf.toString('ascii', 0, 2) !== 'BM') throw new Error('not BMP');
  const off = buf.readUInt32LE(10);
  const hdr = buf.readUInt32LE(14);
  const w = buf.readInt32LE(18), hs = buf.readInt32LE(22);
  const bpp = buf.readUInt16LE(28), comp = buf.readUInt32LE(30);
  if (hdr < 40 || comp !== 0 || (bpp !== 24 && bpp !== 32)) throw new Error('BMP variant not supported');
  const h = Math.abs(hs), bottomUp = hs > 0, stride = Math.floor((bpp * w + 31) / 32) * 4;
  const rgb = new Uint8Array(w * h * 3);
  for (let y = 0; y < h; y++) {
    const src = off + (bottomUp ? h - 1 - y : y) * stride;
    for (let x = 0; x < w; x++) {
      const o = src + x * (bpp / 8), d = (y * w + x) * 3;
      rgb[d] = buf[o + 2]; rgb[d + 1] = buf[o + 1]; rgb[d + 2] = buf[o];
    }
  }
  return { w, h, rgb };
}

function loadImage(file) {
  const buf = fs.readFileSync(file);
  if (buf.length > 8 && buf.readUInt32BE(0) === 0x89504e47) return decodePNG(buf);
  if (buf.length > 2 && buf.toString('ascii', 0, 2) === 'BM') return decodeBMP(buf);
  throw new Error('unsupported raster (need PNG/BMP, or pass gray via exportImageGray)');
}

// ---------------------------------------------------------------- 基础图像
function toGray(img) {
  const n = img.w * img.h, g = new Uint8Array(n);
  for (let i = 0; i < n; i++) {
    g[i] = (img.rgb[i * 3] * 299 + img.rgb[i * 3 + 1] * 587 + img.rgb[i * 3 + 2] * 114) / 1000 | 0;
  }
  return g;
}

function blur(g, w, h, sigma) {
  const r = Math.max(1, Math.ceil(sigma * 3));
  const k = new Float64Array(r * 2 + 1);
  let s = 0;
  for (let i = -r; i <= r; i++) { k[i + r] = Math.exp(-(i * i) / (2 * sigma * sigma)); s += k[i + r]; }
  for (let i = 0; i < k.length; i++) k[i] /= s;
  const tmp = new Float64Array(w * h), out = new Float64Array(w * h);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    let a = 0;
    for (let i = -r; i <= r; i++) { const xx = Math.min(w - 1, Math.max(0, x + i)); a += g[y * w + xx] * k[i + r]; }
    tmp[y * w + x] = a;
  }
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    let a = 0;
    for (let i = -r; i <= r; i++) { const yy = Math.min(h - 1, Math.max(0, y + i)); a += tmp[yy * w + x] * k[i + r]; }
    out[y * w + x] = a;
  }
  return out;
}

function otsu(g) {
  const hist = new Float64Array(256);
  for (let i = 0; i < g.length; i++) hist[g[i]]++;
  const total = g.length;
  let sum = 0; for (let i = 0; i < 256; i++) sum += i * hist[i];
  let sumB = 0, wB = 0, best = 0, thr = 0;
  for (let t = 0; t < 256; t++) {
    wB += hist[t]; if (!wB) continue;
    const wF = total - wB; if (!wF) break;
    sumB += t * hist[t];
    const mB = sumB / wB, mF = (sum - sumB) / wF;
    const between = wB * wF * (mB - mF) * (mB - mF);
    if (between > best) { best = between; thr = t; }
  }
  return thr;
}

function labelComponents(mask, w, h) {
  const lab = new Int32Array(w * h).fill(-1);
  const sizes = [];
  const stack = [];
  for (let i = 0; i < w * h; i++) {
    if (!mask[i] || lab[i] >= 0) continue;
    const id = sizes.length; let n = 0;
    stack.push(i); lab[i] = id;
    while (stack.length) {
      const p = stack.pop(); n++;
      const x = p % w, y = (p / w) | 0;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        const nx = x + dx, ny = y + dy;
        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
        const q = ny * w + nx;
        if (mask[q] && lab[q] < 0) { lab[q] = id; stack.push(q); }
      }
    }
    sizes.push(n);
  }
  return { lab, sizes };
}

function morph(mask, w, h, op, r) {
  const out = new Uint8Array(w * h);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    let hit = op === 'dilate' ? 0 : 1;
    for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
      const nx = x + dx, ny = y + dy;
      const v = (nx < 0 || ny < 0 || nx >= w || ny >= h) ? 0 : mask[ny * w + nx];
      if (op === 'dilate') { if (v) { hit = 1; dy = r + 1; break; } }
      else if (!v) { hit = 0; dy = r + 1; break; }
    }
    out[y * w + x] = hit;
  }
  return out;
}

// Zhang-Suen 细化
function thin(mask, w, h) {
  const I = Uint8Array.from(mask);
  const idx = (x, y) => y * w + x;
  const at = (x, y) => (x < 0 || y < 0 || x >= w || y >= h) ? 0 : I[idx(x, y)];
  for (let iter = 0; iter < 100; iter++) {
    let changed = 0;
    for (let step = 0; step < 2; step++) {
      const del = [];
      for (let y = 1; y < h - 1; y++) for (let x = 1; x < w - 1; x++) {
        if (!I[idx(x, y)]) continue;
        const p2 = at(x, y - 1), p3 = at(x + 1, y - 1), p4 = at(x + 1, y), p5 = at(x + 1, y + 1);
        const p6 = at(x, y + 1), p7 = at(x - 1, y + 1), p8 = at(x - 1, y), p9 = at(x - 1, y - 1);
        const B = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
        if (B < 2 || B > 6) continue;
        const seq = [p2, p3, p4, p5, p6, p7, p8, p9, p2];
        let A = 0; for (let k = 0; k < 8; k++) if (seq[k] === 0 && seq[k + 1] === 1) A++;
        if (A !== 1) continue;
        if (step === 0) { if (p2 * p4 * p6 || p4 * p6 * p8) continue; }
        else { if (p2 * p4 * p8 || p2 * p6 * p8) continue; }
        del.push(idx(x, y));
      }
      for (const d of del) { I[d] = 0; changed++; }
    }
    if (!changed) break;
  }
  return I;
}

// 剪除毛刺：从端点走链，长度 <= maxLen 的整条删掉（细化在 2~3px 宽笔画上会生成梯状短枝）
function pruneSpurs(sk, w, h, maxLen) {
  const at = (x, y) => (x < 0 || y < 0 || x >= w || y >= h) ? 0 : sk[y * w + x];
  const nbrs = (x, y) => {
    const r = [];
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
      if ((dx || dy) && at(x + dx, y + dy)) r.push([x + dx, y + dy]);
    }
    return r;
  };
  for (let pass = 0; pass < 8; pass++) {
    let removed = 0;
    const eps = [];
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (sk[y * w + x] && nbrs(x, y).length === 1) eps.push([x, y]);
    for (const [ex, ey] of eps) {
      const nb0 = nbrs(ex, ey);
      if (!nb0.length) continue;
      const path = [[ex, ey]];
      let px = ex, py = ey, cx = nb0[0][0], cy = nb0[0][1];
      for (let g = 0; g <= maxLen + 1; g++) {
        if (nbrs(cx, cy).length !== 2) break;
        path.push([cx, cy]);
        const nx = nbrs(cx, cy).filter(([a, b]) => !(a === px && b === py));
        if (!nx.length) break;
        px = cx; py = cy; cx = nx[0][0]; cy = nx[0][1];
      }
      if (path.length <= maxLen) {
        for (const [x, y] of path) { sk[y * w + x] = 0; removed++; }
      }
    }
    if (!removed) break;
  }
  return sk;
}

// 骨架 → 链（端点/交点之间走链，得到真正的开放折线）
function skeletonChains(sk, w, h) {
  const on = (x, y) => (x < 0 || y < 0 || x >= w || y >= h) ? 0 : sk[y * w + x];
  const nb = (x, y) => {
    const r = [];
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
      if (!dx && !dy) continue;
      if (on(x + dx, y + dy)) r.push([x + dx, y + dy]);
    }
    return r;
  };
  const nodes = new Set(), deg = new Map();
  const pts = [];
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    if (!sk[y * w + x]) continue;
    const d = nb(x, y).length;
    deg.set(y * w + x, d);
    if (d !== 2) { nodes.add(y * w + x); pts.push([x, y]); }
  }
  const chains = [];
  const usedEdge = new Set();
  const key = (a, b) => a < b ? a + ':' + b : b + ':' + a;
  const walk = (startX, startY, firstX, firstY) => {
    const chain = [[startX, startY]];
    let cx = firstX, cy = firstY, px = startX, py = startY;
    for (let guard = 0; guard < 100000; guard++) {
      chain.push([cx, cy]);
      const d = deg.get(cy * w + cx) || 0;
      if (d !== 2) break;
      const cands = nb(cx, cy).filter(([x, y]) => !(x === px && y === py));
      if (!cands.length) break;
      const [nx, ny] = cands[0];
      px = cx; py = cy; cx = nx; cy = ny;
    }
    return chain;
  };
  for (const n of nodes) {
    const x = n % w, y = (n / w) | 0;
    for (const [nx, ny] of nb(x, y)) {
      const k = key(n, ny * w + nx);
      if (usedEdge.has(k)) continue;
      usedEdge.add(k);
      const chain = walk(x, y, nx, ny);
      if (chain.length > 1) {
        usedEdge.add(key(chain[chain.length - 1][1] * w + chain[chain.length - 1][0], chain[chain.length - 2][1] * w + chain[chain.length - 2][0]));
        chains.push(chain);
      }
    }
  }
  // 纯环（没有端点/交点）
  for (let i = 0; i < w * h; i++) {
    if (!sk[i] || (deg.get(i) || 0) !== 2) continue;
    const x = i % w, y = (i / w) | 0;
    const nbs = nb(x, y);
    const k = key(i, nbs[0][1] * w + nbs[0][0]);
    if (usedEdge.has(k)) continue;
    const chain = walk(x, y, nbs[0][0], nbs[0][1]);
    usedEdge.add(key(i, nbs[0][1] * w + nbs[0][0]));
    chains.push(chain);
  }
  return chains;
}

// 掩膜轮廓（Moore 边界跟踪，闭合）
function maskContours(mask, w, h) {
  const seen = new Uint8Array(w * h);
  const out = [];
  const N8 = [[1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1], [0, -1], [1, -1]];
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const i = y * w + x;
    if (!mask[i] || seen[i]) continue;
    // 只在起点（左侧不是前景）开始
    if (x > 0 && mask[i - 1]) continue;
    const chain = [[x, y]];
    seen[i] = 1;
    let cx = x, cy = y, dir = 6;
    for (let guard = 0; guard < 400000; guard++) {
      let found = false;
      for (let k = 0; k < 8; k++) {
        const d = (dir + 6 + k) % 8;
        const nx = cx + N8[d][0], ny = cy + N8[d][1];
        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
        if (mask[ny * w + nx]) {
          cx = nx; cy = ny; dir = d; chain.push([cx, cy]); seen[cy * w + cx] = 1; found = true; break;
        }
      }
      if (!found) break;
      if (cx === x && cy === y && chain.length > 3) break;
    }
    if (chain.length > 3) out.push(chain);
  }
  return out;
}

// ---------------------------------------------------------------- 工作分辨率（2026-09-22）
// 大图先降采样：1920px 上跑 sigma=0.8 的 DoG 只会抓到抗锯齿噪声；f×f 平均还能顺便磨平锯齿
function downscaleImage(img, f) {
  const w = Math.max(1, Math.floor(img.w / f)), h = Math.max(1, Math.floor(img.h / f));
  const o = { w: w, h: h, rgb: null, gray: null };
  if (img.rgb) {
    const rgb = new Uint8Array(w * h * 3);
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
      let r = 0, g2 = 0, b = 0, n = 0;
      for (let dy = 0; dy < f; dy++) for (let dx = 0; dx < f; dx++) {
        const sx = x * f + dx, sy = y * f + dy;
        if (sx >= img.w || sy >= img.h) continue;
        const i = (sy * img.w + sx) * 3;
        r += img.rgb[i]; g2 += img.rgb[i + 1]; b += img.rgb[i + 2]; n++;
      }
      const j = (y * w + x) * 3;
      rgb[j] = Math.round(r / n); rgb[j + 1] = Math.round(g2 / n); rgb[j + 2] = Math.round(b / n);
    }
    o.rgb = rgb;
  } else {
    const src = img.gray;
    const gray = new Uint8Array(w * h);
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
      let sSum = 0, n = 0;
      for (let dy = 0; dy < f; dy++) for (let dx = 0; dx < f; dx++) {
        const sx = x * f + dx, sy = y * f + dy;
        if (sx >= img.w || sy >= img.h) continue;
        sSum += src[sy * img.w + sx]; n++;
      }
      gray[y * w + x] = Math.round(sSum / n);
    }
    o.gray = gray;
  }
  return o;
}

// ---------------------------------------------------------------- 扁平/纯色插画（2026-09-22）
// 颜色分块：每个色块取轮廓（含孔洞）。扁平插画/矢量风壁纸/logo 走这条路，别用 DoG+骨架。
function regionChains(img, w, h, o) {
  const L = Math.max(2, Math.min(8, o.flatLevels || 4));
  const step = 256 / L;
  const cls = new Int16Array(w * h);
  const keys = new Map();
  const acc = new Map();
  for (let i = 0; i < w * h; i++) {
    let r, g2, b;
    if (img.rgb) { r = img.rgb[i * 3]; g2 = img.rgb[i * 3 + 1]; b = img.rgb[i * 3 + 2]; }
    else { r = g2 = b = img.gray[i]; }
    const R = Math.min(L - 1, Math.floor(r / step)), G = Math.min(L - 1, Math.floor(g2 / step)), B = Math.min(L - 1, Math.floor(b / step));
    const k = (R * L + G) * L + B;
    cls[i] = k;
    keys.set(k, (keys.get(k) || 0) + 1);
    let a = acc.get(k);
    if (!a) { a = [0, 0, 0, 0]; acc.set(k, a); }
    a[0] += r; a[1] += g2; a[2] += b; a[3]++;
  }
  const minArea = Math.max(8, ((o.flatMinArea == null ? 0.05 : o.flatMinArea) / 100) * w * h);
  const all = [...keys.entries()].sort((a, b) => b[1] - a[1]);
  // 主色 = 占比 >= flatMinColor%（默认 1%）；其余（抗锯齿过渡色）并入最近主色
  const minColorShare = (o.flatMinColor == null ? 1 : o.flatMinColor) / 100;
  let kept = all.filter((e) => e[1] >= minColorShare * w * h).map((e) => e[0]);
  if (!kept.length) kept = [all[0][0]];
  const mean = (k) => { const a = acc.get(k); return [a[0] / a[3], a[1] / a[3], a[2] / a[3]]; };
  const remap = new Map();
  for (const e of all) {
    const k = e[0];
    if (kept.indexOf(k) >= 0) { remap.set(k, k); continue; }
    const c1 = mean(k);
    let best = kept[0], bd = Infinity;
    for (const kk of kept) {
      const c2 = mean(kk);
      const d = (c1[0] - c2[0]) * (c1[0] - c2[0]) + (c1[1] - c2[1]) * (c1[1] - c2[1]) + (c1[2] - c2[2]) * (c1[2] - c2[2]);
      if (d < bd) { bd = d; best = kk; }
    }
    remap.set(k, best);
  }
  const merged = new Map();
  for (let i = 0; i < w * h; i++) { const k2 = remap.get(cls[i]); cls[i] = k2; merged.set(k2, (merged.get(k2) || 0) + 1); }
  const list = [...merged.entries()].sort((a, b) => b[1] - a[1]);
  const bgKey = (list.length && list[0][1] > 0.6 * w * h) ? list[0][0] : -1;
  const chains = [];
  for (const e of list) {
    const k = e[0], n = e[1];
    if (k === bgKey && !o.keepBackground) continue;
    const m = new Uint8Array(w * h);
    for (let i = 0; i < w * h; i++) if (cls[i] === k) m[i] = 1;
    const lc = labelComponents(m, w, h);
    const ok = new Set();
    for (let i = 0; i < w * h; i++) if (m[i] && lc.sizes[lc.lab[i]] >= minArea) ok.add(lc.lab[i]);
    const m2 = new Uint8Array(w * h);
    for (let i = 0; i < w * h; i++) if (m[i] && ok.has(lc.lab[i])) m2[i] = 1;
    const a = acc.get(k) || [0, 0, 0, 1];
    const color = [Math.round(a[0] / a[3]), Math.round(a[1] / a[3]), Math.round(a[2] / a[3])];
    for (const c of maskContours(m2, w, h)) {
      if (c.length > 8) { c.__color = color; c.__areaRatio = n / (w * h); chains.push(c); }
    }
  }
  return chains;
}

// 图像类型判定（供工具层自动选模式）
function detectImageKind(img) {
  const w = img.w, h = img.h, n = w * h;
  const g = img.gray ? img.gray : toGray(img);
  let edges = 0;
  for (let y = 1; y < h; y++) for (let x = 1; x < w; x++) {
    const i = y * w + x;
    if (Math.abs(g[i] - g[i - 1]) > 40 || Math.abs(g[i] - g[i - w]) > 40) edges++;
  }
  const L = 8, step = 256 / L;
  const hist = new Map();
  for (let i = 0; i < n; i++) {
    let k;
    if (img.rgb) k = ((Math.floor(img.rgb[i * 3] / step) * L + Math.floor(img.rgb[i * 3 + 1] / step)) * L + Math.floor(img.rgb[i * 3 + 2] / step));
    else k = Math.floor(g[i] / step) * L * L;
    hist.set(k, (hist.get(k) || 0) + 1);
  }
  const top = [...hist.values()].sort((a, b) => b - a);
  const top3Share = ((top[0] || 0) + (top[1] || 0) + (top[2] || 0)) / n;
  const edgeDensity = edges / n;
  let kind = 'photo';
  if (top3Share > 0.85 && hist.size <= 400) kind = 'flat';
  else if (edgeDensity < 0.08) kind = 'lineart';
  return { kind: kind, top3Share: Math.round(top3Share * 1000) / 1000, edgeDensity: Math.round(edgeDensity * 10000) / 10000, colors: hist.size };
}

// ---------------------------------------------------------------- 几何
function rdpIdx(pts, eps) {
  const n = pts.length;
  if (n < 3) return [...Array(n).keys()];
  const keep = new Uint8Array(n); keep[0] = keep[n - 1] = 1;
  const stack = [[0, n - 1]];
  while (stack.length) {
    const [i, j] = stack.pop();
    if (j <= i + 1) continue;
    const ax = pts[i][0], ay = pts[i][1], bx = pts[j][0], by = pts[j][1];
    const dx = bx - ax, dy = by - ay, L = Math.hypot(dx, dy);
    let mx = -1, mi = -1;
    for (let k = i + 1; k < j; k++) {
      const p = pts[k];
      const d = L < 1e-9 ? Math.hypot(p[0] - ax, p[1] - ay) : Math.abs(dx * (p[1] - ay) - dy * (p[0] - ax)) / L;
      if (d > mx) { mx = d; mi = k; }
    }
    if (mx > eps) { keep[mi] = 1; stack.push([i, mi], [mi, j]); }
  }
  const out = [];
  for (let k = 0; k < n; k++) if (keep[k]) out.push(k);
  return out;
}

function bulgeFor(seg, p0, p1, thr) {
  const dx = p1[0] - p0[0], dy = p1[1] - p0[1];
  const L = Math.hypot(dx, dy);
  if (L < 1e-9 || seg.length < 3) return 0;
  let best = 0, sign = 0;
  for (let i = 1; i < seg.length - 1; i++) {
    const p = seg[i];
    const cross = dx * (p[1] - p0[1]) - dy * (p[0] - p0[0]);
    const perp = Math.abs(cross) / L;
    if (perp > best) { best = perp; sign = cross > 0 ? 1 : -1; }
  }
  let b = sign * 2 * best / L;
  b = Math.max(-1.4, Math.min(1.4, b));
  return Math.abs(b) < thr ? 0 : b;
}

// ---------------------------------------------------------------- 主流程
function traceImage(opts) {
  const o = Object.assign({
    input: null, gray: null, mode: 'arc', simplify: 1.2, arcThreshold: 0.02,
    minLength: 12, bridge: 6, scaleTo: 100, offsetX: 0, offsetY: 0, layer: 'LINEWORK',
    speck: 12, close: 1, sigma1: 0.8, sigma2: 1.6, posterizeLevels: 3, spur: 6, upscale: 2,
    maxWorkSide: 1600, flatLevels: 4, flatMinArea: 0.05, flatMinColor: 1, keepBackground: false, arcize: true,   // 2026-09-22
  }, opts || {});

  let img;
  if (o.gray) { img = { w: o.gray.w, h: o.gray.h, rgb: null, gray: Uint8Array.from(o.gray.data) }; }
  else img = loadImage(o.input);
  const w0 = img.w, h0 = img.h;
  // 2026-09-22: 工作分辨率上限（默认 1600）——大图降采样后再检测/细化，顺手磨平锯齿
  const maxWork = o.maxWorkSide == null ? 1600 : o.maxWorkSide;
  const fx = (maxWork > 0 && Math.max(w0, h0) > maxWork) ? Math.ceil(Math.max(w0, h0) / maxWork) : 1;
  if (fx > 1) img = downscaleImage(img, fx);
  const w = img.w, h = img.h;
  const g = img.gray ? img.gray : toGray(img);

  const S = o.scaleTo > 0 ? o.scaleTo / w0 : 1;   // 缩放以原始宽度为基准
  const up = 1;   // 固定 1（放大实验已证伪：块状掩膜细化更碎）
  const UW = w * up, UH = h * up;
  const out = (x, y) => [(x * up * fx) * S + o.offsetX, (h0 - y * up * fx) * S + o.offsetY];
  const stats = { mode: o.mode, w, h, paths: 0, points: 0, arcs: 0, straight: 0, bridged: 0 };
  const items = [];

  // ---- 掩膜 ----
  let mask;
  if (o.mode === 'posterize' || o.mode === 'flat' || o.mode === 'region') mask = null;
  else {
    const b1 = blur(g, w, h, o.sigma1), b2 = blur(g, w, h, o.sigma2);
    const dog = new Float64Array(w * h);
    let mn = Infinity, mx = -Infinity;
    for (let i = 0; i < w * h; i++) { const v = b1[i] - b2[i]; dog[i] = v; if (v < mn) mn = v; if (v > mx) mx = v; }
    const q = new Uint8Array(w * h);
    for (let i = 0; i < w * h; i++) q[i] = Math.round(255 * (dog[i] - mn) / (mx - mn || 1));
    const t = otsu(q);
    mask = new Uint8Array(w * h);
    for (let i = 0; i < w * h; i++) mask[i] = q[i] > t ? 1 : 0;
    if (o.speck > 0) {
      const { lab, sizes } = labelComponents(mask, w, h);
      for (let i = 0; i < w * h; i++) if (mask[i] && sizes[lab[i]] < o.speck) mask[i] = 0;
    }
    if (o.close > 0) mask = morph(morph(mask, w, h, 'dilate', o.close), w, h, 'erode', o.close);
    if (up > 1) {                       // 最近邻放大
      const big = new Uint8Array(UW * UH);
      for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
        const v = mask[y * w + x];
        if (!v) continue;
        for (let dy = 0; dy < up; dy++) for (let dx = 0; dx < up; dx++) big[(y * up + dy) * UW + (x * up + dx)] = 1;
      }
      mask = big;
    }
  }

  // ---- 取链 ----
  let chains = [];
  if (o.mode === 'outline') {
    chains = maskContours(mask, w, h);
    stats.closed = true;
  } else if (o.mode === 'posterize') {
    chains = posterizeChains(img, w, h, o);
    stats.closed = true;
  } else if (o.mode === 'flat' || o.mode === 'region') {
    // 2026-09-22: 扁平/纯色插画——按色块取轮廓（默认丢背景色，可按颜色分层）
    chains = regionChains(img, w, h, o);
    stats.closed = true;
  } else if (o.mode === 'skeleton') {
    // 实验：骨架走链（对块状掩膜会产生大量梯状交叉，仅调试用）
    let sk = thin(mask, w, h);
    if (o.spur > 0) sk = pruneSpurs(sk, w, h, o.spur);
    chains = skeletonChains(sk, w, h);
    if (o.bridge > 0 && chains.length > 1) chains = bridgeChains(chains, o.bridge / (S || 1), stats);
  } else {
    // centerline / arc：取骨架的轮廓（细线，稳定；与已验收的版本一致）
    const sk = thin(mask, w, h);
    chains = maskContours(sk, w, h);
    stats.closed = true;
  }

  // ---- 简化 + 输出 ----
  const minLen = o.minLength;
  for (const ch of chains) {
    if (ch.length < 2) continue;
    const pts = ch.map((p) => out(p[0], p[1]));
    // 长度过滤（输出坐标系）
    let len = 0; for (let i = 1; i < pts.length; i++) len += Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]);
    if (len < minLen * S) continue;
    const idx = rdpIdx(pts, o.simplify * S);    const sp = idx.map((k) => [Math.round(pts[k][0] * 1000) / 1000, Math.round(pts[k][1] * 1000) / 1000]);
    if (sp.length < 2) continue;
    const item = { layer: o.layer, closed: !!stats.closed, points: sp };
    if (ch.__color) { item.color = ch.__color; item.areaRatio = Math.round((ch.__areaRatio || 0) * 10000) / 10000; }   // flat 模式：色块代表色
    const wantArc = o.mode === 'arc' || ((o.mode === 'flat' || o.mode === 'region') && o.arcize !== false);
    if (wantArc) {
      const bulges = [];
      for (let a = 0; a < idx.length - 1; a++) {
        const seg = [];
        for (let k = idx[a]; k <= idx[a + 1]; k++) seg.push(pts[k]);
        const b = bulgeFor(seg, pts[idx[a]], pts[idx[a + 1]], o.arcThreshold);
        bulges.push(Math.round(b * 10000) / 10000);
        if (b) stats.arcs++; else stats.straight++;
      }
      bulges.push(0);
      item.bulges = bulges;
    }
    items.push(item);
    stats.paths++; stats.points += sp.length;
  }
  return { items, stats };
}

// 端点桥接：把相距 < d 的链端点接起来（贪心，最近优先）
function bridgeChains(chains, d, stats) {
  const ends = [];
  chains.forEach((c, ci) => {
    ends.push({ ci, end: 0, p: c[0] });
    ends.push({ ci, end: 1, p: c[c.length - 1] });
  });
  const used = new Set();
  const merged = new Map(chains.map((c, i) => [i, c.slice()]));
  for (let i = 0; i < ends.length; i++) {
    if (used.has(i)) continue;
    let best = -1, bd = d;
    for (let j = i + 1; j < ends.length; j++) {
      if (used.has(j) || ends[j].ci === ends[i].ci) continue;
      const dd = Math.hypot(ends[i].p[0] - ends[j].p[0], ends[i].p[1] - ends[j].p[1]);
      if (dd < bd) { bd = dd; best = j; }
    }
    if (best < 0) continue;
    const A = merged.get(ends[i].ci), B = merged.get(ends[best].ci);
    const a = ends[i].end === 0 ? A.slice().reverse() : A.slice();
    const b = ends[best].end === 0 ? B.slice() : B.slice().reverse();
    merged.set(ends[i].ci, a.concat(b));
    for (let k = 0; k < ends.length; k++) if (ends[k].ci === ends[best].ci) used.add(k);
    used.add(i); used.add(best);
    stats.bridged++;
  }
  return [...merged.values()].sort((x, y) => y.length - x.length);
}

// 颜色量化 → 每色块轮廓（闭合）
function posterizeChains(img, w, h, o) {
  const L = o.posterizeLevels;
  const step = 256 / L;
  const q = new Uint8Array(w * h * 3);
  for (let i = 0; i < w * h * 3; i++) q[i] = Math.min(255, Math.floor(img.rgb[i] / step) * step + step / 2);
  const keys = new Map();
  for (let i = 0; i < w * h; i++) {
    const k = q[i * 3] + ',' + q[i * 3 + 1] + ',' + q[i * 3 + 2];
    keys.set(k, (keys.get(k) || 0) + 1);
  }
  const keep = new Set([...keys.entries()].filter(([, n]) => n > w * h * 0.02).map(([k]) => k));
  const map = new Map([...keep].map((k, i) => [k, i + 1]));
  const cls = new Int16Array(w * h);
  for (let i = 0; i < w * h; i++) cls[i] = map.get(q[i * 3] + ',' + q[i * 3 + 1] + ',' + q[i * 3 + 2]) || 0;
  const chains = [];
  for (const id of new Set(cls)) {
    if (!id) continue;
    const m = new Uint8Array(w * h);
    for (let i = 0; i < w * h; i++) if (cls[i] === id) m[i] = 1;
    for (const c of maskContours(m, w, h)) if (c.length > 20) chains.push(c);
  }
  return chains;
}

module.exports = { traceImage, loadImage, toGray, detectImageKind, _internals: { blur, otsu, morph, thin, pruneSpurs, skeletonChains, maskContours, labelComponents, rdpIdx, downscaleImage, regionChains, detectImageKind } };

// ---- CLI（自测用）----
if (require.main === module) {
  const [file, mode, outFile] = process.argv.slice(2);
  const r = traceImage({ input: file, mode: mode || 'arc' });
  if (outFile) fs.writeFileSync(outFile, JSON.stringify(r, null, 1), 'utf8');
  console.log(JSON.stringify(r.stats));
}
