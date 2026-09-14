// 2026-08-07 崩溃兜底: 未捕获异常落盘到 relay-crash.log 后退出, 由 relay-launcher v2 守护自动重启
process.on('uncaughtException', (e) => {
  const line = '[' + new Date().toISOString() + '] uncaughtException: ' + (e && e.stack || e) + '\n';
  try { require('fs').appendFileSync(__dirname + '/relay-crash.log', line); } catch (_) {}
  console.error(line.trim());
  process.exit(1);
});
process.on('unhandledRejection', (r) => {
  const line = '[' + new Date().toISOString() + '] unhandledRejection: ' + (r && r.stack || r) + '\n';
  try { require('fs').appendFileSync(__dirname + '/relay-crash.log', line); } catch (_) {}
  console.error(line.trim());
  process.exit(1);
});

const http = require('http');
const fs = require('fs');
const path = require('path');
const CAD_SESSION_KEY = 'agent:main:cad:panel';             // CAD 专属会话

// 2026-08-06 S2: llm 模式（形态 3）——发布版不依赖 OpenClaw，relay 直接调 LLM
const { handleMessage, clearActivePlan, hasActivePlan } = require('./llm-agent.js');
const llmHistory = [];                                     // llm 模式会话历史（内存，重启清空）

// 2026-08-12: 日志时间戳修复——toISOString() 是 UTC, 国内用户看差 8 小时。统一用本地时间
function fmtLocal(ts) {
  const d = ts || new Date();
  const p2 = (x) => String(x).padStart(2, '0');
  return d.getFullYear() + '-' + p2(d.getMonth() + 1) + '-' + p2(d.getDate()) + ' ' + p2(d.getHours()) + ':' + p2(d.getMinutes()) + ':' + p2(d.getSeconds());
}
function fmtLocalHMS(ts) {
  const d = ts || new Date();
  const p2 = (x) => String(x).padStart(2, '0');
  return p2(d.getHours()) + ':' + p2(d.getMinutes()) + ':' + p2(d.getSeconds());
}
function log(tag, msg) {
  const line = '[' + fmtLocalHMS() + '][' + tag + '] ' + msg;
  console.log(line);
  try { require('fs').appendFileSync(__dirname + '/relay.log', line + '\n'); } catch(_) {}
}

// ---------- 统一配置入口 (config.json，环境变量可覆盖) ----------
function loadConfig() {
  try {
    const p = path.join(__dirname, '..', 'config.json');
    if (fs.existsSync(p)) {
      let raw = fs.readFileSync(p, 'utf8');
      if (raw.charCodeAt(0) === 0xFEFF) raw = raw.slice(1);   // 剥离 UTF-8 BOM（Windows 编辑器可能写入）
      return JSON.parse(raw);
    }
  } catch(e) { log('CFG', 'config.json 读取失败: ' + e.message); }
  return {};
}
const cfg = loadConfig();
const port = cfg.relayPort || 19876;
const token = process.env.RELAY_TOKEN || cfg.relayToken || '';
const qqSessionKey = process.env.RELAY_SESSION_KEY || cfg.relaySessionKey || '';  // QQ 会话
const BRIDGE_PORT = cfg.bridgePort || 8080;
let RELAY_MODE = (cfg && cfg.relayMode) || 'llm';   // 2026-08-07: let 支持热更新   // 'openclaw' | 'llm'

// ---------- LLM 服务商预设（面板下拉用） ----------
const LLM_PRESETS = [
  { id: 'deepseek',  name: 'DeepSeek',     baseUrl: 'https://api.deepseek.com/v1',  models: ['deepseek-chat','deepseek-reasoner','deepseek-v4-flash','deepseek-v4-pro'] },
  { id: 'openai',    name: 'OpenAI',       baseUrl: 'https://api.openai.com/v1',    models: ['gpt-4o-mini','gpt-4o','gpt-4.1'] },
  { id: 'qwen',      name: '通义千问',     baseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1', models: ['qwen-plus','qwen-max','qwen-turbo'] },
  { id: 'kimi',      name: 'Kimi',         baseUrl: 'https://api.moonshot.cn/v1',   models: ['moonshot-v1-8k','moonshot-v1-32k','moonshot-v1-128k'] },
  { id: 'zhipu',     name: '智谱GLM',      baseUrl: 'https://open.bigmodel.cn/api/paas/v4', models: ['glm-4-flash','glm-4-plus','glm-4-air'] },
  { id: 'minimax',   name: 'MiniMax',      baseUrl: 'https://api.minimax.chat/v1',  models: ['abab6.5s-chat','abab6.5g-chat'] },
  { id: 'openrouter',name: 'OpenRouter',   baseUrl: 'https://openrouter.ai/api/v1', models: ['deepseek/deepseek-chat','openai/gpt-4o-mini','anthropic/claude-3.5-sonnet'] },
  { id: 'xai',       name: 'xAI',          baseUrl: 'https://api.x.ai/v1',          models: ['grok-2-latest','grok-3'] },
  { id: 'custom',    name: '自定义',       baseUrl: '',                              models: [] },
];

// 热更新 llm 配置: 面板 POST /llm/config 时调用，写盘 + 更新内存（立即生效，无需重启）
function updateLlmConfig(newLlm) {
  if (!cfg.llm) cfg.llm = {};
  if (newLlm.provider !== undefined) cfg.llm.provider = String(newLlm.provider);
  if (newLlm.baseUrl !== undefined)  cfg.llm.baseUrl = String(newLlm.baseUrl);
  if (newLlm.model !== undefined)    cfg.llm.model = String(newLlm.model);
  if (newLlm.apiKey !== undefined)   cfg.llm.apiKey = String(newLlm.apiKey);
  if (newLlm.maxTurns !== undefined) {
    // 2026-08-10: 轮数可配；0 = 无限模式（面板∞按钮），NaN/负数用默认 20
    const n = Number(newLlm.maxTurns);
    cfg.llm.maxTurns = (Number.isFinite(n) && n > 0) ? n : (n === 0 ? 0 : 20);
  }
  if (newLlm.timeoutMs !== undefined) cfg.llm.timeoutMs = Number(newLlm.timeoutMs) || 60000;
  cfg.relayMode = 'llm';
  RELAY_MODE = 'llm';        // 2026-08-07: 同步热更新  // 配置 LLM 即强制 llm 模式
  // 写盘（保留其他配置）
  try {
    const p = path.join(__dirname, '..', 'config.json');
    fs.writeFileSync(p, JSON.stringify(cfg, null, 2), 'utf8');
  } catch(e) { log('CFG', 'config.json 写入失败: ' + e.message); }
  log('CFG', 'LLM 配置已更新: ' + (cfg.llm.provider || '?') + '/' + (cfg.llm.model || '?'));
}

let progressCache = null;   // 2026-08-07: 实时进度（/status 轮询 + SSE）
let currentAbort = null;       // 2026-08-07: 取消真中断（AbortController，/cancel 时 abort）
let selectionCache = null;     // 2026-08-07: 选中集跨图缓存（docName+handles+完整几何，切图不丢）
const SEL_CACHE_FILE = path.join(__dirname, '..', 'exchange', 'selection-cache.json');
try { if (fs.existsSync(SEL_CACHE_FILE)) selectionCache = JSON.parse(fs.readFileSync(SEL_CACHE_FILE, 'utf8')); } catch (_) {}
let ws = null;
let wsReqIdCounter = 0;
let cadSessionReady = false;  // CAD 会话是否已创建+初始化
const replies = [];
// inbox 落盘持久化（P1-8）：relay 重启不丢消息
const INBOX_FILE = path.join(__dirname, 'relay-inbox.json');
let inbox = [];        // { id, text, time, status: 'pending'|'claimed'|'done', claimedAt?, sent? }
try { if (fs.existsSync(INBOX_FILE)) { inbox = JSON.parse(fs.readFileSync(INBOX_FILE, 'utf8')); if (!Array.isArray(inbox)) throw new Error('not array'); } } catch(_) {
    try { const bad = INBOX_FILE + '.corrupt-' + Date.now(); fs.renameSync(INBOX_FILE, bad); log('INBOX', 'inbox 文件损坏, 已备份到 ' + bad); } catch(_) {}
    inbox = [];
  }
let msgIdCounter = inbox.reduce((mx, m) => { const n = parseInt(m.id, 36); return isNaN(n) ? mx : Math.max(mx, n); }, 0);
const CLAIM_TIMEOUT = 60000;
let sseClients = [];    // 通用 SSE 连接列表
let replySseClients = []; // 回复 SSE 连接列表

// ---------- inbox 防抖落盘 ----------
let inboxSaveTimer = null;
function saveInbox() {
  clearTimeout(inboxSaveTimer);
  inboxSaveTimer = setTimeout(() => {
    try { fs.writeFileSync(INBOX_FILE, JSON.stringify(inbox)); } catch(e) { log('INBOX', 'save failed: ' + e.message); }
  }, 300);
}

// ========== WS RPC 辅助 ==========
function wsReq(method, params) {
  return new Promise((resolve, reject) => {
    if (!ws) return reject(new Error('not connected'));
    const id = 'r' + (++wsReqIdCounter);
    const handler = (e) => {
      try {
        const m = JSON.parse(e.data);
        if (m.type === 'res' && m.id === id) {
          ws.removeEventListener('message', handler);
          if (m.ok) resolve(m.payload);
          else reject(new Error(m.error?.message || 'unknown error'));
        }
      } catch(_) {}
    };
    ws.addEventListener('message', handler);
    ws.send(JSON.stringify({type:'req', id, method, params}));
    setTimeout(() => { ws.removeEventListener('message', handler); reject(new Error('TIMEOUT')); }, 10000);
  });
}

// ---------- 回收超时的 claimed 消息 ----------
setInterval(() => {
  const now = Date.now();
  let changed = false;
  for (const msg of inbox) {
    if (msg.status === 'processing') continue; // 处理中豁免回收(2026-08-12 C1)
    if (msg.status === 'claimed' && now - msg.claimedAt > CLAIM_TIMEOUT) {
      msg.status = 'pending';
      delete msg.claimedAt;
      changed = true;
      log('INBOX', 'Timeout, re-queued: ' + msg.id);
    }
  }
  // 2026-08-07: pending 超 10 分钟 → 过期取消（防僵尸队列）
  for (const m of inbox) {
    if (m.status === 'processing') continue; // 处理中豁免过期(2026-08-12 C1)
    if (m.status === 'pending' && (now - new Date(m.time).getTime()) > 600000) {
      m.status = 'cancelled'; m.cancelledAt = new Date().toISOString(); changed = true;
      log('INBOX', 'Expired pending: ' + m.id);
    }
  }
  const done = inbox.filter(m => m.status === 'done' || m.status === 'cancelled');   // 2026-08-07: cancelled 一并清理
  if (done.length > 100) {
    let toRemove = done.length - 100;   // 2026-08-07 修复: 原 const+toRemove-- = TypeError, 异机反复崩溃根因
    for (let i = 0; i < inbox.length && toRemove > 0;) {
      if (inbox[i].status === 'done' || inbox[i].status === 'cancelled') { inbox.splice(i, 1); toRemove--; changed = true; }  // 2026-08-07: cancelled 一并回收 toRemove--; changed = true; }
      else i++;
    }
  }
  if (changed) saveInbox();
}, 15000);

// ---------- SSE 推送 ----------
function sseBroadcast(event, data) {
  const msg = 'event: ' + event + '\ndata: ' + JSON.stringify(data) + '\n\n';
  sseClients = sseClients.filter(c => {
    try { c.write(msg); return true; }
    catch(e) { return false; }
  });
}
function sseBroadcastReply(text) {
  const msg = 'event: reply\ndata: ' + JSON.stringify({text, time: new Date().toISOString()}) + '\n\n';
  replySseClients = replySseClients.filter(c => {
    try { c.write(msg); return true; }
    catch(e) { return false; }
  });
  sseBroadcast('reply', {text, time: new Date().toISOString()});
}

function pushReply(text, messageId) {
  replies.push({ text, time: new Date().toISOString() });
  if (replies.length > 200) replies.splice(0, replies.length - 200);  // 2026-08-07: 防内存无限增长
  sseBroadcastReply(text);
  if (messageId) {
    const m = inbox.find(x => x.id === messageId);
    if (m) { m.status = 'done'; saveInbox(); }
  } else {
    // 自动回推（AI 主动回复，无 messageId）：匹配最近一条已发送的 pending 消息标 done，
    // 避免 inbox 状态永久累积（修复: 8 条历史消息全 pending 的问题）
    for (let i = inbox.length - 1; i >= 0; i--) {
      const m = inbox[i];
      if (m.status === 'pending' && m.sent) { m.status = 'done'; saveInbox(); break; }
    }
  }
  log('REPLY', text.slice(0, 80));
}

// ---------- 自动捕获 AI 回复（仅 CAD session 推面板） ----------
function handleSessionMessage(sk, msg) {
  // 只处理 CAD 会话的回复，QQ 会话不再推面板（避免污染）
  if (sk !== CAD_SESSION_KEY) return;
  if (msg.role === 'assistant' && msg.content) {
    let text = '';
    if (typeof msg.content === 'string') {
      text = msg.content;
    } else if (Array.isArray(msg.content)) {
      for (const item of msg.content) {
        if (item.type === 'text' && item.text) text += item.text;
      }
    }
    if (text && !text.includes('NO_REPLY')) {
      text = text.trim();
      if (text) {
        pushReply(text);
        log('GW_AUTO', '[CAD] ' + text.slice(0, 80));
      }
    }
  }
}

// ========== 创建 + 初始化 CAD 会话 ==========
async function setupCadSession() {
  try {
    log('CAD', 'Creating CAD session: ' + CAD_SESSION_KEY);
    await wsReq('sessions.create', {
      key: CAD_SESSION_KEY,
      agentId: 'main'
    });
    log('CAD', 'CAD session created ✅');

    // 订阅 CAD session 消息
    await wsReq('sessions.messages.subscribe', { key: CAD_SESSION_KEY });
    log('CAD', 'Subscribed to CAD session ✅');

    // 注入系统指令：告诉 AI 这是 CAD 面板会话
    const sop = `你当前在 CAD 面板会话（channel=cad-panel）。
🔴🔴 最高红线（违反=失职，立即停止手头工作）：
   **禁止修改/创建/删除 D:\new-acad 下任何文件**——包括 src\*.cs 源码、tools\ 脚本、server\ 代码、文档、DLL、.git 内容。
   **禁止运行任何编译命令**（dotnet build / npm run build / 打包脚本）。
   你只有调用插件 TCP 方法的权限（走 :19876/tcp 桥）。
   发现功能缺失/方法名错误/需要新方法 → 在回复中写明需求（方法名+参数+期望行为+原因），主会话汉克负责改代码+编译+验证。未经 review 的代码不得生效。
   你的每次文件操作都会被 git 审计，越权修改会被发现并回滚。

规则：
1. 回复尽量简短，直接给结果
2. 调用 C3D 插件：HTTP POST http://127.0.0.1:19876/tcp，body 为 JSON-RPC：{"jsonrpc":"2.0","id":1,"method":"方法名","params":{...}}
   ⚠️ 必须用这个 HTTP 桥，不要直接连 8080、不要用 Invoke-RestMethod 发原始 HTTP 到 8080（插件只认 TCP JSON-RPC，HTTP 请求会把队列卡死）
   ⚠️ 不要用 executeCommand 传交互式命令（_BOX/_PLINE/_CIRCLE 等，会被白名单拦截）
   ⚠️ **不确定方法名/参数时，先调 listMethods 拿实时方法清单（336 个，反射自动扫描），再选方法。禁止猜方法名**
   ⚠️ **有依赖关系的操作必须串行**：等前一步返回成功后再发下一步（如 建路线→设桩号→建纵断面 是依赖链，禁止并发发；并发会排队卡死 25s 超时）
3. 常用 API（全部可用，直接调）：
   - 画图: createBox{x,y,z,length,width,height} / createCylinder{x,y,z,radius,height} / createSphere{x,y,z,radius} / createCone{x,y,z,radius,height} / createWedge{x,y,z,length,width,height} / createTorus{x,y,z,majorRadius,minorRadius} / createPolyline{points:[{x,y}],closed} / createCircle{center:[x,y],diameter} / createText / createMText
   - 查: getEntityInfo{handle} / getSelection / getDrawingInfo / measureDist{x1,y1,x2,y2} / measureArea{handle}
   - 编辑: moveEntity / copyEntity / deleteEntity / rotateEntity / scaleEntity / setEntityColor / offsetEntity / breakEntity / explodeEntity
   - 图层: createLayer / layerOff / layerFreeze / layerLock / layerUnlock / layerIsolate / layerMatch
   - 3D变换: move3d / rotate3d / mirror3d
   - 曲面: createSurface / addSurfacePoints / getSurface
4. 不需要汇报过程，直接执行
5. 不需要说"正在操作..."之类的话
6. 别带 [PANEL] 标记
7. （红线重申）禁止直接修改插件源码、禁止直接编译、禁止自行替换 DLL、禁止修改任何项目文件。
   发现功能缺失/需要新方法 → 先尝试现有 API 完成；确实需要改代码 → 在回复中写明需求，汉克负责改代码+编译+验证（未经 review 的代码不得生效）

回复格式示例：
✅ 圆已创建 handle=xxxx
✅ 颜色已改红色
✅ 选中 N 个圆`;

    await wsReq('sessions.send', {
      key: CAD_SESSION_KEY,
      message: '/system ' + sop
    });
    log('CAD', 'SOP injected ✅');
    cadSessionReady = true;
    // 会话就绪后再补发积压消息（修复: 原在会话创建前调用，全新 relay + 积压时会失败）
    flushPending();

    // 等一会再获取图面状态
    setTimeout(async () => {
      try {
        const net = require('net');
        const c = new net.Socket();
        c.on('error', () => { log('CAD', 'C3D offline, skip drawing state'); });
        const req = JSON.stringify({jsonrpc:'2.0', id:1, method:'getDrawingInfo', params:{}}) + '\n';
        c.connect(BRIDGE_PORT, '127.0.0.1', () => c.write(req));
        let b = '';
        c.on('data', d => b += d.toString());
        c.on('close', () => {
          try {
            const info = JSON.parse(b).result;
            const statusMsg = `当前图面：${info.drawingName}，单位：${info.linearUnits}，曲面：${info.objectCounts.surfaces} 个`;
            ws.send(JSON.stringify({type:'req', id:'s_init2', method:'sessions.send',
              params:{key: CAD_SESSION_KEY, message: statusMsg}}));
            log('CAD', 'Drawing state injected');
          } catch(e) {}
        });
        setTimeout(() => c.destroy(), 3000);
      } catch(e) { log('CAD', 'State inject failed: ' + e.message); }
    }, 2000);
  } catch(e) {
    log('CAD', 'Setup failed: ' + e.message);
    // 可能是 sessions.create 不支持 - 直接发消息试试
    cadSessionReady = true;
  }
}

// ---------- OpenClaw 网关 ----------
if (token) {
  const connectGW = () => {
    ws = new WebSocket('ws://127.0.0.1:18789');
    ws.onmessage = (e) => {
      const m = JSON.parse(e.data);
      if (m.event === 'connect.challenge') {
        ws.send(JSON.stringify({type:'req', id:'c1', method:'connect', params:{
          minProtocol:4, maxProtocol:4,
          client:{id:'gateway-client', version:'1.0', platform:'node', mode:'backend'},
          role:'operator', scopes:['operator.admin','operator.write','operator.read'],
          auth:{token}
        }}));
        return;
      }
      if (m.event === 'ping') {
        ws.send(JSON.stringify({type:'event', event:'pong'}));
        return;
      }
      // connect 响应 → 订阅两个 session
      if (m.type === 'res' && m.id === 'c1') {
        if (m.ok) {
          log('GW', 'Connected');
          // 订阅 session 事件
          ws.send(JSON.stringify({type:'req', id:'sub0', method:'sessions.subscribe', params:{}}));
          // 创建 + 初始化 CAD 会话（不再订阅 QQ session）
          setupCadSession();
        } else {
          log('GW', 'Connect failed: ' + (m.error?.message || 'unknown'));
        }
        return;
      }
      // 订阅响应
      if (m.type === 'res' && m.id === 'sub0') {
        if (m.ok) log('GW', 'Subscribed to session events ✅');
        else log('GW', 'Subscribe sub0 failed: ' + (m.error?.message || 'unknown'));
        return;
      }
      // 自动捕获 AI 回复（仅 CAD session）
      if (m.event === 'session.message') {
        const p = m.payload || m.params || {};
        const sk = p.sessionKey || p.key || '';
        if (sk === CAD_SESSION_KEY) {
          const msg = p.message || p;
          handleSessionMessage(sk, msg);
        }
        return;
      }
      // 仅记录重要事件
      if (m.type === 'event' && ['sessions.changed','session.tool'].includes(m.event)) {
        const p = m.payload || {};
        if (p.sessionKey === CAD_SESSION_KEY) log('CAD_EVT', m.event + ' ' + (p.phase || p.reason || ''));
      }
    };
    ws.onclose = () => { ws = null; log('GW', 'Lost, retry 30s'); setTimeout(connectGW, 30000); };
    ws.onerror = () => { ws = null; log('GW', 'Unreachable'); };
  };
  connectGW();
} else log('GW', 'OpenClaw not configured');

// ---------- 转发消息到 CAD session（面板消息直连 CAD，不再走 QQ） ----------
function forwardToCad(text, entry) {
  if (!ws || ws.readyState !== 1 /* OPEN */) return false;
  ws.send(JSON.stringify({type:'req', id:'s' + Date.now() + Math.random().toString(36).slice(2,6), method:'sessions.send',
    params:{key: CAD_SESSION_KEY, message: text}}));
  log('FWD', '-> [CAD] ' + text.slice(0, 80));
  if (entry) { entry.sent = true; saveInbox(); }
  return true;
}

// ---------- 网关恢复后补发断开期间积压的消息（P0-2: 消息不丢） ----------
function flushPending() {
  if (!ws || ws.readyState !== 1 /* OPEN */) return;
  let n = 0;
  for (const msg of inbox) {
    if (msg.status === 'pending' && !msg.sent) {
      msg.sent = true;
      ws.send(JSON.stringify({type:'req', id:'s' + Date.now() + Math.random().toString(36).slice(2,6), method:'sessions.send',
        params:{key: CAD_SESSION_KEY, message: msg.text}}));
      n++;
    }
  }
  if (n > 0) { saveInbox(); log('FWD', 'Flush ' + n + ' queued message(s) to CAD session'); }
}

// ---------- HTTP ----------
let llmChain = Promise.resolve();   // 2026-08-07: llm 消息串行队列（防并发）



async function loadSelectionSnapshot() {
            let cur = null;
            try {
              const sp = path.join(__dirname, '..', 'exchange', '_out', '_sel.json');
              if (fs.existsSync(sp)) {
                const raw = JSON.parse(fs.readFileSync(sp, 'utf8'));
                if (raw && raw.docName) {
                  const items = Array.isArray(raw.items)
                    ? raw.items.map(i => (i && i.handle) ? String(i.handle) : String(i)).filter(Boolean)
                    : [];
                  cur = { docName: String(raw.docName), handles: items };
                }
              }
            } catch (_) {}
            // 调插件拿完整几何（C3D 在线时），成功且非空 → 更新跨图缓存
            try {
              const detail = await require('./cad-tools.js').cadCall('getSelectionDetail', {});
              if (detail && detail.result && detail.result.count > 0 && Array.isArray(detail.result.entities)) {
                selectionCache = {
                  docName: (detail.result.docName || (cur && cur.docName) || ''),
                  handles: detail.result.entities.map(e => (e && e.handle) ? String(e.handle) : '').filter(Boolean),
                  entities: detail.result.entities,
                  savedAt: new Date().toISOString()
                };
                try { saveSelectionCache(); } catch (_) {}
                log('SEL', 'Cache updated: ' + selectionCache.docName + ' (' + selectionCache.entities.length + ' 完整几何)');
              }
            } catch (_) {}
            if (cur && cur.handles.length > 0) {
              log('SEL', 'Snapshot: ' + cur.docName + ' (' + cur.handles.length + ')');
              return cur;
            }
            if (selectionCache && selectionCache.entities && selectionCache.entities.length > 0) {
              log('SEL', 'Using cached: ' + selectionCache.docName + ' (' + selectionCache.entities.length + ', fromCache)');
              return { docName: selectionCache.docName, handles: selectionCache.handles || [], entities: selectionCache.entities, fromCache: true };
            }
            return cur;
          }

// 2026-08-07: llm 消息处理（串行执行；取消/错误统一收口，finally 清 currentAbort）
async function processLlmMessage(entry, msg, lcfg) {
  if (entry && entry.status === 'cancelled') { log('LLM', '跳过已取消消息 ' + (entry.id || '')); return; }
  // 2026-08-12 C1/M1: 标记 processing——豁免 10 分钟过期回收, 长任务不误杀
  if (entry) { entry.status = 'processing'; entry.processingAt = new Date().toISOString(); saveInbox(); }
  if (!lcfg.apiKey) {
    pushReply('⚠️ 未配置 AI：请编辑 config.json 的 llm 段（provider/apiKey/model），或用 install.ps1 第 11 步配置。', entry.id);
    return;
  }
  const selSnapshot = await loadSelectionSnapshot();   // 移到 apiKey 检查后（无 key 不白跑插件查询）
  // 2026-08-07: listMethods 预拉缓存（AI filter 秒回，替代静态速查表）
  try {
    const mt = require('./cad-tools.js');
    if (!mt.isMethodsCacheFresh()) {
      const lm = await require('./cad-tools.js').cadCall('listMethods', {});
      if (lm && lm.result && lm.result.methods) mt.setMethodsCache(lm.result.methods);
      log('PLAN', 'Methods cache refreshed (' + ((lm && lm.result && lm.result.methods) || []).length + ')');
    }
  } catch (_) {}
  // 2026-08-07: 规模感知 → 规划注入（大规模任务强制先出计划，替代"复杂才商量"的失效规则）
  let planNote = '';
  try {
    const selCount = selSnapshot && selSnapshot.handles ? selSnapshot.handles.length : 0;
    const textLow = String(msg).toLowerCase();
    const bulkHints = ['csv', '坐标', '批量', '全部', '所有', '大量', '高程点', '加点', '很多', '每个', '逐'];
    const isBulk = selCount >= 20 || bulkHints.some(k => textLow.includes(k));
    if (isBulk) {
      planNote = '\n\n⚠️ 本任务疑似大规模（选中 ' + selCount + ' 个对象/涉及大量数据）。执行前先输出 1-2 行计划：处理方式（批量方法/数组参数/CSV 文件通道）+ 预计轮数。禁止逐个调用（撞轮次上限）。数据量大（>500）优先文件通道（如 addSurfacePointsFromFile 读 CSV 一次性加点）。';
      log('PLAN', 'Bulk task, plan injected (' + selCount + ' sel)');
    }
  } catch (_) {}
  const abortCtrl = new AbortController();
  currentAbort = abortCtrl;
  try {
    const onProgress = (p) => {
      if (p && p.text) {
        progressCache = { text: String(p.text).slice(0, 80), time: Date.now() };
        try { sseBroadcast('progress', { text: progressCache.text }); } catch (_) {}
        log('PROG', progressCache.text);
      }
    };
    const r = await handleMessage(lcfg, llmHistory, msg, selSnapshot, onProgress, abortCtrl.signal, planNote);
    llmHistory.push({ role: 'user', content: msg });
    if (r.reply) llmHistory.push({ role: 'assistant', content: r.reply });
    if (llmHistory.length > 24) llmHistory.splice(1, llmHistory.length - 24);
    if (r.reply && r.reply.includes('[MISSING]')) {
      try {
        const fsx = require('fs');
        const dir = path.join(__dirname, '..', 'exchange');
        if (!fsx.existsSync(dir)) fsx.mkdirSync(dir, { recursive: true });
        fsx.appendFileSync(path.join(dir, 'missing-report.jsonl'),
          JSON.stringify({ time: fmtLocal(), msg, reply: r.reply, calls: r.calls }) + '\n');
      } catch(_) {}
    }
    if (entry.status === 'cancelled' || r.cancelled) { log('LLM', 'Reply dropped (cancelled): ' + entry.id); return; }
    pushReply(r.reply || '⚠️ AI 未返回内容，请重试', entry.id);   // 空回复兜底
  } catch (e) {
    log('LLM', 'Error: ' + e.message);
    if (entry.status === 'cancelled') { log('LLM', 'Error reply dropped (cancelled): ' + entry.id); return; }
    // 2026-08-10: 图片/视觉相关错误友好翻译（模型/服务商不支持看图时如实告知 + 给替代方案，不预判不拦截）
    const em = String(e.message || '');
    if (/image_url|vision|multimodal|image/i.test(em)) {
      pushReply('⚠️ 当前模型/服务商不支持看图（API 拒绝了图片输入）。可换支持视觉的模型（如 gpt-4o / qwen-vl / glm-4v），或把图片里的文字内容直接发我。', entry.id);
    } else {
      pushReply('⚠️ AI 处理出错: ' + em, entry.id);
    }
  } finally {
    currentAbort = null;   // 统一清理（原 cancelled 提前 return 泄漏）
    if (entry && entry.status !== 'cancelled') { entry.status = 'done'; saveInbox(); }
  }
}

const server = http.createServer((req, res) => {
  // 2026-08-07 安全修复: 来源校验——仅本机(127.0.0.1/localhost)或无 Origin 放行
  const reqOrigin = req.headers.origin || '';
  if (reqOrigin && !/^https?:\/\/(127\.0\.0\.1|localhost)(:\d+)?$/i.test(reqOrigin)) {
    res.writeHead(403, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ ok: false, error: 'forbidden origin' }));
    return;
  }
  res.setHeader('Access-Control-Allow-Origin', reqOrigin || '*');


  // ---- GET /llm/options — 服务商+模型预设（面板下拉） ----
  if (req.method === 'GET' && req.url === '/llm/options') {
    const current = (cfg.llm && cfg.llm.provider) ? { provider: cfg.llm.provider, model: cfg.llm.model || '', baseUrl: cfg.llm.baseUrl || '', maxTurns: (cfg.llm.maxTurns === undefined ? 20 : cfg.llm.maxTurns) } : null;
    res.end(JSON.stringify({ presets: LLM_PRESETS, current, locale: (cfg && cfg.locale) || 'auto' }));
    return;
  }

  // ---- GET /locale — 当前面板语言（auto|zh-CN|en-US） ----
  if (req.method === 'GET' && req.url === '/locale') {
    res.end(JSON.stringify({ ok: true, locale: (cfg && cfg.locale) || 'auto' }));
    return;
  }

  // ---- POST /locale — 面板切换界面语言（写 config.json，热生效；面板随后自行重建 UI） ----
  if (req.method === 'POST' && req.url === '/locale') {
    let body = '';
    req.on('data', c => body += c);
    req.on('end', () => {
      try {
        const j = body ? JSON.parse(body) : {};
        const loc = String(j.locale || 'auto').trim();
        if (!['auto', 'zh-CN', 'en-US'].includes(loc)) { res.end(JSON.stringify({ ok: false, error: 'locale must be auto|zh-CN|en-US' })); return; }
        cfg.locale = loc;
        try { fs.writeFileSync(path.join(__dirname, '..', 'config.json'), JSON.stringify(cfg, null, 2), 'utf8'); } catch (e) { log('CFG', 'config.json 写入失败: ' + e.message); }
        log('CFG', '界面语言已切换: ' + loc);
        res.end(JSON.stringify({ ok: true, locale: loc }));
      } catch (e) { res.end(JSON.stringify({ ok: false, error: e.message })); }
    });
    return;
  }

  // ---- POST /llm/config — 面板保存模型+key（热更新，立即生效） ----
  if (req.method === 'POST' && req.url === '/llm/config') {
    let body = '';
    req.on('data', c => body += c);
    req.on('end', () => {
      try {
        const j = JSON.parse(body);
        if (!j.provider || !j.model) { res.end(JSON.stringify({ok:false, error:'provider/model 必填'})); return; }
        // 2026-08-10: apiKey 可为空（面板仅改轮数等时）；空且无历史配置才报错，有则保留原 key
        if (!j.apiKey && !(cfg.llm && cfg.llm.apiKey)) { res.end(JSON.stringify({ok:false, error:'请先配置 API Key（首次必填）'})); return; }
        // 自定义服务商需 baseUrl
        if (j.provider === 'custom' && !j.baseUrl) { res.end(JSON.stringify({ok:false, error:'自定义服务商需填 baseUrl'})); return; }
        updateLlmConfig(j);
        res.end(JSON.stringify({ok:true, mode:'llm', model: (cfg.llm.provider + '/' + cfg.llm.model)}));
      } catch(e) { res.end(JSON.stringify({ok:false, error:e.message})); }
    });
    return;
  }
  // ---- POST /project/create — 建立项目文件夹（2026-08-17）：创建项目目录并切换为当前项目 ----
  if (req.method === 'POST' && req.url === '/project/create') {
    let body = '';
    req.on('data', c2 => body += c2);
    req.on('end', () => {
      try {
        const j = body ? JSON.parse(body) : {};
        const raw = (j.project !== undefined ? String(j.project) : '').trim();
        if (!raw) { res.end(JSON.stringify({ ok: false, error: 'project name is empty' })); return; }
        const safe = sanitizeProjectName(raw);
        const dir = path.join(__dirname, '..', 'exchange', 'projects', safe);
        fs.mkdirSync(path.join(dir, 'agent-work'), { recursive: true });
        currentProject = safe;
        cfg.project = safe;
        try { fs.writeFileSync(path.join(__dirname, '..', 'config.json'), JSON.stringify(cfg, null, 2), 'utf8'); } catch (_) {}
        try {
          const f = selCacheFile();
          selectionCache = fs.existsSync(f) ? JSON.parse(fs.readFileSync(f, 'utf8')) : null;
        } catch (_) { selectionCache = null; }
        log('PROJ', '项目已建立: ' + safe);
        res.end(JSON.stringify({ ok: true, project: safe, dir }));
      } catch (e) { res.end(JSON.stringify({ ok: false, error: e.message })); }
    });
    return;
  }
  // ---- GET /projects — 已有项目名列表（2026-08-17）----
  if (req.method === 'GET' && req.url === '/projects') {
    try {
      const root = path.join(__dirname, '..', 'exchange', 'projects');
      const list = [];
      if (fs.existsSync(root)) {
        for (const e of fs.readdirSync(root, { withFileTypes: true })) {
          if (e.isDirectory()) list.push(e.name);
        }
      }
      list.sort((a, b) => a.localeCompare(b, 'zh-CN'));
      res.end(JSON.stringify({ ok: true, projects: list }));
    } catch (e) { res.end(JSON.stringify({ ok: false, error: e.message })); }
    return;
  }
  // ---- POST /project/clear — 清理项目（2026-08-17）：删整个项目目录，若为当前项目则回全局 ----
  if (req.method === 'POST' && req.url === '/project/clear') {
    let body = '';
    req.on('data', c2 => body += c2);
    req.on('end', () => {
      try {
        const j = body ? JSON.parse(body) : {};
        const name = (j.project !== undefined ? String(j.project) : currentProject).trim();
        if (!name) { res.end(JSON.stringify({ ok: false, error: 'no project to clear' })); return; }
        const dir = path.join(__dirname, '..', 'exchange', 'projects', sanitizeProjectName(name));
        if (fs.existsSync(dir)) fs.rmSync(dir, { recursive: true, force: true });
        // 清理的是当前项目 → 回全局
        if (currentProject && sanitizeProjectName(name) === sanitizeProjectName(currentProject)) {
          currentProject = '';
          cfg.project = '';
          try { fs.writeFileSync(path.join(__dirname, '..', 'config.json'), JSON.stringify(cfg, null, 2), 'utf8'); } catch (_) {}
          try { selectionCache = fs.existsSync(SEL_CACHE_FILE) ? JSON.parse(fs.readFileSync(SEL_CACHE_FILE, 'utf8')) : null; } catch (_) { selectionCache = null; }
        }
        log('PROJ', '项目已清理: ' + name);
        res.end(JSON.stringify({ ok: true, cleared: name }));
      } catch (e) { res.end(JSON.stringify({ ok: false, error: e.message })); }
    });
    return;
  }
  // ---- GET/POST /project — 项目上下文（2026-08-17）----
  if (req.method === 'GET' && req.url === '/project') {
    const dir = currentProject
      ? path.join(__dirname, '..', 'exchange', 'projects', sanitizeProjectName(currentProject))
      : path.join(__dirname, '..', 'exchange', 'agent-work');
    res.end(JSON.stringify({ ok: true, project: currentProject, dir }));
    return;
  }
  if (req.method === 'POST' && req.url === '/project') {
    let body = '';
    req.on('data', c2 => body += c2);
    req.on('end', () => {
      try {
        const j = JSON.parse(body);
        const name = (j.project !== undefined ? String(j.project) : '').trim();
        // 空名 = 回全局模式（不 sanitize 成 'unnamed'）；非空才清洗特殊字符
        currentProject = name ? sanitizeProjectName(name) : '';
        cfg.project = currentProject;
        // 切换项目：重载该项目的 selection-cache（无项目=全局）
        try {
          const f = selCacheFile();
          selectionCache = fs.existsSync(f) ? JSON.parse(fs.readFileSync(f, 'utf8')) : null;
        } catch (_) { selectionCache = null; }
        // 写盘
        try {
          const cf = path.join(__dirname, '..', 'config.json');
          fs.writeFileSync(cf, JSON.stringify(cfg, null, 2), 'utf8');
        } catch(e) { log('CFG', 'config.json 写入失败: ' + e.message); }
        log('PROJ', '项目已切换: ' + (currentProject || '(全局)'));
        res.end(JSON.stringify({ ok: true, project: currentProject }));
      } catch(e) { res.end(JSON.stringify({ ok: false, error: e.message })); }
    });
    return;
  }

  // ---- GET /status ----
  if (req.method === 'GET' && req.url === '/status') {
    const pending = inbox.filter(m => m.status === 'pending').length;
    const claimed = inbox.filter(m => m.status === 'claimed').length;
    const processing = inbox.filter(m => m.status === 'processing').length; // 2026-08-12 M1: processing 替代 claimed 显示处理中
    const lastReply = replies.length > 0 ? (replies[replies.length - 1].text || '') : '';
    let state = 'idle';
    if (pending > 0) state = 'queued';
    else if (claimed > 0 || processing > 0) state = 'thinking'; // 2026-08-12 M1
    // 当前模型: llm 模式读 config llm 段；openclaw 模式显示汉克（OpenClaw 驱动）
    const modelLabel = RELAY_MODE === 'llm'
      ? ((cfg.llm && (cfg.llm.model || cfg.llm.provider)) ? (cfg.llm.provider + '/' + cfg.llm.model) : '未配置')
      : '汉克(OpenClaw)';
    const prog = progressCache && (Date.now() - progressCache.time < 20000) ? progressCache.text : '';
    res.end(JSON.stringify({state, pending, claimed, processing, lastReply, cadSessionReady, mode: RELAY_MODE, model: modelLabel, progress: prog}));
    return;
  }

  // ---- GET /health ----
  if (req.method === 'GET' && req.url === '/health') {
    const p = inbox.filter(m => m.status === 'pending').length;
    const c = inbox.filter(m => m.status === 'claimed').length;
    const proc = inbox.filter(m => m.status === 'processing').length; // 2026-08-12 M1
    res.end(JSON.stringify({ ok:true, gateway:ws!==null,
      gatewayMode: token ? 'openclaw' : 'universal',
      gatewayReason: !token ? 'no_token' : (ws ? 'connected' : 'unreachable'),
      gatewayHint: !token ? '未配置 relayToken：编辑 config.json 填 relayToken，或 setx RELAY_TOKEN <token>' : (ws ? '' : '网关未连接：请确认 OpenClaw Gateway 运行中 (:18789)'),
      cadSession:cadSessionReady,
      inbox:{pending:p, claimed:c, processing:proc, total:inbox.length}, // 2026-08-12 M1 replies:replies.length,
      sse:{generic:sseClients.length, reply:replySseClients.length} }));
    return;
  }

  // ---- POST /send — 面板发消息（llm 模式走内置代理 / openclaw 模式走 CAD session） ----
  if (req.method === 'POST' && req.url === '/send') {
    let body = '';
    req.on('data', c => body += c);
    req.on('end', async () => {
      try {
        const msg = JSON.parse(body).message;
        if (!msg) { res.end(JSON.stringify({ok:false, error:'empty'})); return; }
        const entry = { id: (++msgIdCounter).toString(36), text: msg, time: new Date().toISOString(), status: 'pending', sent: false };
        inbox.push(entry);
        saveInbox();
        sseBroadcast('message', entry);

        if (RELAY_MODE === 'llm') {
          // ---- llm 模式：内置代理，串行处理（2026-08-07: 防并发共享 llmHistory/currentAbort/插件并发）----
          log('SEND', 'Queued: ' + entry.id + ' (' + msg.slice(0, 50) + ') [LLM]');
          res.end(JSON.stringify({ok:true, messageId: entry.id, status:'pending', session:'llm'}));
          const lcfg = (cfg && cfg.llm) || {};
          llmChain = llmChain.then(async () => {
            try { await processLlmMessage(entry, msg, lcfg); }
            catch (e) { log('LLM', 'processLlmMessage error: ' + e.message); }
          }).catch(() => {});
          return;
        }

        forwardToCad(msg, entry);
        log('SEND', 'Queued: ' + entry.id + ' (' + msg.slice(0, 50) + ')' + (cadSessionReady ? ' [CAD]' : ' [FALLBACK]'));
        res.end(JSON.stringify({ok:true, messageId: entry.id, status:'pending', session: cadSessionReady ? 'cad' : 'fallback'}));
      } catch(e) { res.end(JSON.stringify({ok:false, error:e.message})); }
    });
    return;
  }

  // ---- 以下接口不变：/inbox, /inbox/done, /inbox/stream, /replies/stream, /reply, /replies, /push ----
  // ---- POST /cancel — 取消当前排队/处理中的任务（2026-08-07 新增）----
  if (req.method === 'POST' && req.url === '/cancel') {
    // 2026-08-07: 真中断正在跑的任务（不再等它跑完丢弃结果）
    if (currentAbort) { try { currentAbort.abort(); } catch (_) {} currentAbort = null; }
    const cancelled = [];
    for (const m of inbox) {
      if (m.status === 'pending' || m.status === 'claimed') {
        m.status = 'cancelled';
        m.cancelledAt = new Date().toISOString();
        cancelled.push(m.id);
      }
    }
    if (cancelled.length) saveInbox();
    log('INBOX', 'Cancel requested, cancelled: ' + cancelled.join(','));
    res.end(JSON.stringify({ ok: true, cancelled }));
    return;
  }

  // ---- POST /restart — 掉线重启：优雅退出，由 launcher 3s 后自动拉起（2026-08-10 新增）----
  if (req.method === 'POST' && req.url === '/restart') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ ok: true }));
    log('RESTART', 'Restart requested, exiting in 150ms (launcher will respawn)');
    setTimeout(() => process.exit(0), 150);
    return;
  }

  // ---- POST /clear — 清理对话记录（会话历史+消息队列+回复缓存）----
  // ---- POST /clear 选择性清理（2026-08-07: scope=chat|logs|cache|reports|all；长期记忆/知识库/用户文件永不清理）----
  if (req.method === 'POST' && req.url === '/clear') {
    let body = '';
    req.on('data', c => body += c);
    req.on('end', () => {
      try {
        let scope = 'chat';
        try { const j = JSON.parse(body); if (j && j.scope) scope = String(j.scope); } catch (_) {}
        const result = {};
        const scopeSet = new Set(scope === 'all' ? ['chat', 'logs', 'cache', 'reports', 'plan'] : [scope]);
        if (scopeSet.has('chat')) {
          llmHistory.length = 0; inbox.length = 0; replies.length = 0; saveInbox();
          result.chat = true;
        }
        if (scopeSet.has('logs')) {
          for (const f of ['relay.log', 'relay-launcher.log', 'relay-crash.log']) {
            const p = path.join(__dirname, f);
            try { if (fs.existsSync(p)) { fs.writeFileSync(p, ''); result['logs:' + f] = true; } } catch (e) { result['logs:' + f] = 'error'; }
          }
        }
        if (scopeSet.has('cache')) {
          selectionCache = null;
          for (const f of [path.join(__dirname, '..', 'exchange', '_out', '_sel.json'), path.join(__dirname, '..', 'exchange', 'selection-cache.json')]) {
            try { if (fs.existsSync(f)) { fs.writeFileSync(f, ''); result['cache:' + path.basename(f)] = true; } } catch (e) {}
          }
        }
        if (scopeSet.has('reports')) {
          const p = path.join(__dirname, '..', 'exchange', 'missing-report.jsonl');
          try { if (fs.existsSync(p)) { fs.writeFileSync(p, ''); result.reports = true; } } catch (e) {}
        }
        // 2026-08-11: 任务计划清理 —— 仅 /clear all（全部清理=彻底清）联动；chat 不清（保护跨图任务续接）
        if (scopeSet.has('plan')) {
          clearActivePlan();
          result.plan = true;
        }
        // 长期记忆/知识库/export/import 永不清理（用户数据）
        log('INBOX', 'Clear scope=' + scope + ' ' + JSON.stringify(result));
        res.end(JSON.stringify({ ok: true, scope, result }));
      } catch (e) { res.end(JSON.stringify({ ok: false, error: e.message })); }
    });
    return;
  }

  // ---- POST /plan/clear — 清空任务计划（面板「清空任务」按钮；2026-08-11 新增）----
  // 与 /cancel（停执行、保留计划可续接）和 /clear chat（清聊天、保留计划）语义区分：
  // 本端点=用户主动放弃任务（清 activePlan），不影响聊天记录/消息队列
  if (req.method === 'POST' && req.url === '/plan/clear') {
    try {
      const had = (typeof hasActivePlan === 'function' && hasActivePlan()) || false;
      clearActivePlan();
      log('PLAN', 'Cleared (had=' + had + ')');
      res.end(JSON.stringify({ ok: true, cleared: had }));
    } catch (e) {
      res.end(JSON.stringify({ ok: false, error: e.message }));
    }
    return;
  }

  if (req.method === 'GET' && req.url === '/inbox') {
    const pending = inbox.filter(m => m.status === 'pending');
    res.end(JSON.stringify({messages: pending.map(m => ({ id: m.id, text: m.text, time: m.time })) }));
    return;
  }

  if (req.method === 'POST' && req.url === '/inbox/done') {
    let body = '';
    req.on('data', c => body += c);
    req.on('end', () => {
      try {
        const j = JSON.parse(body);
        const msg = inbox.find(m => m.id === j.messageId);
        if (!msg) { res.end(JSON.stringify({ok:false, error:'not found'})); return; }
        msg.status = 'done';
        saveInbox();
        if (j.reply) pushReply(j.reply, j.messageId);
        else res.end(JSON.stringify({ok:true}));
      } catch(e) { res.end(JSON.stringify({ok:false, error:e.message})); }
    });
    return;
  }

  if (req.method === 'GET' && req.url === '/inbox/stream') {
    res.writeHead(200, {
      'Content-Type': 'text/event-stream',
      'Cache-Control': 'no-cache',
      'Connection': 'keep-alive',
      'Access-Control-Allow-Origin': reqOrigin || '*',
    });
    res.write('event: connected\ndata: {}\n\n');
    sseClients.push(res);
    const heartbeat = setInterval(() => { try { res.write(': heartbeat\n\n'); } catch(e) { clearInterval(heartbeat); }}, 30000);
    req.on('close', () => {
      clearInterval(heartbeat);
      sseClients = sseClients.filter(c => c !== res);
    });
    return;
  }

  if (req.method === 'GET' && req.url === '/replies/stream') {
    res.writeHead(200, {
      'Content-Type': 'text/event-stream',
      'Cache-Control': 'no-cache',
      'Connection': 'keep-alive',
      'Access-Control-Allow-Origin': '*',
    });
    res.write('event: connected\ndata: { "status": "ok" }\n\n');
    if (replies.length > 0) {
      for (const r of replies) {
        res.write('event: reply\ndata: ' + JSON.stringify({text: r.text, time: r.time || new Date().toISOString()}) + '\n\n');
      }
    }
    replySseClients.push(res);
    const heartbeat = setInterval(() => { try { res.write(': heartbeat\n\n'); } catch(e) { clearInterval(heartbeat); }}, 30000);
    req.on('close', () => {
      clearInterval(heartbeat);
      replySseClients = replySseClients.filter(c => c !== res);
    });
    return;
  }

  if (req.method === 'POST' && req.url === '/reply') {
    let body = '';
    req.on('data', c => body += c);
    req.on('end', () => {
      try {
        const j = JSON.parse(body);
        const text = j.message || j.reply || '';
        if (text) pushReply(text, j.messageId);
        else if (j.messageId) {
          const m = inbox.find(x => x.id === j.messageId);
          if (m) { m.status = 'done'; saveInbox(); }
        }
        res.end(JSON.stringify({ok:true}));
      } catch(e) { res.end(JSON.stringify({ok:false, error:e.message})); }
    });
    return;
  }

  // ---- POST /tcp — HTTP→TCP 桥（AI 用 HTTP 调 C3D 插件，避免协议坑） ----
  if (req.method === 'POST' && req.url === '/tcp') {
    let body = '';
    req.on('data', c => body += c);
    req.on('end', () => {
      try {
        const reqBody = JSON.parse(body);
        const net = require('net');
        const c = new net.Socket();
        let buf = '';
        let settled = false;
        const timer = setTimeout(() => {
          if (!settled) { settled = true; c.destroy();
            res.end(JSON.stringify({jsonrpc:'2.0', id:reqBody.id, error:{code:-32000, message:'TCP timeout (30s)'}})); }
        }, 30000);
        c.on('error', (e) => {
          if (!settled) { settled = true; clearTimeout(timer);
            res.end(JSON.stringify({jsonrpc:'2.0', id:reqBody.id, error:{code:-32001, message:'TCP error: ' + e.message}})); }
        });
        c.on('data', d => {
          buf += d.toString();
          try {
            const parsed = JSON.parse(buf);
            if (!settled) { settled = true; clearTimeout(timer); c.destroy(); res.end(JSON.stringify(parsed)); }
          } catch(_) { /* 帧不完整，继续等 */ }
        });
        c.on('close', () => {
          if (!settled) { settled = true; clearTimeout(timer);
            res.end(JSON.stringify({jsonrpc:'2.0', id:reqBody.id, error:{code:-32002, message:'TCP closed, partial: ' + buf.slice(0,200)}})); }
        });
        c.connect(BRIDGE_PORT, '127.0.0.1', () => {
          c.write(JSON.stringify(reqBody) + '\n');
        });
      } catch(e) {
        res.end(JSON.stringify({jsonrpc:'2.0', error:{code:-32700, message:'parse error: ' + e.message}}));
      }
    });
    return;
  }

  if (req.method === 'GET' && req.url === '/replies') {
    res.end(JSON.stringify({replies: replies.splice(0, replies.length)}));
    return;
  }

  if (req.method === 'POST' && req.url === '/push') {
    let body = '';
    req.on('data', c => body += c);
    req.on('end', () => {
      try {
        const msg = JSON.parse(body).message;
        if (msg) pushReply(msg);
        res.end(JSON.stringify({ok:true}));
      } catch(e) { res.end(JSON.stringify({ok:false, error:e.message})); }
    });
    return;
  }

  res.end('relay');
});

server.listen(port, '127.0.0.1', () => {
  log('UP', 'Relay :' + port + (token ? ' [CAD-session mode]' : ' [Universal mode]'));
  // 2026-08-12 C1: llm 模式重启后重新消费积压的 pending 消息（崩溃不丢）
  if (RELAY_MODE === 'llm') {
    const lcfg = (cfg && cfg.llm) || {};
    const pendingMsgs = inbox.filter(m => m.status === 'pending').map(m => ({ entry: m, text: m.text }));
    if (pendingMsgs.length) {
      log('INBOX', '恢复 ' + pendingMsgs.length + ' 条积压消息 [LLM]');
      for (const pm of pendingMsgs) {
        llmChain = llmChain.then(async () => {
          try { await processLlmMessage(pm.entry, pm.text, lcfg); }
          catch (e) { log('LLM', '恢复处理 error: ' + e.message); }
        }).catch(() => {});
      }
    }
  }
  log('UP', `CAD session: ${CAD_SESSION_KEY}`);
  log('UP', 'SSE: /replies/stream | Send: POST /send | Inbox: GET /inbox');
  if (!token) {
    if (RELAY_MODE === 'llm') {
      log('??', '内置 LLM 模式：面板 AI 由 relay 直接调用（面板内填 API Key 即可用）');
    } else {
      log('??', 'OpenClaw 未配置（config.json 的 relayToken 为空）。面板消息将排队落盘，网关恢复后自动补发。');
      log('??', '对接 OpenClaw: 编辑 config.json 填 relayToken + relaySessionKey，或 setx RELAY_TOKEN <token>');
    }
  }
});




// 2026-08-17: 项目上下文——用户显式声明的项目名（config.json 顶层 project；空=全局模式）
let currentProject = (cfg && typeof cfg.project === 'string' && cfg.project.trim()) ? cfg.project.trim() : '';
function sanitizeProjectName(n) {
  return String(n || '').replace(/[\\/:*?"<>|]/g, '_').trim() || 'unnamed';
}
function getProjectDir(sub) {
  if (!currentProject) return path.join(__dirname, '..', 'exchange', sub || 'agent-work');
  return path.join(__dirname, '..', 'exchange', 'projects', sanitizeProjectName(currentProject), sub || 'agent-work');
}
// selection-cache 路径按项目切换（跨项目不串）
function selCacheFile() {
  const f = currentProject ? path.join(getProjectDir(''), 'selection-cache.json') : SEL_CACHE_FILE;
  try { fs.mkdirSync(path.dirname(f), { recursive: true }); } catch (_) {}
  return f;
}
function saveSelectionCache() {
  try { fs.writeFileSync(selCacheFile(), JSON.stringify(selectionCache), 'utf8'); } catch (_) {}
}
