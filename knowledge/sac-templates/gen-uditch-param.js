// parameterise the KNOWN-GOOD 22-point XAML: keep structure identical, replace only the Delta values with expressions
const fs = require('fs');
const SRC = 'D:/new-acad/exchange/projects/ROAD REHABILITATION OF MUNICIPIO ROADS JCT A-16 BOBONARO TO LOLOTOE TOWN STA. 0+000 – 8+900 (8.9 KM)/agent-work/ref-full.xaml';
const OUT = 'D:/new-acad/knowledge/sac-templates/UDitch-param.xaml';
let s = fs.readFileSync(SRC, 'utf8');

const PARAMS = [
  ['HalfCarriage', '2.5', 'Half Carriageway Width', 'Carriageway half width, centreline to ETW'],
  ['ShoulderWidth', '1', 'Shoulder Width', 'Shoulder width, ETW to ditch top'],
  ['DriveFall', '-0.03', 'Carriageway Fall', 'Carriageway cross fall (negative falls away from centreline)'],
  ['ShoulderFall', '-0.04', 'Shoulder Fall', 'Shoulder cross fall'],
  ['Layer1', '0.03', 'Layer 1 Thickness', 'HRS-WC 30 mm'],
  ['Layer2', '0.035', 'Layer 2 Thickness', 'HRS-Base 35 mm'],
  ['Layer3', '0.2', 'Layer 3 Thickness', 'Aggregate Base Class A 200 mm'],
  ['Layer4', '0.3', 'Layer 4 Thickness', 'Aggregate Subbase Class B 300 mm'],
  ['DitchW', '1.4', 'Ditch Outer Width', 'Outside width of the U-ditch'],
  ['DitchInnerW', '0.8', 'Ditch Inner Width', 'Clear inside width'],
  ['DitchBottomT', '0.3', 'Ditch Bottom Thickness', 'Bottom slab thickness'],
  ['DitchNetH', '1', 'Ditch Net Depth', 'Clear inside depth']
];
const HC = 'HalfCarriage', SW = 'ShoulderWidth', DF = 'DriveFall', SF = 'ShoulderFall';
const yTop = `(${HC} * ${DF} + ${SW} * ${SF})`;
const sumL = 'Layer1 + Layer2 + Layer3 + Layer4';
const TW = '((DitchW - DitchInnerW) / 2)';
// 每点的 (DeltaX, DeltaY) —— 均为"相对前一个点"
const DELTAS = [
  ['0', '0'],                                                                     // P1
  [`[${HC}]`, `[${HC} * ${DF}]`],                                                 // P2
  [`[${SW}]`, `[${SW} * ${SF}]`],                                                 // P3
  [`[-(${HC} + ${SW})]`, `[-Layer1 - ${yTop}]`],                                  // P4
  [`[${HC}]`, `[${HC} * ${DF}]`],                                                 // P5
  [`[${SW}]`, `[${SW} * ${SF}]`],                                                 // P6
  [`[-(${HC} + ${SW})]`, `[-Layer2 - ${yTop}]`],                                  // P7
  [`[${HC}]`, `[${HC} * ${DF}]`],                                                 // P8
  [`[${SW}]`, `[${SW} * ${SF}]`],                                                 // P9
  [`[-(${HC} + ${SW})]`, `[-Layer3 - ${yTop}]`],                                  // P10
  [`[${HC}]`, `[${HC} * ${DF}]`],                                                 // P11
  [`[${SW}]`, `[${SW} * ${SF}]`],                                                 // P12
  [`[-(${HC} + ${SW})]`, `[-Layer4 - ${yTop}]`],                                  // P13
  [`[${HC}]`, `[${HC} * ${DF}]`],                                                 // P14
  [`[${SW}]`, `[${SW} * ${SF}]`],                                                 // P15
  ['[0]', `[${sumL} - DitchNetH - DitchBottomT]`],                                // P16
  ['[DitchW]', '[0]'],                                                            // P17
  ['[0]', '[DitchNetH + DitchBottomT]'],                                          // P18
  [`[-${TW}]`, '[0]'],                                                            // P19
  ['[0]', '[-DitchNetH]'],                                                        // P20
  ['[-DitchInnerW]', '[0]'],                                                      // P21
  ['[0]', '[DitchNetH]']                                                          // P22
];

// 1) 替换 Delta 值（按出现顺序）
let ix = 0, iy = 0;
s = s.replace(/(x:Key="DeltaX1">)([^<]*)(<)/g, (m, a, v, c) => {
  const e = DELTAS[ix] ? DELTAS[ix][0] : v; ix++; return a + e + c;
});
s = s.replace(/(x:Key="DeltaY1">)([^<]*)(<)/g, (m, a, v, c) => {
  const e = DELTAS[iy] ? DELTAS[iy][1] : v; iy++; return a + e + c;
});
console.log('DeltaX replaced=' + ix + ' DeltaY replaced=' + iy);

// 2) 根元素加参数属性
const rootEnd = s.indexOf('\n xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"');
if (rootEnd < 0) { console.log('ABORT root anchor'); process.exit(1); }
const attrs = PARAMS.map(([n, d]) => ` this:Subassembly.${n}="${d}"`).join('');
s = s.slice(0, rootEnd) + attrs + s.slice(rootEnd);

// 3) x:Members 加属性（插在最后一个 </x:Property> 之后）
const membersClose = s.indexOf('  </x:Members>');
if (membersClose < 0) { console.log('ABORT members anchor'); process.exit(1); }
const props = PARAMS.map(([n, , dn, dd]) => [
  `    <x:Property Name="${n}" Type="InArgument(x:Double)">`,
  '      <x:Property.Attributes>',
  '        <asw:EnabledFlag2Attribute EnabledFlag="True" />',
  `        <asw:DisplayName2Attribute DisplayName="${dn}" />`,
  `        <asw:Description2Attribute Description="${dd}" />`,
  '      </x:Property.Attributes>',
  '    </x:Property>'
].join('\n')).join('\n');
s = s.slice(0, membersClose) + props + '\n' + s.slice(membersClose);

fs.writeFileSync(OUT, s, 'utf8');
console.log('wrote ' + OUT + ' (' + Buffer.byteLength(s, 'utf8') + ' bytes)');
console.log('points expected=' + DELTAS.length);
