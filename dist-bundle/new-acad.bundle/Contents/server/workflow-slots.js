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

const HANDLE_KEY = /handle/i;
const PATH_VALUE = /[\\/][^\\/]*\.[A-Za-z0-9]{1,6}$/;

// Keys whose string value is a name the user may legitimately swap.
const NAME_KEY = /^(layer|layerName|style|styleName|blockName|surfaceName|alignmentName|profileName|assemblyName|corridorName|networkName|groupName|name|pointGroup|templateName|categoryName|subassemblyName|subassemblyType)$/i;

const UNIT_HINTS = [
  [/diameter|radius|width|length|distance|offset|elevation|height|depth|thickness|spacing|interval|extent/i, 'length'],
  [/station/i, 'station'],
  [/count|qty|quantity|number|times|levels|columns|rows/i, 'count'],
  [/angle|rotation|grade|slope|percent|ratio|bulge/i, 'ratio'],
  [/area/i, 'area'],
  [/volume/i, 'volume'],
];

function unitOf(key) {
  for (const [re, unit] of UNIT_HINTS) if (re.test(key)) return unit;
  return '';
}

function lastKeyOf(path) {
  const parts = String(path).replace(/\[(\d+)\]/g, '.$1').split('.');
  // walk back to the nearest non-numeric segment (arrays: take the parent key, e.g. widths[2])
  for (let i = parts.length - 1; i >= 0; i--) {
    if (parts[i] && !/^\d+$/.test(parts[i])) return parts[i];
  }
  return '';
}

function isHandleLike(value) {
  return typeof value === 'string' && /^[0-9A-Fa-f]{1,8}$/.test(value);
}

// Walk a step's params and collect candidate slots. Bounded on purpose: deep/large structures
// (point arrays, 2000-element lists) are not slots, they are geometry payloads.
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
    const walk = (node, path, depth, arrayLen) => {
      if (depth > 4) return;
      if (Array.isArray(node)) {
        // ignore big geometry arrays entirely (points[], handles[], csV lists ...)
        if (node.length > 12) return;
        node.forEach((v, i) => walk(v, path + '[' + i + ']', depth + 1, node.length));
        return;
      }
      if (node && typeof node === 'object') {
        for (const [k, v] of Object.entries(node)) {
          // 2026-10-23: the oversized-result summarizer wraps arrays as {_array, sample}. That
          // envelope is a reporting shape, not a parameter -- scanning it produced a bogus
          // "handles._array = 6" slot that then had to be filled before a replay could start.
          if (k === '_array' || k === '_truncated' || k === 'sample') continue;
          walk(v, path ? path + '.' + k : k, depth + 1);
        }
        return;
      }
      const key = lastKeyOf(path);
      if (HANDLE_KEY.test(key)) return;                       // object binding, not a slot
      if (typeof node === 'number') {
        if (!Number.isFinite(node)) return;
        add(step, path, key, node, 'number', unitOf(key));
        return;
      }
      if (typeof node === 'string') {
        if (!node) return;
        if (isHandleLike(node) && HANDLE_KEY.test(key)) return;
        if (PATH_VALUE.test(node)) { add(step, path, key, node, 'path', ''); return; }
        if (NAME_KEY.test(key)) { add(step, path, key, node, 'name', ''); return; }
        return;                                               // free text / codes: leave alone
      }
      // booleans and nulls are switches, not parameters
    };
    walk(params, '', 0);
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
