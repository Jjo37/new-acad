#!/usr/bin/env node
// ensure-drawing.js — 真机门禁的前置：确保 CAD 里有一张打开的图纸（2026-10-27）
//
// 背景：C3D 停在"开始"页时，所有需要图纸的方法都返回 CIVIL3D.NO_DRAWING，
// 真机回归整批 FAIL（v1.11.0-fix3 发布时踩过）。发布链在跑 --with-cad 之前必须先建图。
//
// 用法：node tools/ensure-drawing.js [--verbose]

const net = require('net');
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const CFG = (() => { try { return JSON.parse(fs.readFileSync(path.join(ROOT, 'config.json'), 'utf8').replace(/^\uFEFF/, '')); } catch { return {}; } })();
const PORT = CFG.bridgePort || 8080;

function rpc(method, params = {}, timeoutMs = 90000) {
  return new Promise((resolve) => {
    const s = net.connect(PORT, '127.0.0.1');
    let b = '';
    let done = false;
    const fin = (v) => { if (!done) { done = true; try { s.destroy(); } catch { } resolve(v); } };
    s.setEncoding('utf8');
    s.on('connect', () => s.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method, params }) + '\n'));
    s.on('data', (d) => { b += d; });
    s.on('close', () => { try { const r = JSON.parse(b); fin(r.error ? { ok: false, error: r.error } : { ok: true, result: r.result }); } catch { fin({ ok: false, error: { message: 'bad json' } }); } });
    s.on('error', (e) => fin({ ok: false, error: { message: e.message } }));
    setTimeout(() => fin({ ok: false, error: { message: 'timeout' } }), timeoutMs);
  });
}

(async () => {
  const h = await rpc('getCivil3DHealth', {}, 15000);
  if (!h.ok) { console.error('[ensure-drawing] plugin offline: ' + h.error.message); process.exit(1); }
  if (h.result.drawingLoaded) { console.log('[ensure-drawing] a drawing is already open'); process.exit(0); }
  console.log('[ensure-drawing] no active drawing -> newDrawing');
  const n = await rpc('newDrawing', {});
  if (!n.ok) { console.error('[ensure-drawing] newDrawing failed: ' + JSON.stringify(n.error).slice(0, 200)); process.exit(1); }
  const h2 = await rpc('getCivil3DHealth', {}, 15000);
  const ok = !!(h2.ok && h2.result.drawingLoaded);
  console.log('[ensure-drawing] ' + (ok ? 'drawing ready: ' + (n.result && n.result.drawingName) : 'STILL no drawing'));
  process.exit(ok ? 0 : 1);
})();
