'use strict';
/**
 * block-policy.js — 工具失败的「封锁」判定（2026-11-14 重写）
 *
 * 为什么重写（实测事故）：
 *   旧实现 `if (++failCount[method] >= 2) blockedMethods.add(method)` —— 同方法错 2 次就整任务物理封锁，
 *   **不区分错误类型、不区分参数**。2026-11-14 真实代价：
 *     addSurfacePointsFromFile 两次 -32602（AI 自己把 0 起列号写成 1 起）→ 方法被封死 →
 *     只能退回"按桩号 100 m 采样" → 地面线精度差 5 倍 → 被用户判为"没有严格照图建模"。
 *   一个"防死循环"的护栏，最后把正确路径堵死了。
 *
 * 现在的判据（三条，全部可测）：
 *   1. 参数类错误（INVALID_INPUT / -32602 / METHOD_NOT_FOUND）**永不封锁**：这是可修的，改参数就行。
 *   2. 同一 (方法 + 错误码 + 参数指纹) 重复 ≥ 2 次 → 封锁：真·原地打转。
 *   3. 同一 (方法 + 错误码) 重复 ≥ 4 次且参数不同 → 封锁：方法本身不稳，换方法。
 *   另外给出解封途径：reset(method) 或新任务开始。
 */

const PARAM_ERROR_CODES = new Set([
  '-32602', 'CIVIL3D.INVALID_INPUT', 'CIVIL3D.METHOD_NOT_FOUND', '-32601', 'INVALID_INPUT',
]);
const TRANSIENT_ERROR_CODES = new Set([
  'BRIDGE_EXCEPTION', '-32000', '-32001', '-32002', 'CIVIL3D.TIMEOUT', 'CIVIL3D.JOB_RUNNING',
]);

function normCode(code) {
  return String(code == null ? '' : code).trim();
}

function classify(code) {
  const c = normCode(code);
  if (PARAM_ERROR_CODES.has(c)) return 'param';
  if (TRANSIENT_ERROR_CODES.has(c)) return 'transient';
  return 'other';
}

// 参数指纹：只取排序后的键+短值，用于判断"是不是同一次尝试又来了"
function paramsFingerprint(params) {
  try {
    if (!params || typeof params !== 'object') return '';
    const keys = Object.keys(params).sort();
    return keys.map((k) => {
      const v = params[k];
      let s;
      if (v === null || v === undefined) s = '';
      else if (typeof v === 'object') s = JSON.stringify(v);
      else s = String(v);
      if (s.length > 64) s = s.slice(0, 64);
      return k + '=' + s;
    }).join('&').slice(0, 400);
  } catch (_) {
    return '';
  }
}

class FailTracker {
  /**
   * @param {{sameSignatureLimit?:number, sameCodeLimit?:number}} [opts]
   */
  constructor(opts) {
    const o = opts || {};
    this.sameSignatureLimit = o.sameSignatureLimit > 0 ? o.sameSignatureLimit : 2;
    this.sameCodeLimit = o.sameCodeLimit > 0 ? o.sameCodeLimit : 4;
    this.bySignature = new Map();   // sig -> count
    this.byMethodCode = new Map();  // method|code -> Set(fingerprint)
    this.byMethodCodeCount = new Map(); // method|code -> total count
    this.blocked = new Map();       // method -> reason
    this.paramErrors = new Map();   // method -> count（仅用于提示，不封锁）
  }

  isBlocked(method) {
    return this.blocked.has(method);
  }

  blockedMessage(method) {
    const reason = this.blocked.get(method) || '';
    return '⚠️ 方法 ' + method + ' 已被封锁（' + reason + '），本任务内禁止再调用。'
      + '请换方法；若卡住，向用户说明情况并询问如何继续，不要继续猜测参数。';
  }

  /**
   * 记一次失败，返回判定。
   * @param {{method:string, errCode:string, params?:object}} f
   * @returns {{block:boolean, kind:string, count:number, note:string}}
   */
  note(f) {
    const method = String(f.method || '');
    const code = normCode(f.errCode);
    const kind = classify(code);
    const fp = paramsFingerprint(f.params);

    if (kind === 'param') {
      const n = (this.paramErrors.get(method) || 0) + 1;
      this.paramErrors.set(method, n);
      // 参数错永不封锁：可修。但重复 3 次以上说明它在盲猜，给更硬的指令。
      const note = n >= 3
        ? '（方法 ' + method + ' 已连续 ' + n + ' 次参数错误：停止盲猜。先 readKnowledge / 查方法契约，或直接问用户。）'
        : '（参数错误 ' + code + ' 不计入封锁：按错误里的提示修正参数后重试，不要换方法绕路。）';
      return { block: false, kind: 'param', count: n, note };
    }

    const sig = method + '||' + code + '||' + fp;
    const sameSig = (this.bySignature.get(sig) || 0) + 1;
    this.bySignature.set(sig, sameSig);

    const mcKey = method + '||' + code;
    const sigs = this.byMethodCode.get(mcKey) || new Set();
    sigs.add(fp);
    this.byMethodCode.set(mcKey, sigs);
    const sameCode = (this.byMethodCodeCount.get(mcKey) || 0) + 1;
    this.byMethodCodeCount.set(mcKey, sameCode);

    if (sameSig >= this.sameSignatureLimit) {
      this.blocked.set(method, '同一异常重复 ' + sameSig + ' 次（' + code + '，参数未变）');
      return { block: true, kind, count: sameSig, note: '（同一异常重复 ' + sameSig + ' 次，参数未变，已封锁）' };
    }
    if (sameCode >= this.sameCodeLimit && sigs.size >= 2) {
      this.blocked.set(method, '失败 ' + sameCode + ' 次（' + code + '）且换过参数仍不稳');
      return { block: true, kind, count: sameCode, note: '（失败 ' + sameCode + ' 次且换过参数仍不稳，已封锁）' };
    }
    const note = kind === 'transient'
      ? '（桥接/超时类错误，不封锁：可直接重试一次，仍失败再换方法。）'
      : '（失败 ' + sameCode + ' 次，尚未封锁：可换参数或换方法。）';
    return { block: false, kind, count: sameCode, note };
  }

  reset(method) {
    if (method) { this.blocked.delete(method); return; }
    this.blocked.clear();
    this.bySignature.clear();
    this.byMethodCode.clear();
    this.byMethodCodeCount.clear();
    this.paramErrors.clear();
  }
}

module.exports = { FailTracker, classify, paramsFingerprint, PARAM_ERROR_CODES, TRANSIENT_ERROR_CODES };
