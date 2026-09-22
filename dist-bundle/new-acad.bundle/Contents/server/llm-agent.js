// llm-agent.js — relay 内置 LLM 代理（形态 3）
// 2026-08-06 S1 新增。纯新增模块，不依赖 relay 现有代码。
// 2026-08-07 变强方案 Phase1-3: 记忆分层+准入原则 / 知识库+工作流模板 / 元认知护栏+模型分流
// 功能: 面板消息 → LLM API（OpenAI 兼容, tools=cadCall）→ 循环执行工具调用 → 最终回复
// 安全: AI 唯一工具 = cadCall（cad-tools.js），物理上没有文件/exec 能力

'use strict';

const fs = require('fs');
const path = require('path');

const { CAD_CALL_TOOL, cadCall, formatResult, mcpCall, MCP_CALL_TOOL, MCPCALL_ALLOW } = require('./cad-tools.js');
const { compactMessages } = require('./compact.js'); // 2026-08-14: 上下文压缩（超限主动摘要，防膨胀超时）
const { readFile, writeFile, deleteFile, listDir, readImage, traceImage, FILE_TOOLS, getFileTools, getAgentWorkDir } = require('./file-tools.js');

// ---------- 长期记忆（2026-08-07 新增） ----------
// 分层: 核心记忆（每轮注入 ≤4KB）+ 知识库（按需 readKnowledge）
// 准入原则: 只存 规则/习惯/方法；具体任务数据、图纸内容禁止入记忆
const MEMORY_DIR = path.join(__dirname, 'memory');
const MEMORY_FILE = path.join(MEMORY_DIR, 'agent-memory.md');
const KNOWLEDGE_FILE = path.join(MEMORY_DIR, 'knowledge.md');
const MEMORY_MAX = 8000;        // 记忆文件最大字节（写入截断）
const MEMORY_INJECT_MAX = 4000; // 每轮注入上限（防撑上下文）
const KNOWLEDGE_MAX = 16000;    // 知识库最大字节

function loadMemory() {
  try {
    if (!fs.existsSync(MEMORY_FILE)) return '';
    return fs.readFileSync(MEMORY_FILE, 'utf8');
  } catch (_) { return ''; }
}

// 2026-08-07 框架: 记忆检索化——按 ## 分节 + 消息关键词匹配，注入最相关节（记忆可无限大，不全文塞）
function loadMemoryRelevant(text) {
  const full = loadMemory();
  if (!full) return '';
  const t = String(text || '').toLowerCase();
  const sections = full.split(/\n##\s+/).filter(s => s.trim().length > 0);
  if (sections.length <= 1) return full.slice(0, MEMORY_INJECT_MAX);   // 单节: 直接截断注入
  const scored = sections.map(sec => {
    const low = sec.toLowerCase();
    let score = 0;
    // 中文关键词提取：分隔符切 + 2字滑动窗口（长句也能命中）
    const words = new Set();
    const segs = t.split(/[\s,，。；;、:：()（）"'!！?？]/).filter(s => s.length >= 2);
    for (const seg of segs) {
      words.add(seg);
      if (seg.length > 2) { for (let i = 0; i <= seg.length - 2; i++) words.add(seg.slice(i, i + 2)); }
    }
    for (const w of words) if (low.includes(w)) score++;
    return { sec, score };
  });
  const hits = scored.filter(s => s.score > 0).sort((a, b) => b.score - a.score);
  if (hits.length > 0) {
    // 命中节 + 附各节索引（让 AI 知道还有什么可以 readMemory）
    const idx = scored.map(s => { const head = s.sec.split('\n')[0].trim(); return head; }).filter(Boolean);
    return '（按需注入相关节）\n' + hits.slice(0, 3).map(h => '## ' + h.sec.trim()).join('\n\n') +
      '\n\n📋 记忆全部节索引：' + idx.join(' | ') + '（需要某节全文用 readMemory）';
  }
  return full.slice(0, MEMORY_INJECT_MAX);   // 无命中: 注入前面
}

function saveMemory(content) {
  try {
    fs.mkdirSync(MEMORY_DIR, { recursive: true });
    const text = String(content == null ? '' : content).slice(0, MEMORY_MAX);
    fs.writeFileSync(MEMORY_FILE, text, 'utf8');
    return { ok: true, bytes: Buffer.byteLength(text) };
  } catch (e) {
    return { ok: false, error: e.message };
  }
}

function loadKnowledge() {
  try {
    if (!fs.existsSync(KNOWLEDGE_FILE)) return '';
    return fs.readFileSync(KNOWLEDGE_FILE, 'utf8').slice(0, KNOWLEDGE_MAX);
  } catch (_) { return ''; }
}

// 知识库索引（标题行，注入 system 让 AI 知道有什么，全文按需 readKnowledge）
function loadKnowledgeIndex() {
  try {
    const k = loadKnowledge();
    if (!k) return '';
    const heads = k.split('\n').filter(l => /^#{1,3}\s+/.test(l)).map(l => l.trim()).join(' | ');
    return (heads || '（知识库为空）').slice(0, 400);
  } catch (_) { return ''; }
}

// 2026-08-10: 结构化任务计划（单例，跨轮保留，新 createPlan 覆盖；撞线续接/进度跟踪用）
let activePlan = null;

function planToString(p) {
  if (!p) return '';
  const lines = p.steps.map((s, i) => {
    const mark = s.status === 'done' ? '[x]' : s.status === 'doing' ? '[>]' : s.status === 'failed' ? '[!]' : '[ ]';
    return (i + 1) + '. ' + mark + ' ' + s.text + (s.note ? '（' + s.note + '）' : '');
  });
  return '目标：' + p.goal + '\n' + lines.join('\n');
}

const PLAN_TOOLS = [
  {
    type: 'function',
    function: {
      name: 'createPlan',
      description: '复杂任务开始时创建结构化计划（覆盖旧计划）。多步任务（≥3 个依赖步骤）建议先建计划再执行，每完成一步用 updatePlan 更新状态。',
      parameters: {
        type: 'object',
        properties: {
          goal: { type: 'string', description: '任务目标（用户原话或一句话概括）' },
          steps: { type: 'array', items: { type: 'string' }, description: '有序步骤（≤10 步，每步一句话）' }
        },
        required: ['goal', 'steps']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'updatePlan',
      description: '更新计划步骤状态：index=第几步（从 0 开始），status=doing/done/failed，note 可选补充说明',
      parameters: {
        type: 'object',
        properties: {
          index: { type: 'integer' },
          status: { type: 'string', enum: ['pending', 'doing', 'done', 'failed'] },
          note: { type: 'string' }
        },
        required: ['index', 'status']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'getPlan',
      description: '查看当前计划状态（做到哪步/剩余步骤）。撞线续接或不确定进度时调用。',
      parameters: { type: 'object', properties: {} }
    }
  }
];

const MEMORY_TOOLS = [
  {
    type: 'function',
    function: {
      name: 'writeMemory',
      description: '把重要信息写入长期记忆（覆盖式更新全文）。准入原则：只写 规则类（红线/规范/标准）、用户习惯（偏好/风格/常用设置）、工具方法（用法/流程/教训）；禁止写具体任务数据/图纸/一次性上下文。写入前先 readMemory 看现有内容。',
      parameters: {
        type: 'object',
        properties: {
          content: { type: 'string', description: '记忆全文（markdown），按 用户偏好/规则规范/工具方法 分节，自包含可独立阅读' }
        },
        required: ['content']
      }
    }
  },
  {
    type: 'function',
    function: {
      name: 'readMemory',
      description: '读取长期记忆全文（每轮已自动注入，一般无需调用；用于写入前确认现有内容）',
      parameters: { type: 'object', properties: {} }
    }
  },
  {
    type: 'function',
    function: {
      name: 'readKnowledge',
      description: '读取内置知识库全文（前提核查点 + 注意事项；方法查找靠 listMethods filter，高频方法自带描述）。复杂任务（算量/曲面/批量修改/导出等）先调用本工具核对前提，再决定调什么方法。',
      parameters: { type: 'object', properties: {} }
    }
  }
];

// 复杂任务关键词（模型分流用）
const COMPLEX_KEYWORDS = ['算量', '工程量', '曲面', '廊道', '放坡', '管网', '分析', '统计', '导出', '批量', '三维', '3d', '生成', '报告', '质检', '造价', '标注'];
function isComplexTask(text, messages) {
  const t = String(text || '').toLowerCase();
  if (COMPLEX_KEYWORDS.some(k => t.includes(k))) return true;
  // 上下文较大也算复杂（多轮工具循环）
  try { return JSON.stringify(messages || []).length > 6000; } catch (_) { return false; }
}

// 方法 → 中文动作映射（撞线/进度汇报的自然语言兑底；高频方法覆盖 90% 场景）
const ACTION_MAP = {
  listMethods: '获取可用方法清单', getDrawingInfo: '读取图纸信息', getCivil3DHealth: '检查插件状态',
  getSelection: '读取选中图元', saveSelection: '捕获选择集', getEntityInfo: '读取图元属性',
  setEntityColor: '修改图元颜色', mirrorEntity: '镜像图元', undoDrawing: '撤销上一步', redoDrawing: '重做',
  createCircle: '创建圆', createPolyline: '创建多段线', createLineSegment: '创建直线',
  createText: '创建文字', createMText: '创建多行文字', create3dPolyline: '创建三维多段线',
  createBox: '创建盒体', createCylinder: '创建圆柱体', createSphere: '创建球体', createCone: '创建圆锥体',
  createWedge: '创建楔体', createTorus: '创建圆环体',
  alignEntity: '对齐图元', arrayEntity: '阵列图元', array3d: '三维阵列',
  booleanUnion: '布尔并集', booleanSubtract: '布尔差集', breakEntity: '打断图元',
  copyEntity: '复制图元', explodeEntity: '炸开图元', extendEntity: '延伸图元',
  filletEntities: '圆角', chamferEntities: '倒角', joinEntities: '合并图元',
  offsetEntity: '偏移图元', trimEntity: '修剪图元', moveEntity: '移动图元',
  rotateEntity: '旋转图元', scaleEntity: '缩放图元', stretchEntity: '拉伸图元',
  getLayers: '读取图层列表', layerOff: '关闭图层', layerFreeze: '冻结图层', layerLock: '锁定图层',
  layerUnlock: '解锁图层', layerIsolate: '孤立图层', layerMatch: '匹配图层',
  writeBlock: '写块', dimLinear: '线性标注', dimAligned: '对齐标注', dimAngular: '角度标注',
  dimRadius: '半径标注', dimDiameter: '直径标注', measureArea: '测量面积', measureDist: '测量距离',
  createSurface: '创建曲面', addSurfacePoints: '向曲面添加高程点', addSurfaceBreakline: '添加断裂线',
  addSurfaceBoundary: '添加曲面边界', listSurfaces: '查询曲面列表', getSurfaceStatistics: '读取曲面统计',
  getSurfaceElevation: '读取曲面高程', extractSurfaceContours: '提取等高线', deleteSurface: '删除曲面',
  listAlignments: '查询路线列表', createAlignment: '创建路线', alignmentStationToPoint: '路线桩号转坐标',
  listProfiles: '查询纵断面列表', createProfileFromSurface: '由曲面生成纵断面',
  listCorridors: '查询廊道列表', rebuildCorridor: '重建廊道', computeCorridorVolumes: '计算廊道体积',
  addCorridorSurface: '一键给廊道加曲面（算量用）',
  listPipeNetworks: '查询管网列表', createPipeNetwork: '创建管网', addPipeToNetwork: '添加管道',
  checkPipeNetworkInterference: '管网碰撞检查',
  listGradingGroups: '查询放坡组', createGradingGroup: '创建放坡组', createGrading: '创建放坡',
  getGradingGroupVolume: '计算放坡体积',
  listParcels: '查询地块列表', createParcel: '创建地块', reportParcels: '生成地块报告',
  listCogoPoints: '查询点列表', createCogoPoints: '创建点', importCogoPoints: '导入点', exportCogoPoints: '导出点',
  qtyParcelAreas: '计算地块面积', qtySurfaceVolume: '计算曲面体积', qtyCorridorVolumes: '计算廊道体积',
  qtyAlignmentLengths: '计算路线长度', qtyEarthworkSummary: '土方量汇总', qtyExportToCsv: '导出工程量 CSV',
  qcCheckDrawingStandards: '图纸标准检查', qcCheckSurface: '曲面质检', qcReportGenerate: '生成质检报告',
  readFile: '读取文件', writeFile: '写入文件', listDir: '列目录', deleteFile: '删除工作区文件',
  mcpCall: '调用高层工具', readMemory: '读取记忆', writeMemory: '写入记忆', readKnowledge: '查询知识库'
};
function describeCall(c) {
  const a = ACTION_MAP[c.method];
  if (a) return a;
  // 未知方法：尝试去前缀/驼峰拆词成中文可读描述
  return String(c.method || '未知操作');
}

const SYSTEM_PROMPT = `你当前在 CAD 面板会话（channel=cad-panel）。

🔴 能力边界（安全设计，物理限制）：
- 主要工具是 cadCall（调用 C3D 插件方法）；另有 mcpCall（MCP 高层向导）、文件工具（readFile 读 / listDir 列目录 / writeFile+deleteFile 限工作区）、记忆与知识库工具（readMemory/writeMemory/readKnowledge）——详见下文各节
- 不能执行任意系统命令；文件写入/删除仅限 {{WORKDIR}} 工作区（用户文件不可改删，物理边界）

工作流程：
1. 先调 listMethods 确认方法名（可带 filter 参数精确查：{"method":"listMethods","params":{"filter":"surface"}}；分组摘要不够时再 filter），禁止猜方法名；知识库"已知不支持"清单里的功能直接报 [MISSING]
2. 调通 → 简短回复结果（如: ✅ 圆已创建 handle=xxx）
3. 调不通/方法不存在/参数对不上 → 立即停止，回复格式：
   [MISSING] 功能名: xxx
   期望行为: xxx
   尝试记录: 调了 xxx 方法，返回 xxx
4. 有依赖关系的操作必须串行：等前一步返回成功后再发下一步（并发会排队卡死 25s 超时）
4.5. 🎫 后台任务（job）：重操作（批量读块属性/批量导出/曲面体积/DEM 导入/质检报告/廊道重建）返回 {jobId, state:"running"} 是**正常排队**，不是失败！用 getJobStatus{jobId} 轮询（每 5-10s 一次，轮询不占 CAD 上下文）直到 state=completed/failed；期间不要再发其他 CAD 操作（会收到 JOB_RUNNING 拒绝，等 job 完再继续）。cancelJob{jobId} 可取消。
   5. 遇到 HOST_BUSY（"CAD 主机忙/上下文被占用"）或 JOB_RUNNING（"后台任务运行中"）：说明有操作超时/后台重任务占用命令上下文，约 60 秒内恢复。
      禁止连续重试同一方法（会全部失败浪费轮次）。正确做法：
      - 如果任务可拆分：改用不需要 CAD 上下文的方法（如 getJobStatus/listMethods 过滤/读缓存），或直接询问用户稍等；
      - 如果必须等：明确告诉用户"CAD 忙，约 1 分钟后自动恢复，请稍等或点取消重新布置"；
      - 同一方法最多重试 1 次（间隔 1 轮），仍失败就停下向用户说明，不硬撞。
5. 🗣️ 汇报必须用自然语言（最高优先，用户视角）：
   - 描述"动作和目的"，禁止出现方法名/handle/工具名（说"已读取你选中的 3 条范围线"，不说"调用了 getEntityInfo"）
   - 做之前："我先读取你选中的图元，确认范围线"
   - 过程中："正在创建曲面，已加入 120 个高程点"
   - 完成："✅ 3 条范围线面积已算出，报告已导出到桌面"
   - 卡住/异常固定格式：
     遇到问题：xxx（大白话描述，如"选中集是空的"）
     我需要你：xxx（如"请先框选范围线" / "确认用哪个图层" / "换个说法"）
   - 禁止列工具调用清单
6. 同一方法调用失败 >=2 次 → 该方法会被物理封锁（本任务内禁止再调用，换参数也算）。停止重试，记录原因，换方法；没有替代方案就向用户说明情况并询问（禁止继续猜参数）
7. 复杂指令（>=3 个依赖步骤）→ 执行中如果接近轮次上限仍未完成，在最终回复中报告：
8. 复杂/多步任务（>=3 个依赖步骤，如导出+转换+统计、建曲面+路线+廊道）→ 先调用 createPlan 建结构化计划（目标+有序步骤），每完成一步 updatePlan 更新状态（撞线续接/进度跟踪靠它）；简单任务（1-2 步）不用建
9. 用户意图不明确（范围/对象/位置/数据口径不清楚）→ 先问用户确认（给具体选项，如："A 只算选中的 141 个 / B 全图层 183 个"），禁止猜着干
   📋 已完成的步骤（自然语言）
   📋 剩余工作流（后续步骤，自然语言）
   提示用户"回复 继续 可接着做"（会话历史保留）

📁 本地文件工具（readFile / listDir / writeFile / deleteFile / readImage / traceImage + 插件 writeTextFile）：
- traceImage: **位图 → CAD 矢量线条**（用户贴图说"画进 CAD/照着描/变成线条"时用它）。**mode 可以不传**（自动判定：扁平/纯色/卡通/logo/矢量壁纸 → flat；线稿/手绘/照片 → arc）。要指定：flat=色块区域(★扁平图首选，线最少最好看) / arc=圆弧拟合 / centerline=骨架细线(好编辑) / outline=描边(保笔画) / posterize=全色块含背景。要落地**直接 draw:true**（工具自己建图层、批量画完报成功/失败），**别自己循环 createPolyline**；scaleTo 默认缩到宽 100、offsetX/Y 避让已有图形。支持 PNG/BMP/JPEG/GIF/TIFF（非 PNG/BMP 自动走插件 exportImageGray 解码）。❌ 禁用"手工猜坐标逐条 createPolyline" —— 那是烧轮次还不准
- 🖼️ **图片 / 扫描稿 → CAD 分诊（先判定再动手，别跳）**：用户给图或给路径要求画进图纸时，先看图上有什么：
  ①**有尺寸标注** → 走「标注驱动重建」：**先列尺寸表**（每段/总长/门窗位置，落 JSON）→ 尺寸链闭合自检（分段和=总长）→ 不确定的项问用户 → 按真实尺寸画（x=0 锚点+相对尺寸）→ 画完回读量一遍对账。**不许照像素描**
  ②**无标注但有标定线索**（图框比例尺 1:100 / 网格 / 坐标标注）→ 用线索标定后重建
  ③**完全没尺寸** → **必问一次**："图上有没有哪一段长度你知道？哪怕一个。"给三个默认选项：按图比例画（默认，形状准但真实尺寸未知）/ 给一个参考尺寸（单点标定→整图缩放到真实单位）/ 给一个目标总尺寸。**绝不硬猜尺寸**；**只问这一次**，别反复问
  ④**不是工程图**（照片/实物/插画/示意图）→ 照形描（traceImage）或按用户给的目标总尺寸缩放，并**如实说明这是示意图**，不假装是施工图
  铁律：坐标只来自「标注 / 用户给的尺寸」；像素几何只用于识别形状与拓扑；读不准的尺寸必须标出来问。详见知识库 knowledge/image-to-cad.md（项目根目录或安装目录下的 knowledge 文件夹）
- listDir: 列目录内容（子目录+文件名/大小）——用户给文件夹路径时先调这个看里面有什么，再针对性读文件
- readFile: 读本地任意文本文件（文本/CSV/JSON，格式不限），用于读取用户数据/配置/报告。⚠️ 内容将发送给 AI 模型服务商处理；敏感文件（config.json/.env/密钥/证书/.git 等）会被工具拒绝
- writeFile: 在 {{WORKDIR}} 工作区内创建/覆盖文件（生成中间数据/报告）
- deleteFile: 删除 {{WORKDIR}} 工作区内自己生成的文件（清理临时/废弃文件）
- readImage: 读图片并让模型查看（需模型支持视觉；不支持时 API 会报错，如实告知用户即可，不要反复重试）
- 插件文档读取（走 cadCall）: readDocx/readDoc{path} / readXlsx/readXls{path,maxRows}（.doc/.xls 老格式也支持，插件自带解析） / readPptx{path} / readZip{path,entry?}——Word/Excel/PPT/压缩包这类二进制格式用它们读（readFile 读不了二进制）；openDrawing{path,readOnly?} 打开已有图纸（半自动多图工作流：openDrawing→操作→saveDrawing→下一张，切图时当前图自动保存）
- writeTextFile（插件方法，走 cadCall）: 用户明确要求"生成/保存到某路径"时用（如"笔记生成到 D:\\xxx"）——用户明示=授权写该路径；【只新建不覆盖】已存在则换新文件名或请用户删除旧文件；敏感路径/可执行扩展名禁写
- 读: 任意本地路径（敏感文件黑名单拦截）；写中间文件: file-tools 工作区；写用户指定位置: 仅用户明示时用插件 writeTextFile
- ⚠️ .pdf/.dwg 二进制 readFile 读不了；Office 文档走插件方法：.docx→readDocx、.doc→readDoc、.xlsx→readXlsx、.xls→readXls（自带解析，无需装 Office）

🔴 文件安全红线（最高级，违反=失职）：
- 【读写边界】读：任意路径但敏感文件（配置/密钥/凭据/版本库）黑名单拦截；写/删：仅限 {{WORKDIR}} 工作区
- 【禁止】写/删工作区以外的任何文件（工具物理拒绝，返回安全红线错误）
- 【禁止】写入可执行/脚本文件（.exe/.bat/.ps1/.dll 等，会被工具拒绝）
- 用户要求删除工作区外文件时明确回复:
  "⚠️ 项目安全红线：我只能修改/删除自己生成的工作区文件（{{WORKDIR}}），你的其他文件我不能动。"
- 用户要求"修改 xx 文件/删掉 xx" → 拒绝并解释红线，绝不尝试用其他方式绕过

🔴 禁止编造（最高红线）：
- 每次 cadCall 后必须根据真实返回值回复——成功就说成功（引用实际 handle/数量），失败就说失败（引用实际错误信息）
- 禁止猜测、编造、脑补返回值（如"应该成功了""大概创建了"），禁止编造不存在的调用记录
- 如果返回值异常（如 vertexCount=0）或与预期不符，如实报告异常现象并附实际返回，不要自行解释原因
- 拿不准时：重新调一次确认，或直接如实说"返回了 xx，我不确定是否正常"
- 【实测必须真读】汇报"实测值/已完成/数量"必须真的调用方法拿到返回值；没读到就说"未验证/未知"。禁止拿其他实体属性冒充（例如用圆的半径当标注测量值——标注值要调 getDimensionInfo 读 measurement）

📌 长期记忆（readMemory / writeMemory）——准入原则（2026-08-07 定）：
- 【可写入】规则类（红线/规范/固定标准）、用户习惯（偏好/风格/常用设置）、工具方法（用法/流程模板/抽象教训）
- 【禁止写入】具体任务数据、图纸内容、一次性上下文（算量结果/选中集/某张图的处理过程）——这些只属于本次会话或 exchange/ 工作区，写记忆=污染
- writeMemory 是覆盖式全文更新：写入前先 readMemory 看现有内容，保留仍有价值的旧条目
- 用户明确说"记住 xxx"时，必须评估是否符合准入原则再写

📚 知识库（readKnowledge）：
- 内置方法速查表 + 关键前提核查清单
- 复杂任务先 readKnowledge 查方法和前提核查点，再根据用户实际话语核实前提、现场组织步骤
- ⚠️ 用户自然语言经常不准确（说"选中"可能没选中、说"范围线"可能是别的图层）：**不照搬固定流程**，动手前先核实前提（选中集/图层/数据源/单位），对不上就如实指出，不硬凑

📦 批量优先（硬约束，2026-08-08 精简）：多个同类对象→批量方法/数组参数；>500 数据→文件通道（addSurfacePointsFromFile 等）；**禁止逐个调用**。

🛡️ 护栏（精简）：同一方法失败≥2 次→停，报 [MISSING]；拿不准→问用户。复杂任务按注入的规划执行，撞线时报告进度与剩余步骤。`;

/**
 * 调 OpenAI 兼容 chat/completions（含 tools）
 */
function callLLM(cfg, messages, timeoutMs, model, signal) {
  return new Promise((resolve, reject) => {
    const baseUrl = (cfg.baseUrl || 'https://api.deepseek.com/v1').replace(/\/+$/, '');
    const u = new URL(baseUrl + '/chat/completions');
    const body = JSON.stringify({
      model: model || cfg.model || 'deepseek-chat',
      messages,
      tools: [CAD_CALL_TOOL, MCP_CALL_TOOL, ...getFileTools(), ...MEMORY_TOOLS, ...PLAN_TOOLS],
      tool_choice: 'auto',
      temperature: 0.2
    });
    const isHttps = u.protocol === 'https:';
    const mod = isHttps ? require('https') : require('http');
    const req = mod.request({
      host: u.hostname,
      port: u.port || (isHttps ? 443 : 80),
      path: u.pathname,
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': 'Bearer ' + (cfg.apiKey || ''),
        'Content-Length': Buffer.byteLength(body)
      }
    }, (res) => {
      let data = '';
      res.on('data', (c) => data += c);
      res.on('end', () => {
        if (res.statusCode >= 400) {
          return reject(new Error('LLM API ' + res.statusCode + ': ' + data.slice(0, 500)));
        }
        try { resolve(JSON.parse(data)); } catch (e) { reject(new Error('LLM 响应解析失败: ' + data.slice(0, 300))); }
      });
    });
    req.on('error', (e) => reject(new Error('LLM 请求失败: ' + e.message)));
    req.setTimeout(timeoutMs || 60000, () => { req.destroy(); reject(new Error('LLM 超时 (' + (timeoutMs || 60000) + 'ms)')); });
    // 2026-08-07: 支持取消（AbortController）
    if (signal) {
      if (signal.aborted) { req.destroy(); reject(new Error('任务已取消')); return; }
      signal.addEventListener('abort', () => { req.destroy(); reject(new Error('任务已取消')); }, { once: true });
    }
    req.write(body);
    req.end();
  });
}

/**
 * 单轮消息处理: 消息 → LLM 循环（工具调用 ≤ maxTurns）→ 最终回复文本
 * @param {object} cfg llm 配置段（provider/baseUrl/apiKey/model/smartModel/maxTurns/timeoutMs）
 * @param {Array} history 历史消息数组（[{role:'user'|'assistant', content}], 不含 system）
 * @param {string} text 用户本次输入
 * @returns {Promise<{reply:string, turns:number, calls:Array}>}
 */
let imageMsgIndex = -1; // 2026-08-12 M6: 图片注入消息索引(下一轮替换为文本, 防 base64 每轮重发)

// 2026-09-15 修复: 上下文体积估算——图片以固定权重计入（不再按 base64 真实长度计入）
// 背景: 旧实现用 JSON.stringify(messages).length 统计，把一次性 base64 图片(~650KB)当文本计入，
//       图片注入后下一轮直接撞 CTX_MAX 硬停（实测：499KB PNG → 655KB > 250KB → 视觉功能从未生效）。
// 现在: ① 图片按固定权重计（不撑爆统计）；② 单图体积由 file-tools 的 MAX_IMAGE_B64 约束；③ 文本/工具结果膨胀照常拦截。
const IMG_PART_WEIGHT = 2000; // 单个图片 part 的估算权重(字符)
function estimateMessagesBytes(messages) {
  let n = 0;
  for (const m of messages || []) {
    if (!m) continue;
    const c = m.content;
    if (typeof c === 'string') n += c.length + 24;
    else if (Array.isArray(c)) {
      for (const part of c) {
        if (!part) continue;
        if (part.type === 'image_url') n += IMG_PART_WEIGHT;
        else n += String(part.text || '').length + 24;
      }
    }
    if (m.tool_calls) { try { n += JSON.stringify(m.tool_calls).length; } catch (_) {} }
  }
  return n;
}

async function handleMessage(cfg, history, text, snapshot, onProgress, signal, planNote, onLog) {
  let ctxWarn60 = false, ctxWarn80 = false; // 2026-08-12: 上下文分级预警(60%/80%)
  // 2026-09-15: 代理侧诊断出口（relay 传入 log()）——工具配对/空回复等异常落 relay.log，便于定位
  const logFn = (m) => { try { if (onLog) onLog(String(m).slice(0, 500)); } catch (_) {} };
  let compacted = false;   // 2026-08-14: 上下文压缩只做一次
  const CTX_MAX = 250000; // 上下文硬上限(方案A: 120KB→250KB 匹配 128K 窗口模型)
  // 2026-08-10: 轮数可配（面板设置口子）: 默认 20；0/负数 = 无限模式（配无进展检测+15轮进度推送护栏）
  const rawTurns = Number(cfg.maxTurns);
  const unlimited = Number.isFinite(rawTurns) && rawTurns <= 0;
  const maxTurns = unlimited ? Infinity : (Number.isFinite(rawTurns) && rawTurns > 0 ? rawTurns : 20);
  const NO_PROGRESS_LIMIT = unlimited ? 5 : 6;  // 无限模式收紧防空转（防磨洋工烧钱）；2026-08-13 4→5（4 轮太激进，AI 调查中也被截断）
  // 核心记忆注入（空也占位，让它知道机制存在）
  const kIdx = loadKnowledgeIndex();   // 2026-08-08: 记忆注入已砍（不背记忆包袱，readMemory/writeMemory 按需用）
  const kSection = kIdx ? '\n\n📚 知识库索引（复杂任务先 readKnowledge 取全文）：\n' + kIdx : '';
  // 2026-08-07: 选中集快照注入（任务全程用固化快照，勿中途重新 getSelection；跨图缓存含完整几何）
  let snapSection = '';
  if (snapshot && ((snapshot.handles && snapshot.handles.length > 0) || (snapshot.entities && snapshot.entities.length > 0))) {
    let geom = '';
    if (snapshot.entities && snapshot.entities.length > 0) {
      const brief = snapshot.entities.map(e => {
        if (!e) return '';
        const pos = (e.x !== undefined) ? ('@' + Number(e.x).toFixed(1) + ',' + Number(e.y).toFixed(1)) : '';
        const txt = e.text ? (' "' + String(e.text).slice(0, 20) + '"') : '';
        return (e.type || '') + pos + txt;
      }).filter(Boolean);
      geom = '\n完整几何（已缓存，跨图纸可直接重建）：' + brief.slice(0, 30).join('; ') + (brief.length > 30 ? ' …共' + brief.length + '个' : '');
    }
    snapSection = '\n\n📌 选中集快照（发送时固化，含跨图纸缓存）：\n文档: ' + snapshot.docName + (snapshot.fromCache ? '（缓存，来自上次选中，用户已切图）' : '') +
      '\nhandles: ' + (snapshot.handles || []).join(', ') + geom +
      '\n规则：任务全程用快照 handle/几何，不要中途重新 getSelection（可能已变）；用户要"复制到另一张图/跨图纸/粘贴到"时：基于快照几何数据，先告知用户"已获取几何，请切到目标图后回复继续"，切图后用创建方法重建——不要在旧图复制；如当前文档与快照文档不一致，先说明。';
  }
  // 2026-08-17: 项目上下文——读 config.json project（用户显式声明，空=全局）；注入 system 让 AI 感知 + 项目级数据落 projects/<名>/
  let projectName = '';
  try {
    const cfgPath = path.join(__dirname, '..', 'config.json');
    if (fs.existsSync(cfgPath)) {
      const cj = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
      if (cj && typeof cj.project === 'string') projectName = cj.project.trim();
    }
  } catch (_) {}
  const projRoot = path.join(__dirname, '..', 'exchange', 'projects', projectName);
  const projWork = path.join(projRoot, 'agent-work');
  const projSection = projectName
    ? '\n\n📁 当前项目（用户声明）: ' + projectName + '\n- 项目根目录（绝对路径）: ' + projRoot + '\n- 你的文件工作区（writeFile/deleteFile 实际落盘位置，绝对路径）: ' + projWork + '\n- 用户文件可能也在项目根下（图纸等），用 listDir 查看项目根目录了解全貌\n- 选择缓存/临时文件/任务计划按项目隔离；全局记忆/知识库不受影响；跨项目任务需用户确认'
    : '';
    const workDir = getAgentWorkDir();
  const sysPrompt = SYSTEM_PROMPT.replaceAll('{{WORKDIR}}', workDir) + '\n\n' + kSection + snapSection + projSection + (activePlan ? '\n\n⚠️ 当前任务计划（每完成一步用 updatePlan 更新；新任务 createPlan 自动覆盖旧计划）：\n' + planToString(activePlan) : '') + (planNote || '');

  const messages = [
    { role: 'system', content: sysPrompt },
    ...(history || []),
    { role: 'user', content: text }
  ];

  // 模型分流: 复杂任务且配置了 smartModel 且与默认不同 → 用强模型
  const useSmart = !!(cfg.smartModel && cfg.smartModel !== cfg.model && isComplexTask(text, messages));
  const activeModel = useSmart ? cfg.smartModel : (cfg.model || 'deepseek-chat');

  const calls = [];
  const failCount = new Map();   // 同方法失败次数统计（防重试打转）
  const blockedMethods = new Set();  // 2026-08-10: 连续失败≥2次物理封锁（换参数也算，LLM 无法绕过）
  let turns = 0;
  let lastSuccessTurn = 0;   // 2026-08-08: 最近一次成功工具调用轮次（无进展检测）
  let warned = false;            // 轮次预警只触发一次
  let emptyRetried = false;      // 2026-09-15: 空回复只补一次，防死循环

  while (turns < maxTurns) {
    turns++;
    // 2026-08-12 M5: messages 体积上限——防无限模式几十轮后请求体爆炸(CTX_MAX=250KB 强制收尾)
    const messagesBytes = estimateMessagesBytes(messages); // 2026-09-15: 改用估算函数(图片固定权重)
    // 2026-08-14: 上下文压缩——超过 55% 预算且轮次足够时, 把早期对话摘要成检查点(防膨胀→超时)
    if (!compacted && turns >= 4 && messagesBytes > CTX_MAX * 0.55) {
      const newMsgs = await compactMessages(cfg, messages, signal);
      if (newMsgs && newMsgs.length < messages.length) {
        messages.length = 0;
        messages.push(...newMsgs);
        compacted = true;
        imageMsgIndex = -1;   // 图片消息可能已被摘要, 重置索引
        if (onProgress) {
          try { onProgress({ text: "?? 上下文已压缩（早期对话摘要为检查点，继续执行）" }); } catch (_) {}
        }
      }
    }
    // 2026-08-12: 上下文分级预警——提前告知用户, 避免硬停(60% 提醒 / 80% 强烈建议取消)
    if (messagesBytes > CTX_MAX * 0.8 && !ctxWarn80 && onProgress) {
      ctxWarn80 = true;
      try { onProgress({ text: '⚠️ 上下文已达 ' + Math.round(messagesBytes / 1024) + 'KB(' + Math.round(messagesBytes / CTX_MAX * 100) + '%), 即将超限——建议点「取消任务」后重新布置' }); } catch (_) {}
      messages.push({ role: 'system', content: '⚠️ 上下文即将超限(' + Math.round(messagesBytes / 1024) + 'KB/' + Math.round(CTX_MAX / 1024) + 'KB)。请立即收尾: 总结已完成内容, 不要开始新步骤。' });
    } else if (messagesBytes > CTX_MAX * 0.6 && !ctxWarn60 && onProgress) {
      ctxWarn60 = true;
      try { onProgress({ text: 'ℹ️ 上下文 ' + Math.round(messagesBytes / 1024) + 'KB(' + Math.round(messagesBytes / CTX_MAX * 100) + '%), 若需布置新任务建议先取消当前任务' }); } catch (_) {}
      messages.push({ role: 'system', content: 'ℹ️ 上下文已用 ' + Math.round(messagesBytes / 1024) + 'KB(60%+)。若任务接近完成请收尾; 若用户有新任务需求, 引导用户先取消当前任务。' });
    }
    if (messagesBytes > CTX_MAX) {
      return { reply: '?? 工具循环上下文已超限（' + Math.round(messagesBytes / 1024) + 'KB，上限 ' + Math.round(CTX_MAX / 1024) + 'KB），已停止。建议把大任务拆分成小步骤, 或取消后重新布置。' + (activePlan ? '\n\n?? 当前计划进度：\n' + planToString(activePlan) : ''), turns, calls };
    }
    // 2026-08-08: 无进展检测——连续 N 轮无成功工具调用 → 停（防空转烧轮次，替代撞线焦虑）；无限模式收紧到 4 轮
    if (turns > 2 && turns - lastSuccessTurn >= NO_PROGRESS_LIMIT) {
      // 2026-08-13: 停止时保留 AI 最后的实质汇报（不覆盖成纯停止消息）——用户能看到 AI 卡在哪/在做什么
      let lastAi = '';
      for (let i = messages.length - 1; i >= 0; i--) {
        const m = messages[i];
        if (m.role === 'assistant' && typeof m.content === 'string' && m.content.trim()) { lastAi = m.content.trim(); break; }
      }
      const tail = lastAi ? '\n\n--- AI 最后汇报 ---\n' : '';
      const noProgressPlan = activePlan ? '\n\n⚠️ 当前计划进度：\n' + planToString(activePlan) + '\n\n请基于计划状态决定继续或调整方式。' : '';
      return { reply: (lastAi ? lastAi + tail : '') + '⚠️ 连续 ' + (turns - lastSuccessTurn) + ' 轮无成功进展，已停止（避免空转）。请检查图纸/数据状态，或换一种方式重试。' + noProgressPlan, turns, calls };
    }
    // 2026-08-07: 取消检查（面板取消任务 → abort → 立即停）
    if (signal && signal.aborted) {
      return { reply: '', turns, calls, cancelled: true };
    }
    // 2026-08-07: 8 轮中途预警——若在逐个处理数据，强制改批量（不靠 AI 自觉）
    if (turns === 8) {
      messages.push({ role: 'system', content: '⚠️ 已 ' + turns + ' 轮。若仍在逐个处理数据（每次只处理少量对象），立即改用批量方法（复数方法/数组参数）或文件通道（如 addSurfacePointsFromFile）；数据量大优先文件通道。若接近完成则收尾。' });
    }
    // 2026-08-07: 剩 3 轮时注入预警，让 LLM 提前准备自然语言总结（撞线汇报不再是一串方法名）
    if (!warned && turns >= maxTurns - 3) {
      warned = true;
      messages.push({ role: 'system', content: '⚠️ 还剩 ' + (maxTurns - turns) + ' 轮。若任务未完成，本轮回复请用自然语言总结：已完成什么、还差什么、需要用户做什么（禁止列方法名）' });
    }
    // 2026-08-10: 无限模式每 15 轮兑底推送（LLM content 为空时用户也知道还在跑）
    if (unlimited && turns % 15 === 0 && onProgress) {
      try { onProgress({ text: '持续执行中（第 ' + turns + ' 轮）…' }); } catch (_) {}
    }
    let resp;
    try {
      resp = await callLLM(cfg, messages, cfg.timeoutMs || 120000, activeModel, signal);
    } catch (e) {
      // 2026-09-15: API 400（tool_calls 配对类）时把消息结构快照落日志——只记角色与 id，不记内容
      try {
        const trace = messages.map((m, k) => k + ":" + m.role +
          (m.tool_calls ? "{tc:" + m.tool_calls.map(t => t.id).join(",") + "}" : "") +
          (m.tool_call_id ? "{id:" + m.tool_call_id + "}" : "")).join(" | ");
        logFn("[CTX-TRACE] " + String(e.message).slice(0, 160) + " || " + trace);
      } catch (_) {}
      throw e;
    }
    const msg = resp.choices && resp.choices[0] && resp.choices[0].message;
    if (!msg) throw new Error('LLM 无返回消息: ' + JSON.stringify(resp).slice(0, 300));
    // 2026-08-08: progress 优先 LLM 的 content（真实意图叙述），工具名只兜底
    if (onProgress && msg.content && msg.content.trim()) {
      try { onProgress({ text: msg.content.trim().slice(0, 80) }); } catch (_) {}
    }
    // 2026-09-15 修复: 图片"用后即换"——本次 callLLM 已把图片交给模型, 拿到响应后立刻替换为文本占位
    // （旧实现放在 callLLM 之前 → 图片永远到不了模型; 且体积统计先于替换 → 撞 CTX_MAX 硬停。两坑同修）
    if (imageMsgIndex >= 0 && imageMsgIndex < messages.length && messages[imageMsgIndex] && Array.isArray(messages[imageMsgIndex].content)) {
      const imgPath = (messages[imageMsgIndex].content[0] && messages[imageMsgIndex].content[0].text || '').replace('（系统注入：以下图片为用户要求查看的文件 ', '').replace('，请基于图片内容继续任务）', '');
      messages[imageMsgIndex] = { role: 'user', content: '（图片已查看: ' + imgPath + '——为节省上下文, 后续轮次不再附图片数据, 请基于已获取的信息继续）' };
      imageMsgIndex = -1;
    }

    // 有工具调用 → 执行
    if (msg.tool_calls && msg.tool_calls.length > 0) {
      messages.push(msg); // assistant 的 tool_calls 消息
      // 2026-09-15: 图片 user 消息必须排在本次所有 tool 结果之后——插在工具组中间会被 API 判为
      // "An assistant message with 'tool_calls' must be followed by tool messages" → 整轮 400 失败（实测复现）
      let pendingImageMsg = null;
      for (const tc of msg.tool_calls) {
        if (tc.type !== 'function') {
          // 2026-09-15: 不能静默跳过——每个 tool_call 必须有对应 tool 消息，否则下一轮请求会被 API 400 拒绝
          //（"An assistant message with 'tool_calls' must be followed by tool messages..."）
          messages.push({ role: 'tool', tool_call_id: tc.id, content: '不支持的工具调用类型（type=' + String(tc.type) + '），已忽略，请改用标准 function 调用。' });
          logFn('[TOOLCALL] 非 function 类型已占位: ' + JSON.stringify(tc).slice(0, 200));
          continue;
        }
        const fn = tc.function;
        let resultText;
        if (fn.name === 'readImage') {
          let args = {};
          try { args = JSON.parse(fn.arguments || '{}'); } catch (_) { args = {}; }
          calls.push({ method: fn.name, params: args });
          try {
            const img = readImage(args);
            messages.push({ role: 'tool', tool_call_id: tc.id, content: JSON.stringify({ path: img.path, size: img.size, mime: img.mime, note: '图片已读取，已附加到下一轮消息供查看' }) });
            // 2026-09-15: 先暂存，等本轮所有 tool 消息都入列后再追加（否则顺序错误 → API 400）
            pendingImageMsg = {
              role: 'user',
              content: [
                { type: 'text', text: '（系统注入：以下图片为用户要求查看的文件 ' + img.path + '，请基于图片内容继续任务）' },
                { type: 'image_url', image_url: { url: 'data:' + img.mime + ';base64,' + img.base64 } }
              ]
            };
          } catch (e) {
            messages.push({ role: 'tool', tool_call_id: tc.id, content: '读取图片失败: ' + e.message });
          }
          continue;
        }
        if (fn.name === 'readFile' || fn.name === 'writeFile' || fn.name === 'listDir' || fn.name === 'deleteFile') {
          let args = {};
          try { args = JSON.parse(fn.arguments || '{}'); } catch (_) { args = {}; }
          calls.push({ method: fn.name, params: args });
          try {
            const r = fn.name === 'readFile' ? readFile(args)
              : fn.name === 'listDir' ? listDir(args)
              : fn.name === 'deleteFile' ? deleteFile(args)
              : writeFile(args);
            resultText = (r && r.content && typeof r.content === 'string' && r.content.length > 4000)
              ? JSON.stringify({ ...r, content: r.content.slice(0, 4000), truncated: true, totalChars: r.content.length, note: '内容过长已截断, 可分段读取' })
              : JSON.stringify(r);
          } catch (e) {
            resultText = '文件操作被拒绝: ' + e.message;
          }
          messages.push({ role: 'tool', tool_call_id: tc.id, content: resultText });
          continue;
        }
        if (fn.name === 'traceImage') {
          let args = {};
          try { args = JSON.parse(fn.arguments || '{}'); } catch (_) { args = {}; }
          calls.push({ method: fn.name, params: args });
          try {
            const r = await traceImage(args);      // draw=true 时内部会循环落地（异步）
            resultText = JSON.stringify(r);
          } catch (e) {
            resultText = '描摹失败: ' + e.message + '（PNG/BMP 可直接用；JPEG 请先转 PNG）';
          }
          messages.push({ role: 'tool', tool_call_id: tc.id, content: resultText });
          continue;
        }
        if (fn.name === 'createPlan' || fn.name === 'updatePlan' || fn.name === 'getPlan') {
          let args = {};
          try { args = JSON.parse(fn.arguments || '{}'); } catch (_) { args = {}; }
          calls.push({ method: fn.name, params: args });
          if (fn.name === 'createPlan') {
            const steps = Array.isArray(args.steps) ? args.steps.map(s => String(s).slice(0, 80)).slice(0, 10) : [];
            if (steps.length === 0) resultText = '计划步骤为空，请提供 steps 数组（≤10 步）';
            else {
              activePlan = { goal: String(args.goal || '').slice(0, 120), steps: steps.map(t => ({ text: t, status: 'pending' })), updatedAt: new Date().toISOString() };
              resultText = '计划已创建（' + steps.length + ' 步）。每完成一步用 updatePlan 更新状态。\n' + planToString(activePlan);
            }
          } else if (fn.name === 'updatePlan') {
            if (!activePlan) resultText = '当前无计划，请先 createPlan';
            else {
              const idx = Number(args.index);
              const st = String(args.status || '');
              if (!Number.isInteger(idx) || idx < 0 || idx >= activePlan.steps.length) resultText = 'index 越界（0~' + (activePlan.steps.length - 1) + '）';
              else if (!['pending', 'doing', 'done', 'failed'].includes(st)) resultText = 'status 必须是 pending/doing/done/failed';
              else {
                activePlan.steps[idx].status = st;
                if (args.note) activePlan.steps[idx].note = String(args.note).slice(0, 100);
                activePlan.updatedAt = new Date().toISOString();
                resultText = '已更新：\n' + planToString(activePlan);
              }
            }
          } else {
            resultText = activePlan ? planToString(activePlan) : '当前无计划（简单任务可不建计划）';
          }
          messages.push({ role: 'tool', tool_call_id: tc.id, content: resultText });
          continue;
        }
        if (fn.name === 'readMemory' || fn.name === 'writeMemory' || fn.name === 'readKnowledge') {
          let args = {};
          try { args = JSON.parse(fn.arguments || '{}'); } catch (_) { args = {}; }
          calls.push({ method: fn.name, params: args });
          if (fn.name === 'readMemory') {
            resultText = JSON.stringify({ memory: loadMemory() || '（空）' });
          } else if (fn.name === 'writeMemory') {
            resultText = JSON.stringify(saveMemory(args.content || ''));
          } else {
            resultText = JSON.stringify({ knowledge: loadKnowledge() || '（空）' });
          }
          messages.push({ role: 'tool', tool_call_id: tc.id, content: resultText });
          continue;
        }
        if (fn.name === 'mcpCall') {
          let args = {};
          try { args = JSON.parse(fn.arguments || '{}'); } catch (_) { args = {}; }
          const tool = args.tool || '';
          // 2026-08-14 P1-3: mcpCall 白名单硬拦截（不经 HTTP）
          if (!MCPCALL_ALLOW.has(tool)) {
            calls.push({ method: 'mcpCall:' + tool, blocked: true });
            const bm = '?? 工具 ' + tool + ' 不在 mcpCall 白名单，已拒绝。mcpCall 仅限只读/文档/标准/工作流向导类（' + [...MCPCALL_ALLOW].slice(0, 6).join('/') + ' 等）。原子操作请用 cadCall。';
            messages.push({ role: 'tool', tool_call_id: tc.id, content: bm });
            continue;
          }
          const params = args.parameters || {};
          calls.push({ method: 'mcpCall:' + tool, params });
          try {
            const mr = await mcpCall(tool, params, 30000, signal);
            resultText = formatResult(mr);
          } catch (e) {
            resultText = 'MCP 调用异常: ' + e.message + '（如 MCP 服务器未运行或工具需插件在线。原子操作应走 cadCall）';
          }
          messages.push({ role: 'tool', tool_call_id: tc.id, content: resultText });
          continue;
        }
        if (fn.name === 'cadCall') {
          let args = {};
          try { args = JSON.parse(fn.arguments || '{}'); } catch (_) { args = { method: String(fn.arguments || '').slice(0, 100) }; }
          const method = args.method || '';
          const params = args.params || {};
          if (blockedMethods.has(method)) {
            calls.push({ method, params, blocked: true });
            const bm = '⚠️ 方法 ' + method + ' 已被封锁（本任务内连续失败≥2 次），禁止再调用。请换方法；若卡住，向用户说明情况并询问如何继续，不要继续猜测参数。';
            messages.push({ role: 'tool', tool_call_id: tc.id, content: bm });
            continue;
          }
          calls.push({ method, params });
          if (onProgress) { try { onProgress({ text: describeCall({ method }) }); } catch (_) {} }
          try {
            let pluginResp = await cadCall(method, params, 30000, signal);
            // 2026-08-19 方案3④: job 自动轮询——插件返回 jobId 且 state=running → relay 代 AI 轮询到完成，AI 无感
            // 2026-08-19 方案3④: JOB_RUNNING（后台任务占用命令上下文）→ 自动等待 8s 重试一次（不烧 AI 轮次）
            if (pluginResp && !pluginResp.error && pluginResp.result && pluginResp.result.jobId) {
              const jid = pluginResp.result.jobId;
              const jobTimeoutMs = 300000; // 最多等 5 分钟
              const pollStart = Date.now();
              if (onProgress) { try { onProgress({ text: '⏳ 后台任务已排队(jobId=' + String(jid).slice(0, 8) + ')，自动等待完成…' }); } catch (_) {} }
              while (Date.now() - pollStart < jobTimeoutMs) {
                if (signal && signal.aborted) break;
                await new Promise(r => setTimeout(r, 5000));
                const st = await cadCall('getJobStatus', { jobId: jid }, 15000, signal);
                const stRes = st && !st.error && st.result ? st.result : null;
                if (!stRes) break; // 查询失败，把已有信息交给 AI
                if (stRes.state === 'completed') {
                  pluginResp = { jsonrpc: '2.0', id: 1, result: stRes.result ?? { jobId: jid, state: 'completed' } };
                  break;
                }
                if (stRes.state === 'failed' || stRes.state === 'cancelled') {
                  pluginResp = { jsonrpc: '2.0', id: 1, error: { code: 'CIVIL3D.JOB_' + String(stRes.state).toUpperCase(), message: '后台任务 ' + stRes.state + ': ' + (stRes.result && stRes.result.error ? stRes.result.error : '') } };
                  break;
                }
                if (stRes.progressPercent !== undefined) {
                  if (onProgress) { try { onProgress({ text: '⏳ 后台任务 ' + stRes.progressPercent + '%' + (stRes.currentPhase ? '（' + stRes.currentPhase + '）' : '') }); } catch (_) {} }
                }
              }
            } else if (pluginResp && pluginResp.error && pluginResp.error.code === 'CIVIL3D.JOB_RUNNING') {
              await new Promise(r => setTimeout(r, 8000));
              pluginResp = await cadCall(method, params, 30000, signal);
            }
            resultText = formatResult(pluginResp, method, params);
            // 插件返回 error → 记失败（不计成功）
            if (pluginResp && !pluginResp.error) lastSuccessTurn = turns;
            if (pluginResp && pluginResp.error) {
              const _fc = (failCount.get(method) || 0) + 1;
              failCount.set(method, _fc);
              if (_fc >= 2) {
                blockedMethods.add(method);
                resultText += '（方法 ' + method + ' 已连续失败 ' + _fc + ' 次，已封锁，本任务内禁止再调用（新任务自动解锁）。请换方法；若卡住，向用户说明"遇到问题"并询问如何继续，不要继续猜测参数）';
              }
            }
          } catch (e) {
            resultText = '桥接执行异常: ' + e.message;
            const _fc = (failCount.get(method) || 0) + 1;
              failCount.set(method, _fc);
            if (_fc >= 2) {
                blockedMethods.add(method);
                resultText += '（方法 ' + method + ' 已连续失败 ' + _fc + ' 次，已封锁，本任务内禁止再调用（新任务自动解锁）。请换方法；若卡住，向用户说明"遇到问题"并询问如何继续，不要继续猜测参数）';
              }
          }
        } else {
          resultText = '未知工具: ' + fn.name;
        }
        messages.push({ role: 'tool', tool_call_id: tc.id, content: resultText });
      }
      // 2026-09-15: 配对兜底——任何 tool_call 缺对应 tool 消息就补占位（防整轮 400 失败）
      {
        const answered = new Set();
        for (let k = messages.length - 1; k >= 0; k--) {
          const mm = messages[k];
          if (mm === msg) break;
          if (mm && mm.role === 'tool' && mm.tool_call_id) answered.add(mm.tool_call_id);
        }
        for (const tc of msg.tool_calls) {
          if (!answered.has(tc.id)) {
            messages.push({ role: 'tool', tool_call_id: tc.id, content: '（本工具调用未产生结果，已补占位）' });
            logFn('[TOOLCALL] 补齐缺失的 tool 消息: id=' + tc.id + ' name=' + ((tc.function && tc.function.name) || '?') + ' answered=' + answered.size);
          }
        }
      }
      // 2026-09-15: 工具组就位后再追加图片消息（用后即换的逻辑仍在 callLLM 之后）
      if (pendingImageMsg) {
        messages.push(pendingImageMsg);
        imageMsgIndex = messages.length - 1;
        logFn('[IMAGE] 图片消息已追加到工具组之后: ' + String(pendingImageMsg.content[0].text).slice(0, 60));
      }
      continue; // 把工具结果喂回 LLM
    }

    // 无工具调用 → 最终回复
    const finalText = String(msg.content || '').trim();
    if (!finalText) {
      // 2026-09-15: 空正文且无工具调用——先记 finish_reason 判断是否模型端问题，再给模型一次补答机会
      //（原来直接返回空 → relay 兜底成"AI 未返回内容，请重试"，且该轮动作不进历史 → 模型下一轮记忆错位）
      logFn('[EMPTY] 模型返回空正文: finish_reason=' + ((resp.choices && resp.choices[0] && resp.choices[0].finish_reason) || '?') +
        ' tool_calls=' + ((msg.tool_calls && msg.tool_calls.length) || 0) + ' calls=' + calls.length + ' turns=' + turns);
      if (!emptyRetried && turns < maxTurns && !(signal && signal.aborted)) {
        emptyRetried = true;
        messages.push({ role: 'system', content: '你上一条回复是空的（既无正文也无工具调用）。请立刻用自然语言说明：当前进度、刚完成了什么、下一步做什么。不要重复早先的结论，尤其不要声称仍卡在旧阻塞上——以最近的工具返回为准。' });
        continue;
      }
      const acts = calls.length > 0 ? calls.map((c, k) => (k + 1) + '. ' + describeCall(c)).join('\n') : '';
      return { reply: '⚠️ 模型本轮未返回内容' + (acts ? '（本轮已执行 ' + calls.length + ' 个操作）' : '') + '。回复「继续」我接着做。' + (acts ? '\n\n📋 本轮操作：\n' + acts : ''), turns, calls };
    }
    return { reply: finalText, turns, calls };
  }

  // 到顶: 汇总已调用动作作为"进展"，提示可续做（2026-08-07: 改为自然语言动作描述）
  const doneSteps = calls.length > 0 ? calls.map((c, i) => (i + 1) + '. ' + describeCall(c)).join('\n') : '（尚无成功调用）';
  const planSummary = activePlan ? '\n\n?? 当前计划进度：\n' + planToString(activePlan) : '';
  return {
    reply: '⚠️ 已达到工具调用上限 (' + maxTurns + ' 轮)，已停止。\n\n📋 已完成的步骤：\n' + doneSteps + planSummary + '\n\n📋 剩余工作流：请回复"继续"或补充指令，我会基于已完成部分接着做（会话历史已保留）。',
    turns,
    calls
  };
}

// 2026-09-15: 工具组不变式校验（OpenAI 兼容）——assistant(tool_calls) 之后必须紧跟其全部 tool 响应；
// 中间插入任何非 tool 消息（例如把图片 user 消息塞进工具组中间）会被 API 判为
// "insufficient tool messages following tool_calls message" → 整轮 400（2026-09-15 实测复现并已修）。
function validateToolGroups(messages) {
  const problems = [];
  for (let i = 0; i < messages.length; i++) {
    const m = messages[i];
    if (!m || m.role !== 'assistant' || !m.tool_calls || m.tool_calls.length === 0) continue;
    const need = new Set(m.tool_calls.map(t => t.id));
    for (let k = i + 1; k < messages.length; k++) {
      const n = messages[k];
      if (n && n.role === 'tool') { if (n.tool_call_id) need.delete(n.tool_call_id); continue; }
      break;   // 工具组被非 tool 消息打断 → 之后的响应不再算“紧跟”
    }
    if (need.size > 0) problems.push({ index: i, missing: [...need] });
  }
  return problems;
}

module.exports = { handleMessage, SYSTEM_PROMPT, callLLM, loadMemory, loadMemoryRelevant, saveMemory, loadKnowledge, loadKnowledgeIndex, MEMORY_FILE, KNOWLEDGE_FILE, clearActivePlan, hasActivePlan, estimateMessagesBytes, validateToolGroups };

// 2026-08-11: 供 relay 外部调用（/plan/clear 端点 + /clear all 联动）
function clearActivePlan() { activePlan = null; }
function hasActivePlan() { return activePlan !== null; }
