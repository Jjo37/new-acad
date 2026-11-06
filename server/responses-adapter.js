'use strict';
// responses-adapter.js — OpenAI Responses API ⇄ Chat Completions 形状适配（2026-11-06）
//
// 为什么存在：
//   OpenAI 从 GPT-5.4 起收紧 Chat Completions 的工具调用能力，到 GPT-6 Astra / 6.1 Sol
//   直接要求「工具调用必须走 Responses API」：
//     developers.openai.com/api/docs/guides/function-calling
//     "GPT-6 Astra and GPT-6.1 Sol require the Responses API for tool calling."
//   我们的 agent 整条链路都是 Chat Completions + tools(cadCall)，换端点等于换协议。
//
// 设计原则（重要）：
//   本文件只做**形状翻译**，不碰主循环。callLLM 把 Responses 的返回**伪装成 Chat Completions**
//   （choices[0].message.content / .tool_calls），handleMessage 一行都不用改。
//   这样 Chat Completions 那条老路径的代码保持字节级不变 —— 老用户零风险。
//
// 不做的事：不解流式（我们全程非流式），不管内置工具（web_search 等），不管会话状态
//   （store:false，我们自己的 messages 数组才是真相）。

// ---------- 请求方向 ----------

/** content 可能是字符串 / 数组（text + image_url）→ Responses 的 input 内容数组 */
function contentToInputParts(content) {
  if (typeof content === 'string') return [{ type: 'input_text', text: content }];
  if (!Array.isArray(content)) return [];
  const parts = [];
  for (const c of content) {
    if (!c || typeof c !== 'object') continue;
    if (c.type === 'text' && typeof c.text === 'string') {
      parts.push({ type: 'input_text', text: c.text });
    } else if (c.type === 'image_url') {
      const url = c.image_url && (c.image_url.url || c.image_url);
      if (url) parts.push({ type: 'input_image', image_url: url });
    } else if (typeof c.text === 'string') {
      parts.push({ type: 'input_text', text: c.text });
    }
  }
  return parts;
}

/** 纯文本化（assistant 文本、tool 输出都用它 —— 这两处不需要多模态） */
function contentToText(content) {
  if (typeof content === 'string') return content;
  if (!Array.isArray(content)) return '';
  return content
    .map((c) => (c && typeof c === 'object' ? (typeof c.text === 'string' ? c.text : '') : String(c || '')))
    .join('');
}

/**
 * chat messages → Responses 的 instructions + input[]
 * 映射规则：
 *   system            → instructions（合并）
 *   user              → { role:'user', content:[input_text|input_image] }
 *   assistant 文本     → { role:'assistant', content:[{type:'output_text'}] }
 *   assistant.tool_calls → 每条一个 { type:'function_call', call_id, name, arguments }
 *   tool              → { type:'function_call_output', call_id, output }
 */
function toResponsesInput(messages) {
  const instructions = [];
  const input = [];
  for (const m of messages || []) {
    if (!m || typeof m !== 'object') continue;
    if (m.role === 'system' || m.role === 'developer') {
      const t = contentToText(m.content);
      if (t) instructions.push(t);
      continue;
    }
    if (m.role === 'user') {
      input.push({ role: 'user', content: contentToInputParts(m.content) });
      continue;
    }
    if (m.role === 'assistant') {
      const t = contentToText(m.content);
      if (t) input.push({ role: 'assistant', content: [{ type: 'output_text', text: t }] });
      for (const tc of m.tool_calls || []) {
        const fn = (tc && tc.function) || {};
        input.push({
          type: 'function_call',
          // 必须回填 call_id：Responses 靠它把结果配对回调用
          call_id: tc.id || tc.call_id || ('call_' + input.length),
          name: fn.name || '',
          arguments: typeof fn.arguments === 'string' ? fn.arguments : JSON.stringify(fn.arguments || {}),
        });
      }
      continue;
    }
    if (m.role === 'tool') {
      input.push({
        type: 'function_call_output',
        call_id: m.tool_call_id || m.id || '',
        output: contentToText(m.content),
      });
    }
  }
  return { instructions: instructions.join('\n\n'), input };
}

/** chat 的工具 schema（{type,function:{name,…}}）→ Responses 的扁平 schema */
function toResponsesTools(tools) {
  if (!Array.isArray(tools)) return [];
  const out = [];
  for (const t of tools) {
    if (!t || typeof t !== 'object') continue;
    const fn = t.function || t;
    if (!fn || !fn.name) continue;
    const item = { type: 'function', name: fn.name, parameters: fn.parameters || { type: 'object', properties: {} } };
    if (fn.description) item.description = fn.description;
    // strict 需要 schema 完全闭合，我们现有 schema 未必满足 → 显式 false，别让服务端拿默认值猜
    if (typeof fn.strict === 'boolean') item.strict = fn.strict;
    out.push(item);
  }
  return out;
}

/**
 * chat 请求体对象 → Responses 请求体对象
 * @param {object} p  chat 形状的请求参数（model/messages/tools/tool_choice/temperature/max_tokens…）
 * @param {object} [opt] { thinking: 'auto|off|low|medium|high|max', targetEfforts: {...} }
 */
function buildRequest(p, opt) {
  const params = p || {};
  const { instructions, input } = toResponsesInput(params.messages);
  const body = { model: params.model, input, store: false };

  if (instructions) body.instructions = instructions;

  const tools = toResponsesTools(params.tools);
  if (tools.length) {
    body.tools = tools;
    const choice = params.tool_choice;
    body.tool_choice = (typeof choice === 'string') ? choice : 'auto';
  }

  // 采样参数：只透传规则层放行的（推理模型已被剥掉 temperature）
  if (params.temperature !== undefined) body.temperature = params.temperature;
  if (params.top_p !== undefined) body.top_p = params.top_p;

  // 输出上限：chat 的 max_tokens / max_completion_tokens → Responses 的 max_output_tokens
  const cap = params.max_output_tokens !== undefined ? params.max_output_tokens
    : params.max_completion_tokens !== undefined ? params.max_completion_tokens
      : params.max_tokens;
  if (cap !== undefined) body.max_output_tokens = cap;

  // 思考深度
  const effort = resolveEffort(opt && opt.thinking, opt && opt.targetEfforts);
  if (effort) body.reasoning = { effort };

  return body;
}

/** 把统一档位归一化到目标模型支持的档位（做不到就不传，走服务端默认） */
function resolveEffort(thinking, targetEfforts) {
  if (!thinking || thinking === 'auto' || thinking === 'off') return null;
  const allowed = Array.isArray(targetEfforts) && targetEfforts.length
    ? targetEfforts
    : ['low', 'medium', 'high'];
  if (allowed.indexOf(thinking) >= 0) return thinking;
  // 就近降级：先往下找更省的档，再往上
  const order = ['minimal', 'low', 'medium', 'high', 'xhigh', 'max'];
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

// ---------- 响应方向 ----------

/**
 * Responses 的返回 → 伪装成 Chat Completions 的 choices/usage
 * 认清三类 output 项：reasoning（丢弃但计数用途）/ message（正文）/ function_call（工具调用）
 */
function toChatShape(json) {
  const res = json || {};
  const items = Array.isArray(res.output) ? res.output : [];
  let text = '';
  let refusal = '';
  const toolCalls = [];

  for (const it of items) {
    if (!it || typeof it !== 'object') continue;
    if (it.type === 'message') {
      for (const c of it.content || []) {
        if (!c || typeof c !== 'object') continue;
        if (c.type === 'output_text' && typeof c.text === 'string') text += c.text;
        else if (c.type === 'refusal' && typeof c.refusal === 'string') refusal += c.refusal;
        else if (typeof c.text === 'string') text += c.text;
      }
    } else if (it.type === 'function_call') {
      toolCalls.push({
        // chat 侧用 tc.id 配对 tool 消息；Responses 的配对键是 call_id → 用它
        id: it.call_id || it.id || ('call_' + toolCalls.length),
        type: 'function',
        function: {
          name: it.name || '',
          arguments: typeof it.arguments === 'string' ? it.arguments : JSON.stringify(it.arguments || {}),
        },
      });
    }
  }

  const message = { role: 'assistant', content: text || (refusal ? refusal : null) };
  if (toolCalls.length) message.tool_calls = toolCalls;

  let finish = 'stop';
  if (toolCalls.length) finish = 'tool_calls';
  else if (res.status === 'incomplete') finish = 'length';

  const usage = res.usage
    ? {
      prompt_tokens: res.usage.input_tokens || 0,
      completion_tokens: res.usage.output_tokens || 0,
      total_tokens: res.usage.total_tokens || ((res.usage.input_tokens || 0) + (res.usage.output_tokens || 0)),
    }
    : undefined;

  const out = {
    id: res.id || '',
    object: 'chat.completion',
    model: res.model || '',
    choices: [{ index: 0, message, finish_reason: finish }],
    // 保留原始返回：排查时能看到 reasoning 摘要等 chat 形状装不下的东西
    _responsesRaw: res,
  };
  if (usage) out.usage = usage;
  return out;
}

/** baseUrl（…/v1）→ Responses 端点 */
function endpoint(baseUrl) {
  const base = String(baseUrl || '').replace(/\/+$/, '');
  return base + '/responses';
}

module.exports = {
  contentToInputParts, contentToText, toResponsesInput, toResponsesTools,
  buildRequest, toChatShape, resolveEffort, endpoint,
};
