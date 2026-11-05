'use strict';
// workflow-slots.js -- P2.4: turn a recorded run into a parameterised artifact (2026-10-22, 1.11.0)
//
// Why: replaying fixed parameters is worthless -- it just draws the same circle again. The value
// only exists once the numbers/names can be re-entered (new drawing, new layer, new station range).
//
// Design rules (agreed with Bro 2026-10-22):
//   1. **宁少不多**: a wrong slot silently uses the wrong number, which is worse than no slot at
//      all. Only values we can name confidently become slots.
//   2. **Handles are not slots.** A handle is an object reference; the user must point at an
//      object on the drawing (object binding), not type a hex string.
//   3. **No guessing**: an unbound slot blocks execution instead of falling back to the recorded
//      value.
//   4. Every slot usage is visible: step index + parameter path, so the UI can show "this step
//      used slot s3".
//
// This file is pure (no I/O, no CAD) so it can be unit-tested against real recorded shapes.

// 2026-10-28 (P2.6 S5): 判据全部来自 slot-policy.js —— **白名单制**，不在名单里的一律不是槽位。
// 原来这里是黑名单式（宽收 + 逐条排除），没被想到的东西默认会变成槽位 —— 2026-10-23 的假槽位
// `handles._array = 6` 就是这么来的。三条硬保证（句柄 / 几何大数组 / 内部字段）现在由策略模块
// 统一给出，抽取器不再自己判断"这个键像不像参数"。
const P = require('./slot-policy.js');

const PATH_VALUE = /[\\/][^\\/]*\.[A-Za-z0-9]{1,6}$/;

const unitOf = P.unitOf;

function lastKeyOf(path) {
  const parts = String(path).replace(/\[(\d+)\]/g, '.$1').split('.');
  // walk back to the nearest non-numeric segment (arrays: take the parent key, e.g. widths[2])
  for (let i = parts.length - 1; i >= 0; i--) {
    if (parts[i] && !/^\d+$/.test(parts[i])) return parts[i];
  }
  return '';
}

// Walk a step's params and collect candidate slots. 白名单制 + 形状上限：
// 几何容器整枝跳过、大数组跳过、嵌套对象/数组跳过，剩下的叶子交给 decideSlot 判定。
function extractSlots(steps) {
  const slots = [];
  const byKey = new Map();

  const add = (step, path, key, value, type, unit) => {
    const dedupe = key + '|' + type + '|' + String(value);
    let slot = byKey.get(dedupe);
    if (!slot) {
      slot = {
        id: 's' + (slots.length + 1),
        // human label keeps the array index: "center¹ = 5" is ambiguous, "center[0] = 5" is not.
        label: path + ' = ' + String(value),
        key,
        type,
        unit,
        value,               // default = what the record used
        usages: [],
      };
      byKey.set(dedupe, slot);
      slots.push(slot);
    }
    slot.usages.push({ step: step.i, path, method: step.method });
  };

  for (const step of steps || []) {
    const params = step.params;
    if (!params || typeof params !== 'object') continue;

    const walk = (node, path, depth, key) => {
      if (depth > P.MAX_DEPTH) return;
      if (P.INTERNAL_KEYS.test(key)) return;        // S5-C：容器级也要拦（sample 是个对象）
      if (P.isGeometryKey(key)) return;             // S5-B：几何容器整枝跳过

      if (Array.isArray(node)) {
        if (!node.length) return;
        if (node.length > P.MAX_ARRAY_LEN) return;  // 大数组 = 载荷，不是输入
        if (node.some((v) => v !== null && typeof v === 'object')) return;  // 嵌套结构 = 几何
        node.forEach((v, i) => walk(v, path + '[' + i + ']', depth + 1, key));
        return;
      }

      if (node && typeof node === 'object') {
        for (const [k, v] of Object.entries(node)) {
          walk(v, path ? path + '.' + k : k, depth + 1, k);
        }
        return;
      }

      const leafKey = key || lastKeyOf(path);
      const decided = P.decideSlot(leafKey, node);
      if (!decided.allowed) return;
      add(step, path, leafKey, node, decided.type, decided.unit || '');
    };

    walk(params, '', 0, '');
  }
  return slots;
}

// Apply user-supplied values. Never falls back to the recorded value: an unbound slot is an error.
function applyBindings(steps, slots, values) {
  const map = new Map();
  const unbound = [];
  const invalid = [];
  const vals = values || {};

  for (const slot of slots || []) {
    const has = Object.prototype.hasOwnProperty.call(vals, slot.id);
    if (!has || vals[slot.id] === '' || vals[slot.id] === null || vals[slot.id] === undefined) {
      unbound.push({ id: slot.id, label: slot.label, type: slot.type });
      continue;
    }
    const v = vals[slot.id];
    if (slot.type === 'number') {
      const n = typeof v === 'number' ? v : Number(v);
      if (!Number.isFinite(n)) { invalid.push({ id: slot.id, why: 'not a number: ' + String(v) }); continue; }
      if (Math.abs(n) > 1e7) { invalid.push({ id: slot.id, why: 'out of range: ' + n }); continue; }
      map.set(slot.id, n);
    } else if (slot.type === 'path') {
      if (typeof v !== 'string' || !v.trim()) { invalid.push({ id: slot.id, why: 'empty path' }); continue; }
      map.set(slot.id, v.trim());
    } else {
      if (typeof v !== 'string' || !v.trim()) { invalid.push({ id: slot.id, why: 'empty name' }); continue; }
      map.set(slot.id, v.trim());
    }
  }

  const out = JSON.parse(JSON.stringify(steps || []));
  for (const slot of slots || []) {
    if (!map.has(slot.id)) continue;
    for (const use of slot.usages) {
      const target = out.find((s) => s.i === use.step);
      if (target) setPath(target.params, use.path, map.get(slot.id));
    }
  }
  return { steps: out, unbound, invalid, values: Object.fromEntries(map) };
}

function setPath(obj, path, value) {
  const parts = String(path).replace(/\[(\d+)\]/g, '.$1').split('.').filter(Boolean);
  let cur = obj;
  for (let i = 0; i < parts.length - 1; i++) {
    if (cur[parts[i]] === undefined || cur[parts[i]] === null) return;
    cur = cur[parts[i]];
  }
  if (cur && typeof cur === 'object') cur[parts[parts.length - 1]] = value;
}

// What did the run actually change? Without this the user cannot tell success from silence.
const DELETE_RE = /^(delete|erase|purge|overkill|remove|clear|drop)/i;
const MODIFY_RE = /^(set|move|rotate|scale|edit|rename|transform|align|offset|array|copy|mirror|join|trim|extend|fillet|chamfer|break)/i;
const ASSERT_RE = /^(assert|verify|qcCheck|check)/i;

function summarizeEffects(results, slotValues) {
  let created = 0;
  let modified = 0;
  let deleted = 0;
  const checks = [];

  for (const r of results || []) {
    const params = (r && r.params) || {};
    const handles = params.handles || (params.handle ? [params.handle] : []);
    const n = Array.isArray(handles) ? handles.length : (handles ? 1 : 0);
    if (r.status !== 'ok') continue;
    const newH = (r.handles || []).length;
    if (DELETE_RE.test(r.method || '')) { deleted += n || 1; continue; }
    created += newH;
    // an offset produces a NEW entity; counting it as "modified" too would double count it.
    if (MODIFY_RE.test(r.method || '') && newH === 0) modified += n || 1;
    if (ASSERT_RE.test(r.method || '')) {
      const res = r.rawResult || {};
      checks.push({ step: r.i, method: r.method, passed: res.passed, failed: res.failed });
    }
  }
  return {
    created,
    modified,
    deleted,
    checks,
    slots: slotValues || {},
  };
}

module.exports = { extractSlots, applyBindings, summarizeEffects, unitOf, lastKeyOf, setPath };
