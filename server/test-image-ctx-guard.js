// test-image-ctx-guard.js — 2026-09-15 回归测试
// 覆盖 bug: 499KB PNG → base64 650KB 被当成文本计入上下文 → 下一轮直接撞 CTX_MAX 硬停
//          （实锤: relay.log 20:47 "上下文已达 655KB(268%)" + 图片从未真正到达模型）
// 断言: ① 图片消息不再撑爆体积估算; ② 文本/工具结果膨胀仍被拦截; ③ readImage 单图限额生效
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');

const { estimateMessagesBytes, validateToolGroups } = require('./llm-agent.js');
const { readImage } = require('./file-tools.js');

const CTX_MAX = 250000; // 与 llm-agent.js 保持一致
let fail = 0;
function check(name, cond, detail) {
  console.log((cond ? '  PASS ' : '  FAIL ') + name + (detail ? ' — ' + detail : ''));
  if (!cond) fail++;
}

// ---------- 1. 真实场景复现: 图片注入后的那一轮 ----------
const bigB64 = 'A'.repeat(650 * 1024); // 650KB base64（≈ Bro 那张 499KB PNG）
const msgs = [
  { role: 'system', content: 'S'.repeat(20000) },
  { role: 'user', content: '看看这张图片' },
  { role: 'assistant', content: '我来看看这张图片。', tool_calls: [{ id: 'c1', type: 'function', function: { name: 'readImage', arguments: '{"path":"C:\\\\x.png"}' } }] },
  { role: 'tool', tool_call_id: 'c1', content: JSON.stringify({ path: 'C:\\x.png', size: 499149, mime: 'image/png', note: 'ok' }) },
  { role: 'user', content: [{ type: 'text', text: '（系统注入：以下图片为用户要求查看的文件 C:\\x.png，请基于图片内容继续任务）' }, { type: 'image_url', image_url: { url: 'data:image/png;base64,' + bigB64 } }] }
];
const raw = JSON.stringify(msgs).length;
const est = estimateMessagesBytes(msgs);
console.log('[1] 图片注入轮 — 原始长度 ' + Math.round(raw / 1024) + 'KB, 估算 ' + Math.round(est / 1024) + 'KB');
check('原始长度确实超限(复现 bug)', raw > CTX_MAX, raw + ' > ' + CTX_MAX);
check('估算不再超限(硬停不再触发)', est < CTX_MAX, est + ' < ' + CTX_MAX);
check('图片按固定权重计(≈2KB)', est < 30000, 'est=' + est);

// ---------- 2. 文本/工具结果膨胀仍要被拦 ----------
const bloated = [
  { role: 'system', content: 'S'.repeat(20000) },
  { role: 'tool', tool_call_id: 'c2', content: 'X'.repeat(300 * 1024) }
];
check('纯文本膨胀仍触发硬停', estimateMessagesBytes(bloated) > CTX_MAX, estimateMessagesBytes(bloated) + '');

// ---------- 3. readImage 单图限额 ----------
const tmp = os.tmpdir();
const bigPng = path.join(tmp, 'acbridge_big_guard_test.png');
const okPng = path.join(tmp, 'acbridge_ok_guard_test.png');
fs.writeFileSync(bigPng, Buffer.alloc(1200 * 1024, 7));   // base64 ≈ 1.6MB > 1.5MB 上限
fs.writeFileSync(okPng, Buffer.alloc(90 * 1024, 7));      // base64 ≈ 120KB
let threw = '';
try { readImage({ path: bigPng }); } catch (e) { threw = e.message; }
check('超大图片被拒绝且有明确指引', /过大/.test(threw) && /压缩|裁剪/.test(threw), threw || '(未抛错)');
let okLen = -1;
try { okLen = readImage({ path: okPng }).base64.length; } catch (e) { okLen = -1; }
check('正常图片可读', okLen > 0, 'base64=' + okLen);
try { fs.unlinkSync(bigPng); } catch (_) {}
try { fs.unlinkSync(okPng); } catch (_) {}

// ---------- 4. 工具组不变式（2026-09-15 实锤的 400 根因） ----------
const assistantWithTwo = { role: 'assistant', content: 'x', tool_calls: [{ id: 'c_a', type: 'function', function: { name: 'readImage', arguments: '{}' } }, { id: 'c_b', type: 'function', function: { name: 'cadCall', arguments: '{}' } }] };
const badShape = [
  { role: 'user', content: 'go' }, assistantWithTwo,
  { role: 'tool', tool_call_id: 'c_a', content: 'ok' },
  { role: 'user', content: [{ type: 'text', text: '（系统注入：图片）' }, { type: 'image_url', image_url: { url: 'data:image/png;base64,AA' } }] },
  { role: 'tool', tool_call_id: 'c_b', content: 'ok' }
];
const pb = validateToolGroups(badShape);
check('旧顺序(图片插在工具组中间)被判不合规', pb.length === 1 && pb[0].missing.includes('c_b'), JSON.stringify(pb));
const goodShape = [
  { role: 'user', content: 'go' }, assistantWithTwo,
  { role: 'tool', tool_call_id: 'c_a', content: 'ok' },
  { role: 'tool', tool_call_id: 'c_b', content: 'ok' },
  { role: 'user', content: [{ type: 'text', text: '（系统注入：图片）' }, { type: 'image_url', image_url: { url: 'data:image/png;base64,AA' } }] }
];
check('新顺序(图片延后到工具组之后)合规', validateToolGroups(goodShape).length === 0, JSON.stringify(validateToolGroups(goodShape)));

console.log(fail === 0 ? '\nALL PASS (8)' : '\nFAILED: ' + fail);
process.exit(fail === 0 ? 0 : 1);
