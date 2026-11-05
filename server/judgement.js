'use strict';
/**
 * judgement.js — 任务「成功 / 失败」的唯一判定入口（P2.6 S3，2026-10-28）
 *
 * 为什么要有这个文件：判定原先散在 workflow-store.record() 里的一段内联表达式。
 * 那段代码本身是对的（2026-10-23 修过），但它没有名字、没有独立测试、也没有任何东西
 * 阻止第二份"我觉得这样算成功"的判定在别处长出来。S3 要的是**结构**上的单入口。
 *
 * 规则（每条对应一个真实发生过的误判）：
 *   R1 只有 act（写/改）步骤失败才判任务失败。
 *      真实案例：2026-10-23 偏移任务，getEntityInfo 参数写成 handles 被拒（-32602），
 *      模型改成 handle 重试成功，offsetEntity 全部成功 —— 整单却记成 failed。
 *   R2 最后一步失败 → 判失败。任务以报错收尾，即使没有 act 失败。
 *   R3 用户取消 → 判失败（未完成）。
 *   R4 channel=local 的步骤不参与判定（readKnowledge 之类的失败不该拖垮任务）。
 *   R5 blocked 步骤不算失败 —— 那是护栏拦截，不是执行失败。
 *
 * 判定与统计是两件事：判成功 ≠ 没出错。
 * `failed` / `failedProbes` 仍如实计数，报告里必须看得到探针失败了几次。
 *
 * 纯函数：不读文件、不碰时间、不碰网络。所以能直接进测试和变异。
 */

const ACT = 'act';

function isCounted(step) {
  return !!step && typeof step === 'object' && step.channel !== 'local';
}

function isFailed(step) {
  // blocked 步骤在记录里没有 ok 字段；显式排除，避免以后有人给它补上 ok:false 就悄悄改了判定。
  return step.ok === false && !step.blocked;
}

/**
 * @param {Array<object>} steps 工作流步骤（workflow/1 记录里的 steps）
 * @param {{cancelled?: boolean}} [options]
 */
function judgeOutcome(steps, options) {
  const opts = options || {};
  const counted = (Array.isArray(steps) ? steps : []).filter(isCounted);
  const failedSteps = counted.filter(isFailed);
  const failedActs = failedSteps.filter((s) => (s.role || ACT) === ACT).length;
  const failedProbes = failedSteps.length - failedActs;

  const last = counted.length ? counted[counted.length - 1] : null;
  const endedOnError = !!(last && isFailed(last));
  const cancelled = !!opts.cancelled;

  const reasons = [];
  if (cancelled) reasons.push('cancelled');
  if (failedActs > 0) reasons.push(failedActs + ' failed action(s)');
  if (endedOnError) reasons.push('last step failed');

  return {
    ok: !cancelled && failedActs === 0 && !endedOnError,
    failed: failedSteps.length,
    failedActs,
    failedProbes,
    endedOnError,
    cancelled,
    reasons,
    counted: counted.length,
  };
}

module.exports = { judgeOutcome, isCounted, isFailed };
