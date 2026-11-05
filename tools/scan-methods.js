// 扫描 CommandDispatcher.cs 提取所有注册的 TCP 方法名
// 格式: "methodName" => Handler(...)
const fs = require('fs');

const file = process.argv[2] || 'D:\\new-acad\\plugin\\AcBridge-v24\\src\\CommandDispatcher.cs';
const src = fs.readFileSync(file, 'utf8');

const methods = new Set();
const re = /^\s*"([a-zA-Z][a-zA-Z0-9]*)"\s*=>/gm;
let m;
while ((m = re.exec(src)) !== null) {
  methods.add(m[1]);
}

const sorted = [...methods].sort();
console.log('总数: ' + sorted.length);
console.log(sorted.join('\n'));
