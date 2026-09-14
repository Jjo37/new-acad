# new-acad

> An **in-app AI engineering assistant** for AutoCAD / Civil 3D — it brings its own brain and talks to CAD directly; you just bring a model key

<img width="2559" height="1527" alt="7737cd704fa6285a2eee404c4bcbb29b" src="https://github.com/user-attachments/assets/44222c84-59b7-42f3-ac08-f2b4a359d69b" />

[中文](README.md) | **English**

This is not "yet another pile of CAD tools for an AI". It is **an agent that understands Civil 3D engineering practice, living inside CAD**.
The built-in agent exposes **exactly 2 tools** to the model (call the plugin, call MCP) — **none of the 398 CAD methods are handed to the model directly**. A layer of engineering judgement decides **which channel** a task should take.

| Layer | What it is | Why it matters |
|---|---|---|
| **Brain** | Built-in standalone agent: own LLM proxy + context compaction + long-term memory | **No external platform** — no vendor cloud, no Claude Desktop, no gateway. Just your own API key |
| **Nerves** | Palette ↔ plugin **persistent TCP channel** (with a **job channel** and a **file channel**) | Built for real CAD work: multi-minute corridor rebuilds queue up instead of timing out; >500 records go through a file, not the chat |
| **Socket** | **274** MCP tools (`:3000`) | A compatibility exit: any MCP client (Claude Desktop, Cursor…) can plug in. **Our own palette and agent don't use it** |

> **About the UI language**: I'm a Chinese developer. When I built this plugin my head was entirely in "how will Chinese users use it" — it genuinely never crossed my mind that language was even a thing. Then someone picked English in the installer and got a fully Chinese UI; that's how I found out.
> Fixed in v1.4.0: **whatever language you choose in the setup wizard is what you get** — and you can switch it any time from inside the palette.
> The UI currently ships in **Chinese and English only** (no third language). The "Automatic (follow system)" rule: Chinese system → Chinese; any other language → English.

---

## What is this

new-acad is made of three parts:

| Component | Path | What it does |
|-----------|------|--------------|
| **C3D plugin** (C#) | `plugin/AcBridge-v24/` | Runs inside the AutoCAD / Civil 3D process and executes the real drafting and Civil 3D APIs. Exposes a TCP JSON-RPC service (default `:8080`). |
| **Palette + relay** (Node) | `plugin/AcBridge-v24/src/HankPalette.cs`, `server/` | The palette lives inside Civil 3D. The relay is the hub: it forwards your natural-language request to the LLM, turns the LLM's tool calls into plugin calls, and streams results back to the palette. |
| **MCP server** (Node + TypeScript) | `server/mcp/` | 270 MCP tools on `:3000` — usable from any MCP-capable client, not just this palette. |

### Two AI modes

| Mode | Description |
|------|-------------|
| `llm` (**default**) | The relay ships its own LLM agent (OpenAI-compatible). Bring your own API key and it runs **without any external platform**. The installer wizard supports DeepSeek / OpenAI / Qwen (DashScope) / Kimi / Zhipu / MiniMax / OpenRouter / xAI / custom endpoints. |
| `openclaw` | Point the palette at your own agent platform / gateway (advanced, needs your own token + session config). |

---

## Capabilities

| Area | What you can do |
|------|-----------------|
| **Entities** | Inspect selection, read properties, filter by layer / block / type / elevation range; change color and layer, move, rotate, scale, copy, mirror, delete; batch endpoints take an array of handles and process N objects in one call |
| **Drafting** | Lines, circles, arcs, polylines, rectangles, text, dimensions, hatches |
| **3D** | Primitives (box / cylinder / sphere / cone / wedge / torus); extrude, revolve, sweep, loft (real API chain); NURBS surface creation & control-point editing, surface thickening; boolean union / subtract (intersection emulated via double subtraction), interference check; volume and mass properties |
| **Civil 3D domains** | **Surfaces** (create, bulk point import via the file channel); **alignments** (styles, label sets, station lookup); **profiles / profile views** (styles and band sets); **assemblies & subassemblies** (including AI-generated SAC subassemblies: write XAML → pack `.pkt` → import); **corridors** (one-shot creation, region editing, corridor surfaces); **grading / pipe networks / parcels / catchments / intersections / data shortcuts**; quantity take-off and cost estimation |
| **Layers / blocks / xrefs** | Create, rename, read/write properties, wblock, attach xrefs |
| **File channel** | Read `docx` / `xlsx` / `pptx` / `zip` plus legacy `doc` / `xls` (built-in OLE2 / BIFF8 parsing — **no Office, no extra dependencies**); list directories; write inside a sandboxed workspace; open / create / save drawings (semi-automated multi-drawing workflow) |
| **Selection** | The selection is snapshotted automatically when you send a command, and tagged with the drawing name — switching drawings never picks up a stale selection |
| **Tasks & memory** | Multi-step task plans, real abort/cancel, selective cleanup, keyword-injected long-term AI memory |

---

## Architecture

```
   Palette (C#, embedded Civil 3D palette set)
      │  HTTP  POST :19876/send
      ▼
   relay (server/panel-relay.js)
      │  ├─ llm mode: built-in LLM agent (OpenAI-compatible) + tool loop
      │  └─ openclaw mode: your own agent platform
      │  HTTP  POST :19876/tcp   (JSON-RPC 2.0 bridge)
      ▼
   C3D plugin (Civil3DMcpPlugin.dll, TCP :8080)  ←→  AutoCAD / Civil 3D database

   MCP client ──POST :3000/execute──▶ MCP server (server/mcp, 270 tools)
                                            └──▶ same plugin underneath
```

**Ports** (configurable via `config.json` or environment variables): plugin `8080` · MCP `3000` · relay `19876`

---

## Requirements

- **Windows**
- **AutoCAD / Civil 3D 2024 / 2025 / 2026** (licensed copy — bring your own)
- An **LLM API key** (the only thing you must supply in the default mode)
- Runtime prerequisites (Node, etc.) are handled by the installer

> This project is **not an Autodesk product** and is neither affiliated with nor endorsed by Autodesk.

---

## Quick start

> Don't want to build it yourself? Grab `setup.exe` or the portable zip from the [Releases](../../releases) page.

```
1. Make sure Civil 3D (2024 / 2025 / 2026) is installed
2. Double-click install.bat  (recommended — bypasses the PowerShell execution policy)
   or: right-click install.ps1 → Run with PowerShell
3. Step 11 of the wizard: pick your LLM provider and paste your API key
4. Launch Civil 3D — Hank.lsp auto-NETLOADs the plugin
5. Just type into the palette: "raise the selected polyline by 0.5 m"
```

**Uninstall**

- Installed via the installer: remove `new-acad` from Windows *Apps & features* (or Start menu → Uninstall new-acad; it runs the Inno Setup uninstaller)
- Portable zip: run `uninstall.ps1` (removes the autostart task, the `Hank.lsp` autoload entry and leftover processes)

---

## Docs for your AI agent

This project is literally about letting AI drive CAD, so it ships a handover manual written for AI agents:

- [`ONBOARDING.md`](ONBOARDING.md) (Chinese) — environment self-checks (plugin / relay / MCP ports), correct way to call tools, troubleshooting, method-call priority. Point your agent at this file and it knows where to start.
- `使用手册.html` (Chinese) — end-user manual for the palette
- [`knowledge/`](knowledge/) (Chinese) — API inventory, Civil 3D object model, C3D API references, SAC XAML guide and templates

---

## Build & package

```powershell
# Build the plugin (output: plugin/AcBridge-v24/Civil3DMcpPlugin.dll)
# Use this script. A bare `dotnet build -o` produces a 4 KB empty shell DLL
# (no types at all — NETLOAD loads it silently and nothing works).
powershell -ExecutionPolicy Bypass -File build-plugin.ps1

# Building needs the Autodesk managed assemblies (AcDbMgd / AecBaseMgd / AeccDbMgd ...)
# dropped into C_References/  (gitignored, not distributed with this repo)

# Distribution folder / installer
powershell -ExecutionPolicy Bypass -File build-dist.ps1        # → dist/
powershell -ExecutionPolicy Bypass -File build-installer.ps1   # → installer (needs Inno Setup)
```

**The DLL is locked while Civil 3D is running** — close Civil 3D before building.

---

## Repository layout

```
new-acad/
├── plugin/
│   ├── AcBridge-v24/
│   │   ├── src/            C# sources (55 files: plugin + palette)
│   │   └── Civil3DMcpPlugin.dll  ← build output (not shipped in this repo)
│   └── Hank.lsp            Civil 3D autoload script (NETLOADs the plugin)
├── server/
│   ├── panel-relay.js      palette hub (:19876) + /tcp JSON-RPC bridge
│   ├── relay-launcher.js   relay watchdog (auto-restart + crash log)
│   ├── llm-agent.js        built-in LLM agent (OpenAI-compatible, tool loop, context compaction)
│   ├── cad-tools.js        the AI's only tool entry point
│   ├── file-tools.js       file channel (document readers / sandboxed writes)
│   ├── sacred-mcp.js       MCP server launcher (:3000)
│   └── mcp/                MCP server (TS sources + build output + 270 tools)
├── tools/                  environment checks, port checks, self-tests, scanners
├── knowledge/              technical docs (API inventory / object model / SAC guide)
├── install.bat / install.ps1 / uninstall.ps1 / uninstall-pre.ps1
├── config.json             local config (ports / tokens / LLM settings) — gitignored
└── ONBOARDING.md           onboarding guide for AI agents
```

---

## Known limitations

- 12 methods fall back to the command channel (no public managed API), with a 25 s timeout guard
- The plugin DLL is locked while Civil 3D runs — close it before rebuilding
- The palette receives replies over **SSE push** (`/replies/stream`) — not polling. Status and task progress are pushed in real time too (the 10 s `/status` poll is only a fallback for dropped connections)
- A few Civil 3D features (grading group editing, parcel line editing) are not wrapped by the managed API and are therefore unreachable for the AI — see `ONBOARDING.md`

---

## License & credits

- Released under the **MIT License** — see [`LICENSE`](LICENSE)
- **Upstream credit**: [Civil3D-mcp](https://github.com/Sacred-G/Civil3D-mcp) (MIT License).
  This repository references its open-source implementation under `server/mcp/`; the original license text is kept at `server/mcp/LICENSE`.
- Third-party components and dependencies: [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)
