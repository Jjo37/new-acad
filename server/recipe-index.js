'use strict';
// recipe-index.js — v1.18 findRecipes：按意图反查「能用的做法」
//
// 2026-11-18 立。为什么要有它：工具面 439 个方法，客户 AI 面对"统计地块面积""复核管网水力"这种
// 任务级说法时，只能靠猜方法名然后一个个试；resolved 经验（昨天刚跑通的、或项目里历史跑通的）
// 也没有地方复用 —— workflow-store 只在"回放"时用得上。
//
// 做法：
//   ① 内置配方（curated）：每条 = 意图关键词 + 有序步骤（真实方法名）+ 已知坑
//   ② 项目里已记录的工作流（workflow-store）：ask 文本 + 实际步骤，作为"本项目跑通过的做法"
//   ③ 两路一起按中文二元组 + 方法名重合打分，返回前 N 条
//
// 输出刻意带 steps 和 notes：客户 AI 拿到就能照着调，而不是"再问一次"。

const fs = require('fs');
const path = require('path');
const workflowStore = require('./workflow-store');

// ---------- 内置配方（方法名都取自 listMethods 的真实清单） ----------
const BUILTIN = [
  {
    id: 'builtin:parcel-area-report',
    title: '地块面积统计与出报告',
    intent: '统计 地块 面积 汇总 报告 导出 地块表 parcel area report',
    steps: [
      { method: 'listParcelSites', why: '先确认 site（只有 1 个时后续方法可省 siteName）' },
      { method: 'listParcels', why: '拿地块清单（area/perimeter/style 都有；perimeter 是曲线弧长）' },
      { method: 'qtyParcelAreas', why: '按名字子集汇总；注意 requestedCount / matchedCount / missingNames 三个字段' },
      { method: 'reportParcels', why: '出报告，outputPath 写 CSV；units 支持 sqft/sqm/acres/ha（按图纸单位换算）' },
    ],
    notes: '地块创建/删除托管 API 做不到（NOT_SUPPORTED，需界面操作）；改名可以，但 C3D 拒绝名字里的 ":"。',
  },
  {
    id: 'builtin:parcel-rename',
    title: '地块改名 / 换样式',
    intent: '地块 改名 重命名 样式 换样式 parcel rename restyle',
    steps: [
      { method: 'listParcels', why: '拿准确地块名（名字里的冒号是自动命名格式）' },
      { method: 'editParcel', why: 'newName/style/description 都可改；返回 verified + applied 回读结果' },
    ],
    notes: '改名前会查重（撞名回 CONFLICT）；名字不能含 ":" 等禁用字符；结果一定回读校验。',
  },
  {
    id: 'builtin:pipe-hydraulics',
    title: '管网水力/坡度复核',
    intent: '管网 水力 坡度 流量 流速 复核 检查 pipe network hydraulics slope',
    steps: [
      { method: 'listPipeNetworks', why: '拿网名与规模' },
      { method: 'getPipeNetworkInfo', why: '逐管 invert/slope（slope 是比值，slopePercent 才是百分数）' },
      { method: 'qtyPipeNetworkLengths', why: '按管径/材质汇总长度（键里带单位，英制是 in）' },
      { method: 'checkPipeNetworkInterference', why: '与曲面/管网间距冲突（samplesPerPipe 可调）' },
      { method: 'qcCheckPipeNetwork', why: '按阈值判定；没给阈值会回 checksRun + hint，不会假装通过' },
    ],
    notes: '满管流量用曼宁公式（qFull/velocity + flowUnits/velocityUnits）；API 不暴露真实管底高程时 invert 会标 invertSource。',
  },
  {
    id: 'builtin:pressure-network-build',
    title: '压力管网建档（建网→加管→读→删）',
    intent: '压力管网 建网 加管 压力管 pressure network build',
    steps: [
      { method: 'listPressureNetworks', why: '没网时也会回 availablePartsLists（含 pipe/fitting/appurtenance 部件名）' },
      { method: 'createPressureNetwork', why: '先给 partsList 名；同名会 CONFLICT，建完回读 verified' },
      { method: 'addPressurePipe', why: '要 startPoint/endPoint（[x,y,z] 或 {x,y,z}），不带端点会报 INVALID_INPUT' },
      { method: 'getPressureNetworkInfo', why: '复核管/配件/附属设备数量（按 ObjectId 去重）' },
      { method: 'deletePressureNetwork', why: '删完回读 verified + remainingNetworkCount' },
    ],
    notes: '压力管长度 API 不暴露 → 按端点三维距离补，lengthSource=computed-from-endpoints；validate/setCover 目前诚实地报不可用。',
  },
  {
    id: 'builtin:big-export',
    title: '大图层/大选择集导出（带进度）',
    intent: '导出 大批量 六万 大图层 选择集 坐标表 export csv coordinates large',
    steps: [
      { method: 'getLayers', why: '先看有哪些图层' },
      { method: 'getEntitiesByLayer', why: 'includeTotal 默认 true → matchedCount 是该图层总量；按 nextOffset 翻页' },
      { method: 'saveSelection', why: '导出读的是选择集快照（_sel.json），必须先把选择存成快照' },
      { method: 'exportSelectionToFile', why: '带 operationId → getCivil3DHealth.operations 能看到 processed/total；结束回 complete/skippedCount' },
    ],
    notes: '导出结果是"选择集快照"不是实时选择；complete=false 表示有 handle 解析失败（看 skippedHandles）。',
  },
  {
    id: 'builtin:cogo-points',
    title: '地形点读取与导出',
    intent: '地形点 COGO 点 点号 点组 导出 points cogo',
    steps: [
      { method: 'listPointGroups', why: '点组名与点数' },
      { method: 'listCogoPoints', why: '按组过滤 + 分页（filterApplied/nextOffset/byteLimited 自证）' },
      { method: 'qtyPointCountByGroup', why: 'totalPoints 是去重点数，sumOfGroupCounts 才是各组相加' },
      { method: 'exportCogoPoints', why: '按字节预算截断（truncated/selectedCount/exportedCount）' },
    ],
    notes: '点组名拼错会列可用组名；不要拿 totalPoints 当“组内条数”用。',
  },
  {
    id: 'builtin:corridor-feature-lines',
    title: '廊道要素线提取（几何真相）',
    intent: '廊道 要素线 提取 三维多段线 feature line corridor 断面',
    steps: [
      { method: 'listCorridors', why: '拿廊道名' },
      { method: 'getCorridorFeatureLines', why: '按码取要素线（一次 2–3 个码，避免大结果被截断）' },
      { method: 'extractCorridorFeatureLines', why: '落成真实 3D 多段线；replaceExisting 处理已有产物' },
    ],
    notes: '要素线是几何真相，廊道曲面只作可视化 —— 量算要走装配断面 × region 长度。',
  },
  {
    id: 'builtin:surface-build',
    title: '曲面建模（点/等高线 → 曲面）',
    intent: '曲面 建模 建面 建曲面 加点 等高线 地形点建面 surface tin contours',
    steps: [
      { method: 'createSurface', why: '先建空曲面' },
      { method: 'addSurfacePointsFromFile', why: '大点数走文件通道（比逐条 addSurfacePoints 快得多）' },
      { method: 'addSurfaceContours', why: '2D 多段线按对象高程进曲面（weeding/max 距离必须 max>weeding>0）' },
      { method: 'getSurfaceStatistics', why: '面积/高程范围/点数；空曲面会回 0/null 而不是报错' },
    ],
    notes: '等高线距离参数顺序写错会抛 "maximumDistance should greater than weedingDistance"。',
  },
];

// ---------- 分词 / 打分 ----------
function normalize(text) {
  return String(text || '').toLowerCase();
}

function tokens(text) {
  const s = normalize(text);
  const out = new Set();
  for (const m of s.matchAll(/[a-z0-9_]{2,}/g)) out.add(m[0]);
  // 中文按二元组切（够用且不依赖词典）
  for (const m of s.matchAll(/[\u4e00-\u9fff]+/g)) {
    const run = m[0];
    if (run.length === 1) out.add(run);
    for (let i = 0; i + 1 < run.length; i++) out.add(run.slice(i, i + 2));
  }
  return out;
}

function overlapScore(qTokens, text) {
  if (!qTokens.size) return 0;
  const t = tokens(text);
  let hit = 0;
  for (const token of qTokens) if (t.has(token)) hit++;
  return hit / qTokens.size;
}

function searchBuiltin(qTokens, limit) {
  return BUILTIN.map((r) => {
    const bodyText = [r.intent, r.notes, ...r.steps.map((s) => s.method + ' ' + s.why)].join(' ');
    // 标题权重 ×2：标题命中说明"这就是你要做的事"，步骤文本命中只说明"沾边"
    const score = overlapScore(qTokens, r.title) * 2 + overlapScore(qTokens, bodyText);
    return {
      source: 'builtin',
      id: r.id,
      title: r.title,
      score: Math.round(score * 1000) / 1000,
      steps: r.steps,
      notes: r.notes,
    };
  })
    .filter((r) => r.score > 0)
    .map((r) => ({ ...r, score: Math.min(1, r.score / 2) }))
    .sort((a, b) => b.score - a.score)
    .slice(0, limit);
}

function searchRecorded(project, qTokens, limit) {
  if (!project) return [];
  let listed = [];
  try { listed = workflowStore.list(project, 200) || []; } catch { listed = []; }
  const scored = listed
    .map((w) => ({
      raw: w,
      score: overlapScore(qTokens, String(w.ask || '')) * 0.9 + (w.ok ? 0.15 : 0) + Math.min(0.05, (w.score || 0) / 2000),
    }))
    .filter((x) => x.score > 0.05)
    .sort((a, b) => b.score - a.score)
    .slice(0, limit);

  const out = [];
  for (const item of scored) {
    let full = null;
    try { full = workflowStore.get(project, item.raw.taskId); } catch { full = null; }
    const steps = ((full && full.steps) || [])
      .filter((s) => s && s.method && s.channel !== 'local')
      .slice(0, 12)
      .map((s) => ({ method: s.method, why: s.ok === false ? '上次这一步失败' : '本项目跑通过' }));
    out.push({
      source: 'recorded',
      id: 'recorded:' + item.raw.taskId,
      title: String(item.raw.ask || '').slice(0, 80) || ('已记录任务 ' + item.raw.taskId),
      score: Math.round(item.score * 1000) / 1000,
      steps,
      notes: '本项目历史跑通记录（可配合 replay 直接回放；ok=' + !!item.raw.ok + '，耗時ms=' + (item.raw.ms || '-') + '）',
      reuse: { taskId: item.raw.taskId, ok: !!item.raw.ok, ms: item.raw.ms || null, slots: item.raw.slots || 0 },
    });
  }
  return out;
}

function search(project, query, opts = {}) {
  const limit = Math.max(1, Math.min(10, Number(opts.limit) || 5));
  const qTokens = tokens(query);
  const builtin = searchBuiltin(qTokens, limit);
  const recorded = opts.includeRecorded === false ? [] : searchRecorded(project, qTokens, Math.max(2, Math.ceil(limit / 2)));
  // 2026-11-18: 本项目跑通过、且相关度够高（>=0.5）的记录小幅优先 —— 它能直接回放，
  // 比通用配方更省事；相关度低的记录不享受这个加成，避免"旧任务乱入"。
  const rank = (h) => h.score + (h.source === 'recorded' && h.score >= 0.5 ? 0.12 : 0);
  const hits = [...recorded, ...builtin]
    .sort((a, b) => rank(b) - rank(a))
    .slice(0, limit);
  return {
    query: String(query || ''),
    hits,
    builtinCount: BUILTIN.length,
    recordedCount: recorded.length,
    hint: hits.length
      ? '步骤里的方法名可直接用 cadCall 调；notes 里是已知坑，照着做别再踩一遍。'
      : '没有匹配到配方。改用 listMethods filter 找方法名，或直接描述任务让模型自己规划。',
  };
}

module.exports = { search, tokens, overlapScore, BUILTIN };
