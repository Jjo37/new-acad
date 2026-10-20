'use strict';
// node server/test-ocr.js [OCR_TEST_FILE=<图片或PDF>]
// 扫描件 OCR（Windows 自带 WinRT，零打包）自测。
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { ocrLanguages, ocrFile, normalizeOcrText, PS_SCRIPT } = require('./ocr.js');

let pass = 0, fail = 0;
function t(name, fn) { try { fn(); console.log('  PASS ' + name); pass++; } catch (e) { console.log('  FAIL ' + name + ': ' + e.message); fail++; } }

(async () => {
  console.log('== OCR 自测 ==');
  t('normalize: 去 CJK 间空格', () => assert.strictEqual(normalizeOcrText('工 程 签 证 变更'), '工程签证变更'));
  t('normalize: 保留英文词间空格', () => assert.strictEqual(normalizeOcrText('hello world 工 程'), 'hello world 工程'));
  t('normalize: 合并多余空白', () => assert.strictEqual(normalizeOcrText('a    b'), 'a b'));
  t('ocr-win.ps1 存在', () => assert.ok(fs.existsSync(PS_SCRIPT), PS_SCRIPT));

  const langs = await ocrLanguages();
  t('检测到 Windows OCR 语言', () => assert.ok(langs.length > 0, '未检测到语言（缺语言包？）'));
  console.log('  langs = [' + langs.join(', ') + ']');

  if (process.env.OCR_TEST_FILE) {
    const r = await ocrFile(process.env.OCR_TEST_FILE, { maxPages: 1 });
    t('ocrFile(' + path.basename(process.env.OCR_TEST_FILE) + ')', () => { assert.ok(r.ok, 'error=' + r.error); assert.ok(r.chars > 0, 'chars=0'); });
    console.log('  -> lang=' + r.lang + ' chars=' + (r.chars || 0) + (r.ok ? (' head="' + r.text.slice(0, 40).replace(/\n/g, ' ') + '"') : ''));
  } else {
    console.log('  (未设 OCR_TEST_FILE → 跳过文件识别)');
  }

  console.log('\nOCR 自测: ' + pass + ' PASS / ' + fail + ' FAIL');
  process.exit(fail ? 1 : 0);
})();
