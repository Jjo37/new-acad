// 扫描所有 C# 方法，找出用 (command "_.XXX" ...) 模拟交互式命令的实现
// 输出: 方法名 | 文件:行号 | 调用的 AutoCAD 命令 | 类型
const fs = require('fs');
const path = require('path');

const srcDir = process.argv[2] || 'plugin/AcBridge-v24/src';
const files = fs.readdirSync(srcDir).filter(f => f.endsWith('.cs'));

const results = []; // { method, file, line, cmd, kind }
let totalCommand = 0;

for (const file of files) {
  const full = path.join(srcDir, file);
  const lines = fs.readFileSync(full, 'utf8').split('\n');

  let currentMethod = null;
  let methodLine = 0;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    // 跟踪当前方法名: public static Task<object?> XxxAsync(...)
    const methodMatch = line.match(/public static\s+(?:async\s+)?Task<object\?>\s+(\w+Async)\s*\(/);
    if (methodMatch) {
      currentMethod = methodMatch[1];
      methodLine = i + 1;
      continue;
    }

    if (!currentMethod) continue;

    // 找 (command "_.XXX" 或 (command "XXX" 或 (command "_.XXX"
    // C# 源码里是转义形式: (command \"_.EXTRUDE\"
    const cmdMatch = line.match(/\(command\s*\\?"(_?\.?[A-Z0-9]+)\\?"/i);
    if (cmdMatch) {
      const cmd = cmdMatch[1];
      totalCommand++;
      // 判断是否交互式命令（有更多参数喂给 command）
      const hasArgs = line.includes('(handent') || line.includes('\"\"');
      results.push({
        method: currentMethod,
        file,
        line: i + 1,
        cmd,
        kind: hasArgs ? '交互式模拟' : '简单命令'
      });
      // 一个方法可能有多个 command 调用，继续但不重复方法名标记问题不大
    }

    // 方法体结束（下一个方法或类结束）——用大括号粗略判断：方法结束后重置
    // 简单策略：如果遇到 "  }" 且缩进级别低，可能方法结束；这里用简化判断：
    // 连续看到下一行是 public static 就重置（上面已处理）
  }
}

// 汇总按方法去重（一个方法可能多次 command）
const byMethod = new Map();
for (const r of results) {
  if (!byMethod.has(r.method)) byMethod.set(r.method, []);
  byMethod.get(r.method).push(r);
}

console.log('=== 使用 (command ...) 的方法 ===');
console.log(`总数: ${byMethod.size} 个方法, ${results.length} 处 command 调用\n`);

for (const [method, rs] of byMethod) {
  const cmds = [...new Set(rs.map(r => r.cmd))].join(', ');
  const file = rs[0].file;
  const line = rs.map(r => r.line).join(',');
  console.log(`${method} | ${file}:${line} | ${cmds}`);
}

console.log('\n=== 按命令分类统计 ===');
const byCmd = new Map();
for (const r of results) {
  if (!byCmd.has(r.cmd)) byCmd.set(r.cmd, []);
  byCmd.get(r.cmd).push(r.method);
}
for (const [cmd, methods] of [...byCmd.entries()].sort()) {
  console.log(`${cmd}: ${methods.length} 个方法`);
}
