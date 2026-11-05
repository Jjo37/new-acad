'use strict';
// 2026-10-14: 扫描件 OCR —— 用 Windows 自带 WinRT（Windows.Data.Pdf 渲染 + Windows.Media.Ocr 识别），
// 由本机 PowerShell 5.1 拉起。**零打包**：不引 Tesseract / pdfium / Microsoft.Windows.SDK.NET。
// 供 llm-agent 在 ingestUserKnowledge 遇到"无文本层 PDF"时调用；也可单独用。
const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const PS_SCRIPT = path.join(__dirname, 'ocr-win.ps1');

// 去掉 Windows OCR 逐字产生的 CJK 间空格，并压缩多余空白
function normalizeOcrText(s) {
  if (!s) return '';
  const CJK = '[\\u3000-\\u303f\\u4e00-\\u9fff\\uff00-\\uffef]';
  let t = s.replace(new RegExp('(' + CJK + ')[ \\t]+(?=' + CJK + ')', 'g'), '$1');
  t = t.replace(/[ \t]{2,}/g, ' ');
  return t;
}

function runPs(args, timeoutMs) {
  return new Promise((resolve) => {
    let out = '', err = '';
    let child;
    try { child = spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', PS_SCRIPT, ...args], { windowsHide: true }); }
    catch (e) { resolve({ code: -1, out: '', err: e.message }); return; }
    const to = setTimeout(() => { try { child.kill(); } catch (_) { /* best-effort */ } }, timeoutMs || 240000);
    child.stdout.on('data', d => out += d);
    child.stderr.on('data', d => err += d);
    child.on('close', (code) => { clearTimeout(to); resolve({ code, out: out.trim(), err: err.trim() }); });
    child.on('error', (e) => { clearTimeout(to); resolve({ code: -1, out, err: e.message }); });
  });
}

let _langs = null;
async function ocrLanguages() {
  if (_langs) return _langs;
  const r = await runPs(['-List'], 60000);
  const tags = (r.out || '').split(/[\r\n,]+/).map(s => s.trim()).filter(Boolean);
  _langs = tags;
  return tags;
}

// OCR 一个 PDF 或图片文件。返回 { ok, text, lang, pages?, chars?, error?, hint? }
async function ocrFile(filePath, opts = {}) {
  if (!fs.existsSync(filePath)) return { ok: false, error: 'FILE_NOT_FOUND', hint: filePath };
  const out = path.join(os.tmpdir(), 'nacad-ocr-' + process.pid + '-' + Date.now() + '.txt');
  const args = ['-Path', filePath, '-Out', out,
    '-MaxPages', String(opts.maxPages || 30), '-Width', String(opts.width || 2000)];
  if (opts.lang) args.push('-Lang', opts.lang);
  const r = await runPs(args, opts.timeoutMs || 240000);
  let raw = '';
  try { raw = fs.readFileSync(out, 'utf8'); } catch (_) { /* 读不到→看 stderr */ }
  try { fs.unlinkSync(out); } catch (_) { /* best-effort */ }

  if (r.code !== 0 || !raw) {
    const msg = (r.err || '') + ' ' + (r.out || '');
    if (/OCR_UNAVAILABLE|AvailableRecognizerLanguages|no engine/i.test(msg)) {
      const langs = await ocrLanguages().catch(() => []);
      const hint = langs.length
        ? ('系统已装 OCR 语言: ' + langs.join(', ') + '；需要中文请到 设置→时间和语言→语言→添加“中文(简体)”的“光学字符识别”组件')
        : '本机没有可用的 Windows OCR 语言包：请到 设置→时间和语言→语言 添加所需语言（含“光学字符识别”组件）';
      return { ok: false, error: 'OCR_UNAVAILABLE', hint };
    }
    return { ok: false, error: (r.err || r.out || ('exit ' + r.code)).slice(0, 300) };
  }

  const all = raw.split(/\r?\n/);
  const headLines = all.filter(l => /^\[ocr\] /.test(l));
  const body = all.filter(l => !/^\[ocr\] /.test(l)).join('\n');
  const head = headLines.join(' ');
  const langM = head.match(/lang=([^\s]+)/);
  const pagesM = head.match(/pdf pages=(\d+) rendered=(\d+)/);
  const text = normalizeOcrText(body).trim();
  return {
    ok: true,
    lang: langM ? langM[1] : '',
    totalPages: pagesM ? Number(pagesM[1]) : null,
    renderedPages: pagesM ? Number(pagesM[2]) : null,
    chars: text.length,
    text,
  };
}

module.exports = { ocrFile, ocrLanguages, normalizeOcrText, PS_SCRIPT };
