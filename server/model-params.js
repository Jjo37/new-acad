'use strict';
// model-params.js — 模型能力约束层（2026-11-06）
//
// 一个文件的职责：**别把 OpenAI 兼容当成统一协议**。各家/各代模型在四件事上不同：
//   ① 采样参数是否接受（temperature 等）        → RULES[].omit
//   ② 工具调用走哪个端点（Chat Completions / Responses） → RULES[].endpoint
//   ③ 思考深度怎么表达（参数名、档位、能不能关）  → THINKING_STYLES
//   ④ 服务端拒绝时怎么自愈                       → isSamplingParamRejection / isResponsesRequired
//
// 实测到的真实报错（都在下面有对应判据）：
//   Moonshot Kimi : invalid temperature: only 1 is allowed for this model
//   OpenAI 推理系 : Unsupported value/parameter: 'temperature' ...
//   OpenAI 新端点 : GPT-6 Astra / 6.1 Sol require the Responses API for tool calling
//
// 铁律（对齐 MEMORY「不许硬编码判据」）：
//   1. 每条规则必须带 source（官方文档 URL + 查证日期）。凭印象加 = 造故障。
//   2. 不确定就**不发**那个参数 —— 可选参数省略永远合法；发了可能整轮 400。
//   3. 名单只是"省一次往返"的优化，真正兜底的是自愈；名单过期不该导致功能挂掉。

// ---------- ① 采样参数 + ② 端点 ----------
const RULES = [
  {
    id: 'moonshot-kimi',
    provider: 'kimi',
    hostPattern: /(^|\.)moonshot\.(cn|ai)$/i,
    modelPattern: /^kimi-/i,
    omit: ['temperature'],
    reason: 'Moonshot 把 temperature 固定：kimi-k3 = 1.0；kimi-k2.7-code = 1.0；kimi-k2.6 思考 1.0 / 非思考 0.6。传其他值返回 invalid temperature 400。',
    source: 'https://platform.kimi.ai/docs/api/models-overview（2026-11-06 查证）',
  },
  {
    id: 'openai-reasoning',
    provider: 'openai',
    hostPattern: /^api\.openai\.com$/i,
    modelPattern: /^(o[1-9]|gpt-[5-9])/i,
    omit: ['temperature'],
    // 2026-11-06: o1/gpt-5+ 不再接受 max_tokens，必须改名（不然后台摘要会 400）
    rename: { max_tokens: 'max_completion_tokens' },
    reason: 'OpenAI 推理系拒绝 temperature：Unsupported value/parameter: temperature。只有 reasoning_effort=none 的部分型号接受，我们不传该参数，所以一律省略。另外这批模型不接受 max_tokens，须改用 max_completion_tokens。',
    source: 'https://community.openai.com/t/gpt-5-models-temperature/1337957 · https://github.com/LibreChat-AI/LibreChat/issues/10737（2026-11-06 查证）',
  },
  {
    id: 'openai-responses-required',
    provider: 'openai',
    hostPattern: /^api\.openai\.com$/i,
    // 官方 function-calling 指南点名这两个必须走 Responses；luna / 老 sol 走 chat + none 即可
    modelPattern: /^(gpt-6\.1-sol|gpt-6-astra)/i,
    endpoint: 'responses',
    omit: ['temperature'],
    rename: { max_tokens: 'max_completion_tokens' },
    reason: 'GPT-6 Astra 与 GPT-6.1 Sol 在 Chat Completions 上不支持工具调用，必须走 Responses API；且二者都没有 none 档可退。',
    source: 'https://developers.openai.com/api/docs/guides/function-calling · https://developers.openai.com/api/docs/models/gpt-6-sol（2026-11-06 查证）',
  },
  {
    id: 'openrouter-reasoning',
    hostPattern: /(^|\.)openrouter\.ai$/i,
    modelPattern: /^(openai\/gpt-[5-9]|anthropic\/claude-(opus-4\.[7-9]|sonnet-[5-9]))/i,
    omit: ['temperature'],
    reason: '同一批推理/新代模型经 OpenRouter 中转时采样约束不变；Claude Sonnet 5.5 还额外拒绝强制 tool_choice（必须发 auto）。',
    source: 'https://openrouter.ai/docs/cookbook/evaluate-and-optimize/model-migrations/claude-5-5 · https://www.digitalapplied.com/blog/which-ai-models-still-accept-temperature（2026-11-06 查证）',
  },
];

// ---------- ③ 思考深度 ----------
// style:
//   'reasoning_effort'        顶层 reasoning_effort
//   'thinking+reasoning_effort'  thinking.type + reasoning_effort
//   'responses'               Responses 的 reasoning.effort（由适配层写入）
// canDisable=false 的模型传关闭会直接报错（GLM-5.3 明确写传 disabled 请求失败）
const THINKING_STYLES = [
  {
    id: 'openai-effort',
    provider: 'openai',
    hostPattern: /^api\.openai\.com$/i,
    modelPattern: /^(o[1-9]|gpt-[5-9])/i,
    style: 'reasoning_effort',
    efforts: ['minimal', 'low', 'medium', 'high', 'xhigh', 'max'],
    canDisable: true,   // 部分型号支持 none；不支持时服务端会拒绝，由自愈兜
    reason: 'gpt-6.1-sol 起没有 none/minimal；max 仅 Responses API 可用。档位按模型不同，传递前已归一化。',
    source: 'https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning（2026-11-06 查证）',
  },
  {
    id: 'kimi-effort',
    provider: 'kimi',
    hostPattern: /(^|\.)moonshot\.(cn|ai)$/i,
    modelPattern: /^kimi-/i,
    style: 'reasoning_effort',
    efforts: ['low', 'high', 'max'],
    canDisable: false,  // K3 始终思考
    reason: 'kimi-k3 始终思考，reasoning_effort 仅 low/high/max，默认 max。',
    source: 'https://platform.kimi.ai/docs/api/models-overview（2026-11-06 查证）',
  },
  {
    id: 'glm-effort',
    provider: 'zhipu',
    hostPattern: /(^|\.)bigmodel\.cn$/i,
    modelPattern: /^glm-5/i,
    style: 'thinking+reasoning_effort',
    efforts: ['low', 'high', 'max'],
    canDisable: false,  // glm-5.3 传 disabled 直接失败
    reason: 'GLM-5.3 强制思考（thinking.type 只接受 enabled，传 disabled 请求失败），reasoning_effort 仅 low/high/max，默认 max。',
    source: 'https://docs.bigmodel.cn/cn/guide/models/text/glm-5.3（2026-11-06 查证）',
  },
  {
    id: 'deepseek-effort',
    provider: 'deepseek',
    hostPattern: /(^|\.)deepseek\.com$/i,
    modelPattern: /^deepseek/i,
    style: 'thinking+reasoning_effort',
    efforts: ['none', 'low', 'high', 'max'],
    canDisable: true,
    reason: 'DeepSeek 用 thinking.type=enabled/disabled 开关，reasoning_effort 取 none/low/high/max，默认 high。',
    source: 'https://api-docs.deepseek.com/api/create-chat-completion（2026-11-06 查证）',
  },
];

// 会被服务端拒绝的采样参数全集（省略时按此表逐项删）
const SAMPLING_PARAMS = ['temperature', 'top_p', 'presence_penalty', 'frequency_penalty'];

// 统一档位（面板上给用户选的），与各家具体值通过 resolveEffort 归一化
const THINKING_LEVELS = ['auto', 'off', 'low', 'medium', 'high', 'max'];

function hostOf(cfg) {
  try {
    return new URL(String((cfg && cfg.baseUrl) || '')).hostname;
  } catch (_) {
    return '';
  }
}

function hit(rule, provider, host, name) {
  const byProvider = !!rule.provider && provider === rule.provider;
  const byHost = !!rule.hostPattern && !!host && rule.hostPattern.test(host);
  const byModel = !rule.modelPattern || rule.modelPattern.test(name);
  return (byProvider || byHost) && byModel;
}

/** 命中哪些采样/端点规则 */
function matchRules(cfg, model) {
  const provider = String((cfg && cfg.provider) || '').trim().toLowerCase();
  const host = hostOf(cfg);
  const name = String(model || (cfg && cfg.model) || '');
  return RULES.filter((r) => hit(r, provider, host, name));
}

/** 命中哪条思考深度规则（取第一条命中的） */
function matchThinking(cfg, model) {
  const provider = String((cfg && cfg.provider) || '').trim().toLowerCase();
  const host = hostOf(cfg);
  const name = String(model || (cfg && cfg.model) || '');
  return THINKING_STYLES.find((r) => hit(r, provider, host, name)) || null;
}

/**
 * 该模型需要走哪个端点（chat | responses）。
 * 规则表只覆盖"已知必须"的；未知的靠 isResponsesRequired 自愈。
 */
function endpointFor(cfg, model) {
  const hits = matchRules(cfg, model);
  for (const r of hits) if (r.endpoint === 'responses') return 'responses';
  return 'chat';
}

/**
 * 按静态规则剥掉不支持的参数 + 改名不兼容的参数名。无副作用（浅拷贝）。
 * rename 语义：源键存在且目标键不存在时才改名（不覆盖调用方显式给的新名字）。
 */
function stripUnsupportedParams(cfg, model, params) {
  const hits = matchRules(cfg, model);
  if (!hits.length) return params;
  const omit = new Set();
  const renames = {};
  for (const r of hits) {
    for (const k of r.omit || []) omit.add(k);
    if (r.rename) for (const k of Object.keys(r.rename)) renames[k] = r.rename[k];
  }
  if (!omit.size && !Object.keys(renames).length) return params;
  const out = Object.assign({}, params);
  for (const k of Object.keys(renames)) {
    const to = renames[k];
    if (Object.prototype.hasOwnProperty.call(out, k) && !Object.prototype.hasOwnProperty.call(out, to)) {
      out[to] = out[k];
      delete out[k];
    }
  }
  for (const k of omit) delete out[k];
  return out;
}

/** 命中说明（诊断/测试用） */
function explain(cfg, model, params) {
  const p = params || {};
  return matchRules(cfg, model)
    .map((r) => ({
      id: r.id,
      endpoint: r.endpoint || 'chat',
      omitted: (r.omit || []).filter((k) => Object.prototype.hasOwnProperty.call(p, k)),
      source: r.source,
      reason: r.reason,
    }))
    .filter((x) => x.omitted.length > 0 || x.endpoint === 'responses');
}

// ---------- 思考深度：统一档位 → 各家参数 ----------

/** 就近归一化：目标模型不支持该档时取最接近的支持档 */
function resolveEffort(thinking, efforts) {
  if (!thinking || thinking === 'auto') return null;
  const allowed = Array.isArray(efforts) && efforts.length ? efforts : [];
  if (!allowed.length) return null;
  if (allowed.indexOf(thinking) >= 0) return thinking;
  const order = ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'];
  const want = order.indexOf(thinking);
  if (want < 0) return null;
  for (let d = 1; d < order.length; d++) {
    const down = order[want - d];
    if (down && allowed.indexOf(down) >= 0) return down;
  }
  for (let d = 1; d < order.length; d++) {
    const up = order[want + d];
    if (up && allowed.indexOf(up) >= 0) return up;
  }
  return null;
}

/**
 * 计算本次请求要追加的思考参数（chat 路径用）。
 * @param {object} cfg
 * @param {string} model
 * @param {string} thinking 统一档位 auto|off|low|medium|high|max
 * @returns {{params: object, applied: string|null, note: string}}
 *   applied=null 表示"按服务端默认，什么都不发"
 */
function buildThinkingParams(cfg, model, thinking) {
  const level = String(thinking || 'auto').toLowerCase();
  if (level === 'auto' || level === '') return { params: {}, applied: null, note: 'auto（不发思考参数）' };

  const rule = matchThinking(cfg, model);
  if (!rule) return { params: {}, applied: null, note: '该模型没有思考深度参数，已忽略' };

  if (level === 'off') {
    if (!rule.canDisable) {
      return { params: {}, applied: null, note: '该模型强制思考，不能关闭（未发送任何参数）' };
    }
    if (rule.style === 'thinking+reasoning_effort') {
      return { params: { thinking: { type: 'disabled' } }, applied: 'off', note: 'thinking.type=disabled' };
    }
    return { params: {}, applied: null, note: '该模型用档位表达开关，关闭=不指定（走默认）' };
  }

  const applied = resolveEffort(level, rule.efforts);
  if (!applied) return { params: {}, applied: null, note: '档位无法映射到该模型，已忽略' };

  if (rule.style === 'thinking+reasoning_effort') {
    return {
      params: { thinking: { type: 'enabled' }, reasoning_effort: applied },
      applied,
      note: 'thinking.type=enabled + reasoning_effort=' + applied,
    };
  }
  return { params: { reasoning_effort: applied }, applied, note: 'reasoning_effort=' + applied };
}

// ---------- ④ 自愈判据 ----------

/**
 * 服务端是否明确因为「采样参数」拒绝了这次请求。
 * 只有明确命中才自愈 —— 避免把上下文超限、工具配对错误也重试一遍。
 */
function isSamplingParamRejection(err) {
  if (!err) return false;
  const status = err.statusCode;
  if (status !== 400 && status !== 422) return false;
  const body = String(err.apiBody || err.message || '');
  if (!/temperature|top_p/i.test(body)) return false;
  return /(invalid|unsupported|not\s+support|does\s+not\s+support|only\s+[\d.]+\s+is\s+allowed|不支持|无效)/i.test(body);
}

/**
 * 服务端是否在说「工具调用请走 Responses API」。
 * 判据要窄：必须同时提到端点/协议替换意图，不能把任何含 tools 的 400 都当端点问题。
 */
function isResponsesRequired(err) {
  if (!err) return false;
  if (err.statusCode !== 400) return false;
  const body = String(err.apiBody || err.message || '');
  if (/responses api/i.test(body)) return true;
  if (/chat\s*completions/i.test(body) && /(not\s+support|require|unsupported|不支持)/i.test(body)) return true;
  return false;
}

/** 去掉所有采样参数（自愈重试用） */
function dropSamplingParams(params) {
  const out = Object.assign({}, params || {});
  const dropped = [];
  for (const k of SAMPLING_PARAMS) {
    if (Object.prototype.hasOwnProperty.call(out, k)) { delete out[k]; dropped.push(k); }
  }
  return { params: out, dropped };
}

module.exports = {
  RULES, THINKING_STYLES, THINKING_LEVELS, SAMPLING_PARAMS,
  hostOf, matchRules, matchThinking, endpointFor, stripUnsupportedParams, explain,
  resolveEffort, buildThinkingParams,
  isSamplingParamRejection, isResponsesRequired, dropSamplingParams,
};
