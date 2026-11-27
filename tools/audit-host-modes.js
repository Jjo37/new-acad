#!/usr/bin/env node
// audit-host-modes.js — v1.11.0-fix
//
// Guards the dual-host promise (Civil 3D + plain AutoCAD):
//   * every AutoCAD-only code path must stay free of Civil 3D API types, because a JIT-time
//     type resolution failure is thrown at the CALL SITE and cannot be caught by the callee;
//   * the shared helper files (CivilObjectUtils / LookupUtils) are mixed, so the audit only
//     demands that the helpers actually reached from CAD paths stay Civil-free.
//
// Run: node tools/audit-host-modes.js [--verbose]
// Exit code 1 = at least one AutoCAD-only path learned to touch Civil 3D.

const fs = require('fs');
const path = require('path');

const SRC = path.join(__dirname, '..', 'plugin', 'AcBridge-v24', 'src');
const CIVIL_PATTERN = /CivilDocument|CivilApplication|Autodesk\.Civil\.|Aecc[A-Za-z]*Mgd/;

// Whole-file must be Civil-free.
const CAD_FILES = [
  'AcadCommands.cs', 'EditCommands.cs', 'EditCommands.Geo.cs', 'FileFormatCommands.cs',
  'HatchCommands.cs', 'PdfCommands.cs', 'StmCommands.cs', 'SummarizeCommands.cs',
  'TraceCommands.cs', 'UndoJournal.cs', 'HankPalette.cs', 'HankPalette.Tools.cs',
  'HankPalette.Workflows.cs', 'HankPalette.Assisted.cs', 'HankPalette.Binding.cs',
  'HankPalette.Journal.cs', 'JsonRpcProtocol.cs', 'RpcTcpServer.cs', 'FileBoundary.cs',
  'PluginRuntime.cs', 'PluginEntry.cs'
];

// Mixed files whose *selected* members must be Civil-free.
const MIXED = ['DrawingCommands.cs', 'CivilObjectUtils.cs', 'LookupUtils.cs'];

const DRAWING_SAFE = [
  'GetDrawingInfoAsync', 'GetProjectContextAsync', 'SaveDrawingAsync', 'NewDrawingAsync',
  'OpenDrawingAsync', 'UndoDrawingAsync', 'RedoDrawingAsync', 'ListTextsAsync',
  'ListSelectedTextsAsync', 'GetSelectedCivilObjectsInfoAsync', 'ListCivilObjectTypesAsync',
  'CreateCircleAsync', 'CreateEllipseAsync', 'BuildDrawingInfoCore'
];

// Plumbing that plain AutoCAD does execute.
const EXECUTION_SAFE = [
  'ExecuteCoreAsync', 'ExecuteDocumentOpAsync', 'ExecuteInCommandContextAsync',
  'ExecuteSerializedAsync', 'GetHostRecoveryState', 'IsRegeneratingOperation',
  'ClearHostBusy', 'SetHostBusy', 'CaptureHostState'
];

const read = f => fs.readFileSync(path.join(SRC, f), 'utf8');

function methodBodies(src, name) {
  const out = [];
  const re = new RegExp(`(?:public|private|internal|protected)[^;{}()]*\\b${name}\\s*(?:<[^>]*>)?\\s*\\(`, 'g');
  let m;
  while ((m = re.exec(src))) {
    let i = src.indexOf('{', m.index + m[0].length);
    if (i < 0) continue;
    let depth = 0;
    for (let j = i; j < src.length; j++) {
      if (src[j] === '{') depth++;
      else if (src[j] === '}') { depth--; if (depth === 0) { out.push(src.slice(i, j + 1)); break; } }
    }
  }
  return out;
}

let violations = 0;
const lines = [];
const v = msg => { violations++; lines.push('VIOLATION ' + msg); };
const ok = msg => lines.push('ok  ' + msg);

// 1. whole-file CAD paths
for (const f of CAD_FILES) {
  const src = read(f);
  let bad = 0;
  src.split(/\r?\n/).forEach((line, i) => {
    const code = line.replace(/\/\/.*$/, '');
    if (CIVIL_PATTERN.test(code)) { bad++; v(`${f}:${i + 1}: ${line.trim().slice(0, 120)}`); }
  });
  if (!bad) ok(f);
}

// 2. selected members of mixed files
const bodiesOf = {};
for (const name of DRAWING_SAFE) {
  const bodies = methodBodies(read('DrawingCommands.cs'), name);
  if (!bodies.length) { v(`DrawingCommands.${name}: method not found`); continue; }
  bodiesOf[name] = bodies;
  const bad = bodies.filter(b => CIVIL_PATTERN.test(b));
  if (bad.length) v(`DrawingCommands.${name}: touches Civil 3D types`);
  else ok(`DrawingCommands.${name}`);
}

// 3. shared helpers reached from CAD paths (discovered, not hand-maintained)
const usedHelpers = new Set();
const scanForHelpers = text => {
  for (const m of text.matchAll(/\b(CivilObjectUtils|LookupUtils)\.(\w+)/g)) usedHelpers.add(m[1] + '.' + m[2]);
};
for (const f of CAD_FILES) scanForHelpers(read(f));
for (const b of Object.values(bodiesOf).flat()) scanForHelpers(b);

for (const pair of [...usedHelpers].sort()) {
  const [file, name] = pair.split('.');
  const bodies = methodBodies(read(file + '.cs'), name);
  if (!bodies.length) { v(`${pair}: helper not found`); continue; }
  const bad = bodies.filter(b => CIVIL_PATTERN.test(b));
  if (bad.length) v(`${pair}: touched from CAD path but references Civil 3D types`);
  else ok(`${pair} (shared helper)`);
}

// 4. execution plumbing
const exec = read('CivilExecution.cs');
for (const name of EXECUTION_SAFE) {
  const bodies = methodBodies(exec, name);
  if (!bodies.length) { v(`CivilExecution.${name}: method not found`); continue; }
  const bad = bodies.filter(b => CIVIL_PATTERN.test(b));
  if (bad.length) v(`CivilExecution.${name}: touches Civil 3D types`);
  else ok(`CivilExecution.${name}`);
}

const threeArg = [
  ['ExecuteCoreAsync', 'Func<Document, Database, Transaction, T>'],
  ['ReadAsync', 'Func<Document, Database, Transaction, T>'],
  ['WriteAsync', 'Func<Document, Database, Transaction, T>']
];
for (const [name, sig] of threeArg) {
  if (exec.includes(name + '<T>(' + sig)) ok(`CivilExecution.${name}(3-arg, Civil-free)`);
  else v(`CivilExecution.${name}: missing Civil-free 3-argument overload`);
}

// 4. dispatcher must stay host-neutral
const dispatcher = read('CommandDispatcher.cs');
if (CIVIL_PATTERN.test(dispatcher)) v('CommandDispatcher.cs: touches Civil 3D types (must stay host-neutral)');
else ok('CommandDispatcher.cs (host-neutral)');

// 4b. every Assembly.GetTypes() call must survive a partial type load
//     (plain AutoCAD cannot load the Civil-typed classes; an unhandled ReflectionTypeLoadException
//     once collapsed listMethods from ~420 entries to the 23 hardcoded ones)
for (const f of fs.readdirSync(SRC).filter(x => x.endsWith('.cs'))) {
  const src = read(f);
  if (!/GetTypes\(\)/.test(src)) continue;
  if (/ReflectionTypeLoadException/.test(src)) ok(`${f}: GetTypes() guarded against partial type load`);
  else v(`${f}: GetTypes() without ReflectionTypeLoadException rescue (will throw on plain AutoCAD)`);
}

// 6. the four-argument wrappers must exist (otherwise Civil 3D silently loses the document)
if (!/Func<Document, CivilDocument, Database, Transaction, T> action, bool write\)\r?\n\s*=> ExecuteCoreAsync\(\(doc, database, transaction\) => action\(doc, CivilSupport\.RequireDocument\(\)/.test(exec)) {
  v('CivilExecution.ExecuteAsync(4-arg): expected thin wrapper around ExecuteCoreAsync + CivilSupport.RequireDocument()');
} else ok('CivilExecution.ExecuteAsync(4-arg) is a thin Civil wrapper');

const verbose = process.argv.includes('--verbose');
console.log((verbose ? lines : lines.filter(l => l.startsWith('VIOLATION'))).join('\n') || 'all checks passed');
console.log(`\nsummary: ${violations} violation(s); ${CAD_FILES.length} whole files, ${DRAWING_SAFE.length} drawing methods, ${usedHelpers.size} shared helpers, ${EXECUTION_SAFE.length} execution methods audited`);
process.exit(violations === 0 ? 0 : 1);
