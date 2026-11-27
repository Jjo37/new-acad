'use strict';
// slot-policy.js -- P2.6 S5: 哪些东西**才可以**成为可绑定槽位（2026-10-28）
//
// 病根：P2.4 的抽取是"黑名单式"——先宽松地收，再一条条排除句柄/大数组/内部字段。
// 黑名单的问题是：没被想到的东西默认**会**变成槽位。2026-10-23 就是这样冒出假槽位
// `handles._array = 6`（结果摘要器的包装字段），用户不填它就没法重放。
//
// 这里换成白名单式：**不在名单里的一律不是槽位**。三条硬保证：
//   S5-A 句柄（handle/handles/objectId/stableId...）永不成为槽位 —— 那是对象绑定，不是输入
//   S5-B 几何大数组（points/vertices/coordinates...、含对象或嵌套数组、长度超限）永不是槽位
//   S5-C 内部字段（_array/_truncated/sample/requestId/approvalToken/docName/token...）永不是槽位
//
// 纯模块：无 I/O、无时间、无随机。抽取器与门禁都只认这一个判据来源。
//
// 加白名单的规矩：只加"用户换得起、且换了有意义"的参数名。宁少不多 ——
// 错槽位会让用户以为是那个数在起作用，比没有槽位更糟。

// ---------- 数值型：用户真正会改的量（含复数形式，如 widths[]） ----------
const NUMBER_KEYS = new RegExp([
  '^(diameter|radius|width|height|length|depth|thickness|distance|offset|spacing|interval|gap)s?$',
  '^(angle|rotation|grade|slope|percent|ratio|bulge)s?$',
  '^(area|volume)$',
  '^(colorIndex|color|lineWeight|lineweight|ltscale)$',
  '^(count|qty|quantity|number|times|levels|columns|rows|segments)$',
  '^(station|startStation|endStation|stationStart|stationEnd)$',
  '^(elevation|elev|z|startZ|endZ|x|y|startX|startY|endX|endY|easting|northing)$',
].join('|'), 'i');

// ---------- 名称型：换图/换层时会被替换的名字 ----------
const NAME_KEYS = new RegExp([
  '^(layer|layerName|style|styleName|blockName|templateName)$',
  '^(linetype|plotStyle|textStyle|labelStyle|pointStyle)$',
  '^(surfaceName|alignmentName|profileName|assemblyName|corridorName)$',
  '^(networkName|pipeNetworkName|groupName|pointGroup|partName)$',
  '^(subassemblyName|subassemblyType|categoryName|siteName|featureLineName)$',
  '^(name)$',
].join('|'), 'i');

// ---------- 路径型：文件路径（导出/导入） ----------
const PATH_KEYS = /^(path|filePath|fileName|outputPath|inputPath|targetPath|savePath|exportPath|importPath|file)$/i;

// ---------- S5-C：内部字段 —— 永不是槽位 ----------
// 前三个来自结果摘要器；_fp/_ok/_role 是记录器自己的字段；requestId/approvalToken 一类是运行时凭据。
const INTERNAL_KEYS = new RegExp([
  '^(_array|_truncated|_object|sample)$',
  '^(_fp|_ok|_role|_i|_ms|_bytes|_doc|_channel)$',
  '^(requestId|sessionId|taskId|jobId|documentId|docName|drawingName|approvalToken|token|apiKey|password|secret|credential|fp|fingerprint)$',
  '^(method|channel|role|ok|status|errCode|error|params|result|handlesOut|polling)$',
].join('|'), 'i');

// ---------- S5-A：句柄类键 —— 永不是槽位 ----------
const HANDLE_KEYS = /(handle|objectid|stableid|entityId|entId|id)$/i;

// ---------- S5-B：几何容器键 —— 即使很短也不是槽位 ----------
const GEOMETRY_KEYS = new RegExp([
  '^(points|pointList|vertices|vertexList|nodes|grid|coords|coordinates|csV|csList)$',
  '^(center|centers|startPoint|endPoint|midPoint|insertPoint|basePoint|origin)$',
  '^(polyline|polygon|boundary|outline|contour|profile|section|path3d)$',
  '^(handles|handlesList|ids|idsList)$',
].join('|'), 'i');

// 形状上限：超过就不做槽位（几何载荷 / 不该由用户手填的批量数据）
const MAX_ARRAY_LEN = 8;      // 小标量数组（如 widths[]）仍可绑；超了就按载荷处理
const MAX_DEPTH = 4;

// 句柄形状的值：1~8 位十六进制。只在高危位置（名字键）才拦，
// 避免把 "1A" 这种正常编码值在别处误判（工程师的编码经常长得像句柄）。
const HANDLE_VALUE = /^[0-9A-Fa-f]{1,8}$/;

const UNIT_HINTS = [
  [/^(start|end|base|top|min|max)?(x|y|z)$|easting|northing/i, 'length'],
  [/diameter|radius|width|length|distance|offset|elevation|elev|height|depth|thickness|spacing|interval|gap/i, 'length'],
  [/station/i, 'station'],
  [/count|qty|quantity|number|times|levels|columns|rows|segments/i, 'count'],
  [/angle|rotation|grade|slope|percent|ratio|bulge/i, 'ratio'],
  [/area/i, 'area'],
  [/volume/i, 'volume'],
];

function unitOf(key) {
  for (const [re, unit] of UNIT_HINTS) if (re.test(key)) return unit;
  return '';
}

/**
 * 单个叶子值是否可以作为槽位。
 * @returns {{allowed: boolean, type?: 'number'|'name'|'path', unit?: string, why?: string}}
 */
function decideSlot(key, value) {
  const k = String(key || '');
  if (!k) return { allowed: false, why: 'no-key' };
  if (INTERNAL_KEYS.test(k)) return { allowed: false, why: 'internal-field' };
  if (HANDLE_KEYS.test(k)) return { allowed: false, why: 'handle-key' };

  if (typeof value === 'number') {
    if (!Number.isFinite(value)) return { allowed: false, why: 'not-finite' };
    if (!NUMBER_KEYS.test(k)) return { allowed: false, why: 'not-whitelisted-number' };
    return { allowed: true, type: 'number', unit: unitOf(k) };
  }

  if (typeof value === 'string') {
    if (!value) return { allowed: false, why: 'empty' };
    if (PATH_KEYS.test(k) || /[\\/][^\\/]*\.[A-Za-z0-9]{1,6}$/.test(value)) {
      return { allowed: true, type: 'path' };
    }
    if (NAME_KEYS.test(k)) {
      // 名字键下的"句柄形状"值是常见事故（把 stableId 错写成 name）—— 宁可不出槽位
      if (HANDLE_VALUE.test(value)) return { allowed: false, why: 'handle-shaped-name' };
      return { allowed: true, type: 'name' };
    }
    return { allowed: false, why: 'not-whitelisted-string' };
  }

  return { allowed: false, why: 'unsupported-type' };
}

/** 容器键是否一律跳过（几何载荷）。 */
function isGeometryKey(key) {
  return GEOMETRY_KEYS.test(String(key || ''));
}

module.exports = {
  decideSlot,
  isGeometryKey,
  unitOf,
  NUMBER_KEYS,
  NAME_KEYS,
  PATH_KEYS,
  INTERNAL_KEYS,
  HANDLE_KEYS,
  GEOMETRY_KEYS,
  HANDLE_VALUE,
  MAX_ARRAY_LEN,
  MAX_DEPTH,
};
