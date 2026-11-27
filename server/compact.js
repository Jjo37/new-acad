// compact.js — 上下文压缩模块（2026-08-14，借鉴 DeepSeek Harness compaction-basic）
// 解决 llm-agent 多轮工具循环上下文膨胀 → 单轮推理慢/超时的问题
// 核心：token 压力触发 → 早期对话 LLM 摘要成结构化检查点 → 保留尾部原文
// 安全：压缩边界不跨越未完成的工具调用；摘要失败则降级不压缩
const http = require('http');
const https = require('https');
const { stripUnsupportedParams, isSamplingParamRejection, isResponsesRequired, dropSamplingParams, endpointFor } = require('./model-params.js');   // 2026-11-06: 采样参数 / 端点 能力层
const RESP = require('./responses-adapter.js');   // 2026-11-06: Responses API 适配（摘要请求也要能走新端点）

// 尾部保留条数（约 2 轮 + 当前 user）：压缩只动更早部分
const RETAIN_COUNT = 6;

// 摘要指令（作为最后一条 user 消息，前缀与主请求对齐 → 复用 KV 缓存）
const COMPACT_INSTRUCTION = [
  '你现在是上下文压缩引擎。把上面这段对话压缩成结构化检查点，让后续模型能无损续接工作。',
  '',
  '严格按下面的 Markdown 结构输出，顺序不变，用简短条目不要写长段落；空节写"(无)"，不允许删节。',
  '',
  '## 任务目标',
  '- [用户的原始与演进目标；关键原话照抄]',
  '',
  '## 关键技术事实',
  '- [涉及的图层/实体/文档/坐标系/方法等事实性信息]',
  '',
  '## 已完成动作',
  '- [已执行的步骤与结果，保留方法名/handle/文件路径/数值]',
  '',
  '## 错误与修复',
  '- [遇到的错误与解决方式，保留错误原文]',
  '',
  '## 待办',
  '- [明确要求但未完成的工作]',
  '',
  '## 当前状态',
  '- [检查点时刻正在进行的精确状态]',
  '',
  '## 下一步',
  '- [紧接着要做的单个动作，或"(无)"]',
  '',
  '## 关键上下文',
  '- [决策与理由、约束、用户偏好、继续所需的数据]',
  '',
  '规则：',
  '- 保留精确的路径、方法名、handle、命令、错误串、标识符、数值、函数签名。',
  '- 忠实保留用户反馈和明确纠正。',
  '- 不要提及本次压缩请求，不要输出检查点以外的内容。',
  '- 如果对话中已有检查点块，不要原样复制：保留仍然成立的事实，合并新信息成单一检查点。'
].join('\n');

// 检查点落地引导语（替换早期消息的那条 user 消息）
const CHECKPOINT_PREAMBLE =
  '【上下文检查点】以下为早期对话的自动摘要，视为已确认的背景信息，直接基于它继续任务，无需复述或确认。';

/** 无工具调用的纯文本 LLM 请求（摘要用；不动原 callLLM） */
function callPlain(cfg, messages, timeoutMs, signal) {
  // 2026-10-29: no vendor fallback (see llm-agent.callLLM) - unconfigured must fail loudly.
  if (!cfg.baseUrl) return Promise.reject(new Error('未配置模型服务商（baseUrl 为空）：无法压缩上下文'));
  if (!cfg.model) return Promise.reject(new Error('未配置模型名（model 为空）：无法压缩上下文'));
  const baseUrl = String(cfg.baseUrl).replace(/\/+$/, '');
  try { new URL(baseUrl + '/chat/completions'); } catch (e) { return Promise.reject(new Error('baseUrl 无效: ' + e.message)); }

  // 2026-11-06: 过能力层 —— 采样参数按模型剥离/改名，端点按模型选。
  // 摘要刻意**不指定思考深度**：压缩要的是快和省，思考档留给主链路。
  let params = stripUnsupportedParams(cfg, cfg.model, { model: cfg.model, messages, temperature: 0.2, max_tokens: 1500 });
  let endpoint = endpointFor(cfg, cfg.model);
  let heals = 0;

  const send = () => {
    if (endpoint === 'responses') {
      return postPlainJson(new URL(RESP.endpoint(baseUrl)), cfg.apiKey, RESP.buildRequest(params, {}), timeoutMs, signal)
        .then(RESP.toChatShape);
    }
    return postPlainJson(new URL(baseUrl + '/chat/completions'), cfg.apiKey, params, timeoutMs, signal);
  };

  const attempt = () => send().catch((err) => {
    if (endpoint === 'chat' && heals < 2 && isResponsesRequired(err)) {
      heals += 1;
      endpoint = 'responses';
      return attempt();
    }
    if (heals < 2 && isSamplingParamRejection(err)) {
      const { params: next, dropped } = dropSamplingParams(params);
      if (dropped.length) { heals += 1; params = next; return attempt(); }
    }
    throw err;
  });

  return attempt();
}

/** 发一次摘要请求（不改参数）。400 时把原始响应体挂到 error 上，供自愈判定。 */
function postPlainJson(u, apiKey, params, timeoutMs, signal) {
  const body = JSON.stringify(params);
  return new Promise((resolve, reject) => {
    const mod = u.protocol === 'https:' ? https : http;
    const req = mod.request({
      host: u.hostname, port: u.port || (u.protocol === 'https:' ? 443 : 80), path: u.pathname,
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': 'Bearer ' + (apiKey || ''),
        'Content-Length': Buffer.byteLength(body)
      }
    }, (res) => {
      let data = '';
      res.on('data', (c) => data += c);
      res.on('end', () => {
        if (res.statusCode >= 400) {
          const err = new Error('LLM API ' + res.statusCode + ': ' + data.slice(0, 300));
          err.statusCode = res.statusCode;
          err.apiBody = data;
          return reject(err);
        }
        try { resolve(JSON.parse(data)); } catch (e) { reject(new Error('摘要响应解析失败: ' + data.slice(0, 200))); }
      });
    });
    req.on('error', (e) => reject(new Error('摘要请求失败: ' + e.message)));
    req.setTimeout(timeoutMs || 60000, () => { req.destroy(); reject(new Error('摘要超时 (' + (timeoutMs || 60000) + 'ms)')); });
    if (signal) {
      if (signal.aborted) { req.destroy(); reject(new Error('任务已取消')); return; }
      signal.addEventListener('abort', () => { req.destroy(); reject(new Error('任务已取消')); }, { once: true });
    }
    req.write(body);
    req.end();
  });
}

/** 压缩末端：最后一个"配对完整且尾部保留 ≥ retainCount 条"的位置（exclusive）；无可压缩返回 -1 */
function findCompactEnd(messages, retainCount) {
  const pending = new Set();
  let best = -1;
  for (let i = 1; i < messages.length; i++) {
    const m = messages[i];
    if (!m || typeof m !== 'object') continue;
    if (m.role === 'assistant' && Array.isArray(m.tool_calls) && m.tool_calls.length) {
      for (const tc of m.tool_calls) { if (tc && tc.id) pending.add(tc.id); }
    } else if (m.role === 'tool' && m.tool_call_id) {
      pending.delete(m.tool_call_id);
    }
    if (pending.size === 0 && (messages.length - i - 1) >= retainCount) best = i + 1;
  }
  return best;
}

/**
 * 压缩早期消息：范围 [1, end) 摘要成一条检查点 user 消息，尾部原文保留。
 * @param {object} cfg llm 配置
 * @param {Array} messages 当前完整消息数组（含 system）
 * @param {AbortSignal} [signal]
 * @returns {Promise<Array|null>} 压缩后的新数组；不可压缩/失败返回 null
 */
async function compactMessages(cfg, messages, signal) {
  if (!cfg || !cfg.apiKey) return null;
  const end = findCompactEnd(messages, RETAIN_COUNT);
  // 可压缩范围太小（<4 条）→ 无意义
  if (end < 5) return null;
  const head = messages.slice(0, end); // system + 早期消息（将被摘要）
  const tail = messages.slice(end);
  if (tail.length < 2) return null; // 尾部必须有内容可继续

  // 摘要请求：复用 system 前缀 + 早期消息 + 指令结尾（前缀缓存复用）
  const sumMessages = [
    ...head,
    { role: 'user', content: COMPACT_INSTRUCTION }
  ];
  let summary = '';
  try {
    const resp = await callPlain(cfg, sumMessages, 90000, signal);
    const msg = resp && resp.choices && resp.choices[0] && resp.choices[0].message;
    summary = (msg && msg.content || '').trim();
  } catch (e) {
    // 摘要失败 → 降级不压缩（走原预警/硬停逻辑）
    return null;
  }
  if (!summary) return null;

  const checkpoint = { role: 'user', content: CHECKPOINT_PREAMBLE + '\n\n' + summary };
  return [messages[0], checkpoint, ...tail];
}

module.exports = { compactMessages };
