// new-acad 自检 — 验证 C3D 插件基础功能
// 用法: 开 C3D 后跑 node tools\smoke-test.js

const net = require('net');

function tcp(method, params, timeout) {
  return new Promise((resolve, reject) => {
    const c = new net.Socket();
    const id = Date.now();
    c.connect(8080, '127.0.0.1', () => c.write(JSON.stringify({jsonrpc:'2.0',id,method,params})+'\n'));
    let b = '';
    c.on('data', d => b += d.toString());
    c.on('close', () => { try { const r = JSON.parse(b); if (r.error) reject(r.error.message); else resolve(r.result); } catch(e) { reject(b); } });
    c.on('error', reject);
    setTimeout(() => { c.destroy(); reject('TIMEOUT'); }, timeout || 15000);
  });
}

async function check(label, fn) {
  process.stdout.write('  ' + label + '... ');
  try { await fn(); console.log('PASS'); }
  catch(e) { console.log('FAIL: ' + e.toString().slice(0, 80)); }
}

async function main() {
  console.log();
  console.log('=== new-acad 自检 ===');
  console.log();

  await check('TCP :8080 联通', async () => {
    const h = await tcp('getCivil3DHealth', {});
    if (!h.connected) throw 'not connected';
  });

  await check('getDrawingInfo', async () => {
    const info = await tcp('getDrawingInfo', {});
    if (!info.fileName) throw 'no fileName';
  });

  let ch;
  await check('createCircle', async () => {
    const r = await tcp('createCircle', { center: [0, 200], diameter: 100 });
    ch = r.handle;
    if (!r.handle) throw 'no handle';
  });

  let ph;
  await check('createPolyline', async () => {
    const r = await tcp('createPolyline', { points: [{x:0,y:0},{x:500,y:0},{x:500,y:300}] });
    ph = r.handle;
    if (!r.handle) throw 'no handle';
  });

  await check('getEntityInfo', async () => {
    const info = await tcp('getEntityInfo', { handle: ph });
    if (!info.type || info.vertexCount < 2) throw 'bad info';
  });

  await check('setEntityColor', async () => {
    const r = await tcp('setEntityColor', { handle: ch, color: 1 });
    if (r.color !== 1) throw 'color mismatch';
  });

  await check('runLisp (+ 1 2)', async () => {
    const r = await tcp('runLisp', { lisp: '(+ 1 2)' });
    if (r.status !== 'OK' && r.status !== 'queued') throw 'status not OK';
  });

  await check('selectByCriteria (CIRCLE)', async () => {
    const r = await tcp('selectByCriteria', { criteria: 'type', value: 'CIRCLE' });
    if (r.count < 1) throw 'no circles';
  });

  // 清理
  if (ch) await tcp('deleteEntity', { handle: ch }).catch(() => {});
  if (ph) await tcp('deleteEntity', { handle: ph }).catch(() => {});

  console.log();
  console.log('=== 自检完成 ===');
}

main().catch(e => console.log('\nCRASH: ' + e.message));
