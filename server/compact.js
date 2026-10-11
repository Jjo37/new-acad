// compact.js — 上下文压缩模块（2026-08-14，借鉴 DeepSeek Harness compaction-basic）
// 解决 llm-agent 多轮工具循环上下文膨胀 → 单轮推理慢/超时的问题
// 核心：token 压力触发 → 早期对话 LLM 摘要成结构化检查点 → 保留尾部原文
// 安全：压缩边界不跨越未完成的工具调用；摘要失败则降级不压缩
const http = require('http');
const https = require('https');

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

/** 估算 messages 体积（字节，与 JSON 请求体一致） */
function estimateBytes(messages) {
  try { return Buffer.byteLength(JSON.stringify(messages)); } catch { return 0; }
}

/**
 * 找安全压缩边界（exclusive 索引）：从 system 之后起，最后一个"工具调用配对完整"的位置。
 * 配对规则：assistant 发出 tool_calls 后，对应 tool 结果必须全部出现，边界才安全。
 * @returns {number} 边界索引（可压缩范围 = [1, boundary)）
 */
function findSafeBoundary(messages) {
  const pending = new Set();
  let lastSafe = 1; // system 之后
  for (let i = 1; i < messages.length; i++) {
    const m = messages[i];
    if (!m || typeof m !== 'object') continue;
    if (m.role === 'assistant' && Array.isArray(m.tool_calls) && m.tool_calls.length) {
      for (const tc of m.tool_calls) { if (tc && tc.id) pending.add(tc.id); }
    } else if (m.role === 'tool' && m.tool_call_id) {
      pending.delete(m.tool_call_id);
    }
    if (pending.size === 0) lastSafe = i + 1;
  }
  return lastSafe;
}

/** 无工具调用的纯文本 LLM 请求（摘要用；不动原 callLLM） */
function callPlain(cfg, messages, timeoutMs, signal) {
  return new Promise((resolve, reject) => {
    const baseUrl = (cfg.baseUrl || 'https://api.deepseek.com/v1').replace(/\/+$/, '');
    let u;
    try { u = new URL(baseUrl + '/chat/completions'); } catch (e) { return reject(new Error('baseUrl 无效: ' + e.message)); }
    const body = JSON.stringify({ model: cfg.model || 'deepseek-chat', messages, temperature: 0.2, max_tokens: 1500 });
    const mod = u.protocol === 'https:' ? https : http;
    const req = mod.request({
      host: u.hostname, port: u.port || (u.protocol === 'https:' ? 443 : 80), path: u.pathname,
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': 'Bearer ' + (cfg.apiKey || ''),
        'Content-Length': Buffer.byteLength(body)
      }
    }, (res) => {
      let data = '';
      res.on('data', (c) => data += c);
      res.on('end', () => {
        if (res.statusCode >= 400) return reject(new Error('LLM API ' + res.statusCode + ': ' + data.slice(0, 300)));
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

module.exports = { estimateBytes, findSafeBoundary, compactMessages };
