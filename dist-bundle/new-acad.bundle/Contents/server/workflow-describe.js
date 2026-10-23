'use strict';
// workflow-describe.js -- P2.4: turn a recorded step into a sentence a designer can read.
//
// Why: Bro's verdict on the last round was blunt -- "问题不是一定要合并，而是说明的人类可读性要高".
// The tables were showing `createSphere | B | x=0,y=0,z=8,radius=8`, which is developer-speak.
// This module is the single place that decides how a step is phrased, in both languages, so the
// panel never has to invent wording (and the wording can be unit-tested without CAD).
//
// Rules:
//   * say WHAT is drawn / changed and WHERE, in the user's terms (球心, 半径, 图层, 图元)
//   * keep the technical method name out of the sentence (it stays in the record for debugging)
//   * never invent values: unknown parameters are shown as-is, never guessed
//   * short: one line, no trailing punctuation

const UNIT_WORD = { length: '', station: '', count: '', ratio: '' };

function num(v) {
  if (typeof v !== 'number') return String(v);
  const r = Math.round(v * 1000) / 1000;
  return String(r);
}

function pick(params, names) {
  for (const n of names) if (params && params[n] !== undefined && params[n] !== null) return params[n];
  return undefined;
}

function listAll(params) {
  const out = [];
  if (!params || typeof params !== 'object') return out;
  for (const [k, v] of Object.entries(params)) {
    if (v === null || v === undefined) continue;
    if (Array.isArray(v)) { out.push(k + '=' + v.map(num).join('/')); continue; }
    if (typeof v === 'object') continue;
    out.push(k + '=' + num(v));
  }
  return out;
}

function zh(step) {
  const p = (step && step.params) || {};
  const m = String((step && step.method) || '');
  const layer = pick(p, ['layer', 'layerName']);

  switch (m) {
    case 'createSphere': {
      const c = Array.isArray(p.center) ? p.center.map(num).join(',') : [num(p.x), num(p.y), num(p.z)].join(',');
      return '\u4ee5 (' + c + ') \u4e3a\u7403\u5fc3\u753b\u4e00\u4e2a\u534a\u5f84 ' + num(pick(p, ['radius'])) + ' \u7684\u7403' + (layer ? '\uff0c\u653e\u5230\u56fe\u5c42\u300c' + layer + '\u300d' : '');
    }
    case 'createCylinder': {
      const c = Array.isArray(p.center) ? p.center.map(num).join(',') : [num(p.x), num(p.y), num(p.z)].join(',');
      return '\u4ee5 (' + c + ') \u4e3a\u5e95\u9762\u4e2d\u5fc3\u753b\u4e00\u4e2a\u534a\u5f84 ' + num(pick(p, ['radius'])) + '\u3001\u9ad8 ' + num(pick(p, ['height'])) + ' \u7684\u5706\u67f1' + (layer ? '\uff0c\u653e\u5230\u56fe\u5c42\u300c' + layer + '\u300d' : '');
    }
    case 'createCircle': {
      const c = Array.isArray(p.center) ? p.center.map(num).join(',') : [num(p.centerX), num(p.centerY)].join(',');
      return '\u4ee5 (' + c + ') \u4e3a\u5706\u5fc3\u753b\u4e00\u4e2a\u76f4\u5f84 ' + num(pick(p, ['diameter'])) + ' \u7684\u5706';
    }
    case 'createLineSegment':
      return '\u4ece (' + num(p.startX) + ',' + num(p.startY) + ') \u5230 (' + num(p.endX) + ',' + num(p.endY) + ') \u753b\u4e00\u6761\u7ebf';
    case 'createLayer':
      return '\u5efa\u4e00\u4e2a\u56fe\u5c42\u300c' + (p.name || '') + '\u300d' + (p.color !== undefined ? '\uff0c\u989c\u8272 ' + num(p.color) : '');
    case 'deleteEntities': {
      const n = Array.isArray(p.handles) ? p.handles.length : (p.handle ? 1 : 0);
      return '\u5220\u6389 ' + n + ' \u4e2a\u56fe\u5143';
    }
    case 'getEntitiesByLayer': {
      const n = Array.isArray(p.layers) ? p.layers.length : 1;
      return '\u6e05\u70b9\u56fe\u5c42' + (p.layer ? '\u300c' + p.layer + '\u300d' : '') + '\u91cc\u7684\u56fe\u5143' + (n > 1 ? '\uff08' + n + ' \u4e2a\u56fe\u5c42\uff09' : '') + '\uff0c\u53ea\u8bfb';
    }
    case 'getEntityInfo':
      return '\u8bfb\u4e00\u4e0b\u56fe\u5143 ' + (Array.isArray(p.handles) ? p.handles.join('/') : (p.handle || '')) + ' \u7684\u4fe1\u606f\uff0c\u53ea\u8bfb';
    case 'listMethods':
      return '\u67e5\u4e00\u4e0b\u6709\u54ea\u4e9b\u65b9\u6cd5' + (p.filter ? '\uff08\u5173\u952e\u5b57 ' + p.filter + '\uff09' : '') + '\uff0c\u53ea\u8bfb';
    case 'offsetEntity':
      return '\u628a\u56fe\u5143 ' + (p.handle || (Array.isArray(p.handles) ? p.handles.join('/') : '')) + ' \u5411\u5916\u504f\u79fb ' + num(pick(p, ['distance', 'offset'])) ;
    case 'setEntitiesColor': {
      const n = Array.isArray(p.handles) ? p.handles.length : (p.handle ? 1 : 0);
      return '\u628a ' + n + ' \u4e2a\u56fe\u5143\u6539\u6210\u989c\u8272 ' + num(p.color);
    }
    case 'moveEntity':
    case 'moveEntities':
      return '\u79fb\u52a8\u56fe\u5143 ' + num(p.dx) + ', ' + num(p.dy) + (p.dz !== undefined ? ', ' + num(p.dz) : '');
    case 'copyEntity':
    case 'copyEntities':
      return '\u590d\u5236\u56fe\u5143 ' + (p.handle || (Array.isArray(p.handles) ? p.handles.length + ' \u4e2a' : ''));
    case 'assertChecks': {
      const n = Array.isArray(p.checks) ? p.checks.length : 0;
      return '\u6821\u9a8c ' + n + ' \u6761\uff08\u4e0d\u901a\u8fc7\u5c31\u8bf4\u660e\u8fd9\u6b21\u56de\u653e\u4e0d\u5bf9\uff09';
    }
    case 'booleanUnion':
      return '\u628a ' + (Array.isArray(p.handles) ? p.handles.length : 0) + ' \u4e2a\u5b9e\u4f53\u5408\u5e76\u6210\u4e00\u4e2a';
    default: {
      const rest = listAll(p).slice(0, 4).join(', ');
      return m + (rest ? '\uff1a' + rest : '');
    }
  }
}

function en(step) {
  const p = (step && step.params) || {};
  const m = String((step && step.method) || '');
  const layer = pick(p, ['layer', 'layerName']);
  switch (m) {
    case 'createSphere':
      return 'Draw a sphere at (' + [num(p.x), num(p.y), num(p.z)].join(',') + ') with radius ' + num(p.radius) + (layer ? ' on layer ' + layer : '');
    case 'createCylinder':
      return 'Draw a cylinder at (' + [num(p.x), num(p.y), num(p.z)].join(',') + '), radius ' + num(p.radius) + ', height ' + num(p.height) + (layer ? ' on layer ' + layer : '');
    case 'createCircle':
      return 'Draw a circle centred at (' + (Array.isArray(p.center) ? p.center.map(num).join(',') : '') + ') with diameter ' + num(p.diameter);
    case 'createLineSegment':
      return 'Draw a line from (' + num(p.startX) + ',' + num(p.startY) + ') to (' + num(p.endX) + ',' + num(p.endY) + ')';
    case 'createLayer':
      return 'Create layer ' + (p.name || '') + (p.color !== undefined ? ', colour ' + num(p.color) : '');
    case 'deleteEntities':
      return 'Delete ' + (Array.isArray(p.handles) ? p.handles.length : 1) + ' entity/entities';
    case 'getEntitiesByLayer':
      return 'List entities on layer ' + (p.layer || '') + ' (read only)';
    case 'getEntityInfo':
      return 'Read entity ' + (p.handle || '') + ' (read only)';
    case 'listMethods':
      return 'Look up available methods' + (p.filter ? ' (filter ' + p.filter + ')' : '') + ' (read only)';
    case 'offsetEntity':
      return 'Offset entity ' + (p.handle || '') + ' by ' + num(pick(p, ['distance', 'offset']));
    default: {
      const rest = listAll(p).slice(0, 4).join(', ');
      return m + (rest ? ': ' + rest : '');
    }
  }
}

function describeStep(step, locale) {
  const text = locale === 'en' ? en(step) : zh(step);
  return { zh: zh(step), en: en(step), text };
}

module.exports = { describeStep, zh, en };
