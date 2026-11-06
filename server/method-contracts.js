'use strict';
// method-contracts.js — 高频方法参数契约，注入 system prompt（2026-11-06）
//
// 为什么放在这里（而不是 listMethods 的返回里）：
//   实测（A/B，2026-11-06）：同一段注解
//     · 放在 listMethods 的 tool 返回里 → 模型照旧传 centerX/centerY/radius，0/3 正确
//     · 放进 system prompt             → 3/3 正确
//   模型的信任层级：system/user/tool-schema 是权威，tool 返回的文本只是参考资料，基本不遵循。
//   所以"提升注解"必须换位置，不是把描述写得更漂亮。
//
// 成本：整段约 1.5K 字符 ≈ 500 token/轮（system prompt 每轮都发）。
//   对照：28 个"真工具"要 +2,287 token/轮 —— 这条路便宜 4 倍，且不改接口、不用编译 C#。
//
// 维护纪律：
//   1. 这里只是**注解**，真相永远是 C# 实现。参数名漂移由 tools/check-method-contracts.js 拦截。
//   2. 只放"高频且模型容易错"的方法；不是全量清单（全量说明走 listMethods）。
//   3. 每条必须能说清：必填什么、可选什么、最容易错在哪、一个可照抄的例子。

// 选点依据：真实 trace（剔除探测数据后 884 次调用）里覆盖率最高的方法，
//   Top6 = 50%、Top16 = 80%。取 Top16。
const CONTRACTS = [
  { name: 'getDrawingInfo', req: [], opt: [], note: '' },
  { name: 'getEntityInfo', req: [['handle', '字符串']], opt: [], note: '' },
  {
    name: 'createCircle',
    req: [['center', '数组 [x, y]'], ['diameter', '数字']],
    opt: [],
    note: 'center 必须是数组（不是 centerX/centerY 两个字段）；diameter 是**直径**，用户说半径 R 时传 R*2',
    ex: { center: [0, 0], diameter: 100 },
  },
  {
    name: 'getEntitiesByLayer',
    req: [['layer', '字符串']],
    opt: [['maxCount', '数字'], ['offset', '数字'], ['minZ', '数字'], ['maxZ', '数字']],
    note: 'layer 为图层名精确匹配',
    ex: { layer: '道路' },
  },
  { name: 'revertEntry', req: [['seq', '数字']], opt: [['through', '布尔']], note: 'through=true 表示该条及其之后一起按逆序撤回' },
  { name: 'getSelectionDetail', req: [], opt: [], note: '' },
  { name: 'journalList', req: [], opt: [['limit', '数字']], note: '' },
  { name: 'closeDrawing', req: [], opt: [['name', '字符串'], ['force', '布尔']], note: '有未保存路径的图默认拒绝；force=true 先存盘再关' },
  { name: 'getCivil3DHealth', req: [], opt: [], note: '' },
  { name: 'newDrawing', req: [], opt: [['templatePath', '字符串']], note: '' },
  { name: 'listTexts', req: [], opt: [['layer', '字符串'], ['maxCount', '数字'], ['offset', '数字']], note: '' },
  { name: 'setEntityLayer', req: [['layer', '字符串']], opt: [], note: '需要 handle 参数时用 handles 或 handle，见方法描述' },
  { name: 'analyzeLayer', req: [['layer', '字符串']], opt: [], note: '' },
  { name: 'createLayer', req: [['name', '字符串']], opt: [['color', '数字']], note: '' },
  {
    name: 'offsetEntity',
    req: [['handle', '字符串'], ['distance', '数字']],
    opt: [['layer', '字符串']],
    note: '',
    ex: { handle: '1A2B', distance: 20 },
  },
  { name: 'getLayers', req: [], opt: [], note: '' },
];

function fmt(rows) {
  return rows.map(([n, t]) => n + '=' + t).join(', ');
}

/** 渲染成注入 system prompt 的文本块 */
function renderContracts() {
  const lines = ['', '=== 常用方法参数契约（params 严格按此填；参数名自创会直接调用失败）==='];
  for (const c of CONTRACTS) {
    const parts = [];
    if (c.req.length) parts.push('必填 ' + fmt(c.req));
    if (c.opt.length) parts.push('可选 ' + fmt(c.opt));
    if (!parts.length) parts.push('无参数（params 传 {}）');
    lines.push(c.name.padEnd(20) + parts.join('；') + (c.note ? '｜' + c.note : ''));
    if (c.ex) lines.push(' '.repeat(20) + '例：' + JSON.stringify(c.ex));
  }
  lines.push('注意：以上方法契约已在此列明，调用它们前不必再调 listMethods 确认。');
  return lines.join('\n');
}

/** 只有名字的集合，供门禁比对 */
function contractNames() {
  return CONTRACTS.map((c) => c.name);
}

module.exports = { CONTRACTS, renderContracts, contractNames };
