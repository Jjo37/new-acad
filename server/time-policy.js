'use strict';
// time-policy.js -- P2.6 S7: 时间只有一种存法（2026-10-28）
//
// 病根：2026-10-23 的工作流事故 —— 记录 id 和月份目录是**本地时间**拼的，排序也按文件名，
// 用户换时区（出差 / 笔记本设成别的区）后，新记录沉到列表底部，prune() 先删掉"最新"的那条。
// 排序那次改成了看 createdAt，但**存法**没统一：同一个时刻在不同机器上会得到不同的 id 和目录。
//
// 规则（三条，其它地方一律不许自己拼时间）：
//   T1 存：UTC、固定格式 `YYYY-MM-DDTHH:mm:ss.sssZ`（就是 Date#toISOString）
//   T2 派生标识（id 里的时间戳、月份目录）：也从 UTC 算 —— 否则同一条记录在不同时区的机器上身份不同
//   T3 显：只在这里转本地（Intl.DateTimeFormat），且格式固定（秒级 / 日级两档），别处不许自己拼
//
// 纯模块：除了 formatLocal 读系统时区，没有任何 I/O。

const STORED_UTC = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/;

/** T1：存进记录里的时间（UTC ISO，毫秒固定三位）。 */
function nowIso(now) {
  const d = now instanceof Date ? now : new Date();
  return d.toISOString();
}

/** T1 校验：这条字符串是不是"存法合法"的（本地偏移、缺毫秒、缺 Z 都不算）。 */
function isStoredUtc(value) {
  return typeof value === 'string' && STORED_UTC.test(value);
}

const p2 = (n) => String(n).padStart(2, '0');

/** T2：id 里的时间戳（UTC）—— 磁盘上看得懂，且跨时区一致。 */
function idStamp(now) {
  const d = now instanceof Date ? now : new Date();
  return d.getUTCFullYear() + p2(d.getUTCMonth() + 1) + p2(d.getUTCDate()) +
    '-' + p2(d.getUTCHours()) + p2(d.getUTCMinutes()) + p2(d.getUTCSeconds());
}

/** T2：记录落在哪个月份目录（UTC，`YYYY-MM`）。 */
function monthBucket(now) {
  const d = now instanceof Date ? now : new Date();
  return d.getUTCFullYear() + '-' + p2(d.getUTCMonth() + 1);
}

/** T2：日期戳（UTC，`YYYY-MM-DD`）—— 按天分文件的日志用它。 */
function dateStamp(value) {
  const d = value instanceof Date ? value : new Date();
  return d.getUTCFullYear() + '-' + p2(d.getUTCMonth() + 1) + '-' + p2(d.getUTCDate());
}

/** T3：展示用（本地时间，固定格式）。styles: 'seconds'（默认）| 'minutes' | 'date'。 */
function formatLocal(value, style) {
  const d = value instanceof Date ? value : new Date(value);
  if (!Number.isFinite(d.getTime())) return '';
  const opts = style === 'date'
    ? { year: 'numeric', month: '2-digit', day: '2-digit' }
    : (style === 'minutes'
      ? { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false }
      : { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
  try {
    return new Intl.DateTimeFormat('sv-SE', opts).format(d);   // sv-SE = ISO 形状，本地时区
  } catch (e) {
    return d.toISOString().slice(0, 19).replace('T', ' ');
  }
}

/** 解析存储值（字符串或数字）→ 毫秒；非法返回 0（调用方按"最旧"处理）。 */
function toMillis(value) {
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (typeof value !== 'string' || !value) return 0;
  const t = Date.parse(value);
  return Number.isFinite(t) ? t : 0;
}

module.exports = { nowIso, isStoredUtc, idStamp, monthBucket, dateStamp, formatLocal, toMillis, STORED_UTC };
