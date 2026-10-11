// cad-tools.js — llm-agent 的工具定义 + 执行器（形态 3 唯一工具：cadCall）
// 2026-08-06 S1 新增。纯新增模块，不依赖 relay 现有代码。
// 功能: 定义 OpenAI 兼容 tools schema（cadCall）+ 执行（HTTP POST :19876/tcp）

'use strict';

const http = require('http');

// 工具定义（OpenAI 兼容 function calling schema）
const CAD_CALL_TOOL = {
  type: 'function',
  function: {
    name: 'cadCall',
    description: '调用 C3D 插件的 TCP 方法（JSON-RPC）。先调 listMethods 拿方法清单再选方法，禁止猜方法名。方法执行结果会返回给 AI。',
    parameters: {
      type: 'object',
      properties: {
        method: {
          type: 'string',
          description: '插件方法名，例如 listMethods / getEntityInfo / createCircle。不确定时先调 listMethods。'
        },
        params: {
          type: 'object',
          description: '方法参数对象（与 JSON-RPC params 一致）。无参数传 {}。',
          additionalProperties: true
        }
      },
      required: ['method'],
      additionalProperties: false
    }
  }
};

/**
 * 执行 cadCall → HTTP POST relay /tcp 桥 → 插件 JSON-RPC 响应
 * @param {string} method 插件方法名
 * @param {object} params 参数
 * @param {number} timeoutMs 超时（默认 30s，与 relay /tcp 一致）
 * @returns {Promise<object>} 插件原始 JSON-RPC 响应（含 result 或 error）
 */
function cadCall(method, params = {}, timeoutMs = 30000, signal) {
  return new Promise((resolve, reject) => {
    const body = JSON.stringify({ jsonrpc: '2.0', id: 1, method, params: params || {} });
    const req = http.request({
      host: '127.0.0.1',
      port: 19876,
      path: '/tcp',
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) }
    }, (res) => {
      let data = '';
      res.on('data', (c) => data += c);
      res.on('end', () => {
        try {
          const parsed = JSON.parse(data);
          resolve(parsed); // 原样返回插件响应（result/error）
        } catch (e) {
          reject(new Error('响应解析失败: ' + data.slice(0, 300)));
        }
      });
    });
    req.on('error', (e) => reject(new Error('桥接请求失败: ' + e.message)));
    req.setTimeout(timeoutMs, () => { req.destroy(); reject(new Error('桥接超时 (' + timeoutMs + 'ms)')); });
    // 2026-08-07: 支持取消（AbortController）
    if (signal) {
      if (signal.aborted) { req.destroy(); reject(new Error('任务已取消')); return; }
      signal.addEventListener('abort', () => { req.destroy(); reject(new Error('任务已取消')); }, { once: true });
    }
    req.write(body);
    req.end();
  });
}

/**
 * 把插件响应格式化为 AI 可读的 tool 结果文本
 * - 成功: JSON 序列化 result
 * - 失败: 明确标注 error（AI 看到 error 应报告 [MISSING] 或修正参数）
 */
// 2026-08-07 P0: listMethods 分组摘要 + filter 过滤（消灭猜方法名烧轮次）
let methodsCache = null;
let methodsCacheTime = 0;   // 2026-08-07: 缓存时间戳（供预拉刷新判断）
const METHOD_GROUPS = [
  { key: '创建/导入', re: /^(create|add|insert|new|import)/ },
  { key: '查询/读取', re: /^(list|get|read|sample|query|find|export)/ },
  { key: '修改/编辑', re: /^(set|update|edit|change|resize|move|rotate|scale|mirror|copy|align|trim|extend|offset|break|join|explode|fillet|chamfer|array|stretch|assign|transform)/ },
  { key: '删除', re: /^delete/ },
  { key: '计算/分析', re: /^(calc|compute|analy|check|measure|sample|qty)/ },
  { key: '质检', re: /^qc/ },
  { key: '曲面', re: /surface/i },
  { key: '路线/纵断', re: /alignment|station|profile/i },
  { key: '廊道', re: /corridor/i },
  { key: '管网', re: /pipe|structure|pressure|hydraulic/i },
  { key: '放坡', re: /grading|featureline/i },
  { key: '地块/横断', re: /parcel|section/i },
  { key: '点/测量', re: /cogo|point|survey/i },
  { key: '图纸集/标注/样式', re: /sheet|label|dim|style/i },
  { key: '其他', re: /.*/ }
];
function formatMethodList(list, filter, descriptions) {
  const desc = (m) => (descriptions && descriptions[m]) ? ' — ' + descriptions[m] : '';
  if (filter) {
    const f = String(filter).toLowerCase();
    const hit = list.filter(m => m.toLowerCase().includes(f));
    if (hit.length === 0) {
      return '未找到包含 "' + filter + '" 的方法（全部 ' + list.length + ' 个）。该功能可能不存在：直接报 [MISSING] 或换关键词再查，禁止猜方法名。';
    }
    return '找到 ' + hit.length + ' 个匹配 "' + filter + '" 的方法：\n' + hit.map(m => m + desc(m)).join('\n');
  }
  // 分组摘要（每个方法归入第一个匹配组，避免重复）
  const groups = {};
  for (const m of list) {
    let g = '其他';
    for (const grp of METHOD_GROUPS) { if (grp.re.test(m)) { g = grp.key; break; } }
    (groups[g] = groups[g] || []).push(m);
  }
  const lines = Object.entries(groups).map(([k, v]) => '【' + k + '】' + v.length + '个: ' + v.slice(0, 8).map(m => m + desc(m)).join(', ') + (v.length > 8 ? ' …' : ''));
  return '方法总数: ' + list.length + '，按功能分组：\n' + lines.join('\n') +
    '\n\n精确查找方法名：调 listMethods 带 filter 参数（如 params={"filter":"surface"}），禁止猜方法名';
}

// 2026-08-07 Phase0: 结果截断+摘要, 防止大 JSON 撑爆上下文导致 LLM 变慢/超时
const FORMAT_MAX = 2000;   // 单结果最大注入字符
function formatResult(resp, method, params) {
  if (!resp) return '（无响应）';
  if (resp.error) return '插件返回错误: ' + JSON.stringify(resp.error);
  const r = resp.result ?? resp;
  // 2026-08-07 P0: listMethods 特殊处理（分组摘要 + filter 本地过滤 + 缓存）
  if (method === 'listMethods') {
    const list = Array.isArray(r) ? r : (r.methods || []);
    const descriptions = (!Array.isArray(r) && r.descriptions) ? r.descriptions : null;  // 2026-08-08: 高频方法描述
    if (list && list.length) {
      methodsCache = list;
      methodsCacheTime = Date.now();
      return formatMethodList(list, params && params.filter, descriptions);
    }
  }
  const text = JSON.stringify(r);
  if (text.length <= FORMAT_MAX) return text;
  // 列表类: 报条数 + 截断样例
  if (Array.isArray(r)) {
    return '数组共 ' + r.length + ' 项，已截断前若干项：\n' + text.slice(0, FORMAT_MAX) +
      '\n...（完整结果 ' + text.length + ' 字符，已截断。如需完整数据请换按需获取的方式）';
  }
  return text.slice(0, FORMAT_MAX) +
    '\n...（结果过长已截断，完整 ' + text.length + ' 字符。如需完整数据请换按需获取的方式）';
}

// 2026-08-07: mcpCall — 调 MCP 高层工具 (:3000/execute)。仅复合向导/帮助/标准类用（见 knowledge.md 通道原则）
// 注意: MCP 底层也是插件 :8080（b3dExec 同名方法），插件坏的方法 MCP 一样坏，不是替代品
function mcpCall(tool, params = {}, timeoutMs = 30000, signal) {
  return new Promise((resolve, reject) => {
    const body = JSON.stringify({ tool, parameters: params || {} });
    const req = http.request({
      host: '127.0.0.1',
      port: 3000,
      path: '/execute',
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) }
    }, (res) => {
      let data = '';
      res.on('data', (c) => data += c);
      res.on('end', () => {
        if (res.statusCode >= 400) {
          let parsed = null;
          try { parsed = JSON.parse(data); } catch (_) {}
          const msg = (parsed && parsed.error) ? JSON.stringify(parsed.error) : data.slice(0, 300);
          return reject(new Error('MCP ' + res.statusCode + ': ' + msg));
        }
        try { resolve(JSON.parse(data)); } catch (e) { reject(new Error('MCP 响应解析失败: ' + data.slice(0, 300))); }
      });
    });
    req.on('error', (e) => reject(new Error('MCP 请求失败: ' + e.message)));
    req.setTimeout(timeoutMs, () => { req.destroy(); reject(new Error('MCP 超时 (' + timeoutMs + 'ms)')); });
    // 2026-08-07: 支持取消（AbortController）
    if (signal) {
      if (signal.aborted) { req.destroy(); reject(new Error('任务已取消')); return; }
      signal.addEventListener('abort', () => { req.destroy(); reject(new Error('任务已取消')); }, { once: true });
    }
    req.write(body);
    req.end();
  });
}

const MCP_CALL_TOOL = {
  type: 'function',
  function: {
    name: 'mcpCall',
    description: '调用 MCP 高层工具（POST :3000/execute）。仅用于：复合/多步向导类（civil3d_workflow_* / civil3d_quantity_takeoff / civil3d_orchestrate）、帮助/文档/标准核查（civil3d_docs / civil3d_help / civil3d_standards_lookup）、参数复杂易错的方法。原子操作一律用 cadCall（TCP）。MCP 底层与 TCP 同源（都是插件 :8080），插件坏的方法 MCP 一样坏。',
    parameters: {
      type: 'object',
      properties: {
        tool: { type: 'string', description: 'MCP 工具名（如 civil3d_quantity_takeoff / civil3d_docs）' },
        parameters: { type: 'object', description: '工具参数对象', additionalProperties: true }
      },
      required: ['tool'],
      additionalProperties: false
    }
  }
};

// 2026-08-14 P1-3: mcpCall 工具名白名单——只放行只读/文档/标准/工作流向导类；orchestrate 等路由器不放行
const MCPCALL_ALLOW = new Set([
  'civil3d_health', 'civil3d_get_drawing_info', 'civil3d_docs', 'civil3d_help',
  'civil3d_standards_lookup', 'civil3d_list_tool_capabilities', 'civil3d_quantity_takeoff',
  'hank_get_selection', 'hank_get_entity_info', 'hank_get_layers', 'hank_measure_dist', 'hank_measure_area',
  'civil3d_workflow', 'civil3d_workflow_corridor_qc_report', 'civil3d_workflow_grading_surface_volume',
  'civil3d_workflow_surface_comparison_report', 'civil3d_workflow_data_shortcut_publish_sync',
  'civil3d_workflow_data_shortcut_reference_sync', 'civil3d_workflow_project_startup',
  'civil3d_workflow_project_reference_setup', 'civil3d_workflow_drawing_readiness_audit',
  'civil3d_workflow_feature_line_to_grading', 'civil3d_workflow_pipe_network_design',
  'civil3d_workflow_plan_production_publish', 'civil3d_workflow_qc_fix_and_verify',
]);
function getMethodsCache() { return methodsCache; }
function setMethodsCache(list) { methodsCache = list; methodsCacheTime = Date.now(); }
function isMethodsCacheFresh(ms) { return methodsCacheTime > 0 && (Date.now() - methodsCacheTime) < (ms || 300000); }
module.exports = { CAD_CALL_TOOL, cadCall, formatResult, mcpCall, MCP_CALL_TOOL, MCPCALL_ALLOW, getMethodsCache, setMethodsCache, isMethodsCacheFresh };
