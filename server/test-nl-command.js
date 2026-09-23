// 面板自然语言命令测试器（2026-09-10 建）
// 用法: node server/test-nl-command.js "你的自然语言指令" [超时秒数]
// 走真实面板链路: POST /send → llm-agent(DeepSeek) → POST /tcp → C3D 插件
// 监听 /replies/stream 抓回复；同时打印 relay.log 里该时段新出现的 [LLM]/[TCP] 行，便于看它调了什么方法
const fs = require('fs');
const path = require('path');

const msg = process.argv[2];
const timeoutSec = Number(process.argv[3] || 180);
if (!msg) { console.error('用法: node server/test-nl-command.js "指令文本" [超时秒数]'); process.exit(1); }

const RELAY = 'http://127.0.0.1:19876';
const LOG = path.join(__dirname, 'relay.log');

(async () => {
  const logStart = fs.existsSync(LOG) ? fs.statSync(LOG).size : 0;
  const replies = [];

  // SSE 监听
  const ac = new AbortController();
  (async () => {
    try {
      const res = await fetch(RELAY + '/replies/stream', { signal: ac.signal, headers: { Accept: 'text/event-stream' } });
      const reader = res.body.getReader();
      const dec = new TextDecoder();
      let buf = '';
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += dec.decode(value, { stream: true });
        const parts = buf.split('\n\n');
        buf = parts.pop();
        for (const p of parts) {
          const ev = (p.match(/^event: (.+)$/m) || [])[1];
          const data = (p.match(/^data: (.+)$/m) || [])[1];
          if (ev === 'reply' && data) {
            try { const j = JSON.parse(data); replies.push(j.text); } catch { replies.push(data); }
          }
        }
      }
    } catch (e) { /* aborted */ }
  })();

  await new Promise(r => setTimeout(r, 800));
  const baseline = replies.length;
  if (msg !== '--listen') {
    console.log('>>> 发送指令: ' + msg);
    const sent = await fetch(RELAY + '/send', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ message: msg })
    }).then(r => r.json());
    console.log('    受理: ' + JSON.stringify(sent));
  } else {
    console.log('>>> 只听模式（等待已有任务回复）');
  }
  const t0 = Date.now();

  // 等新回复（跳过 SSE 重放的历史回复）
  while (Date.now() - t0 < timeoutSec * 1000) {
    await new Promise(r => setTimeout(r, 2000));
    if (replies.length > baseline) { await new Promise(r => setTimeout(r, 2000)); break; }
  }
  ac.abort();

  console.log('\n=== 面板 AI 回复 (' + Math.round((Date.now() - t0) / 1000) + 's) ===');
  if (replies.length <= baseline) console.log('(超时无新回复)');
  for (const r of replies.slice(baseline)) console.log(r);

  // relay.log 新增部分（看它调了哪些方法）
  try {
    const fd = fs.openSync(LOG, 'r');
    const size = fs.fstatSync(fd).size;
    if (size > logStart) {
      const len = Math.min(size - logStart, 20000);
      const buf = Buffer.alloc(len);
      fs.readSync(fd, buf, 0, len, size - len);
      console.log('\n=== relay.log 新增 ===');
      console.log(buf.toString('utf8'));
    }
    fs.closeSync(fd);
  } catch { }
  process.exit(0);
})();
