new-acad — installation & uninstall
===================================

WHAT THIS IS
  An in-app AI assistant for Autodesk AutoCAD / Civil 3D.
  This folder is an Autodesk Autoloader bundle: once it sits in the
  ApplicationPlugins folder, AutoCAD / Civil 3D loads it automatically.
  Everything else it needs (a private Node runtime, the relay service and the
  MCP server) is INSIDE this bundle — no separate installer, no scheduled task,
  and no changes to your AutoCAD support paths.

INSTALL (2 steps, no reboot)
  1. Copy the whole "new-acad.bundle" folder to:
       %APPDATA%\Autodesk\ApplicationPlugins\
     (paste  %APPDATA%\Autodesk\ApplicationPlugins  into the Explorer address bar)
  2. Start AutoCAD / Civil 3D. The palette opens automatically.
     You can also run the command  HANK_SHOW  at any time.

  Then open the palette settings and paste your own LLM API key
  (DeepSeek / OpenAI / Qwen / Kimi / GLM / MiniMax / OpenRouter / xAI,
  or any OpenAI-compatible endpoint).

UNINSTALL (1 step, no leftovers)
  1. Close AutoCAD / Civil 3D.
  2. Delete the folder:
       %APPDATA%\Autodesk\ApplicationPlugins\new-acad.bundle
  That is all: settings, logs, chat history and the AI memory live inside that
  single folder, so removing it removes everything.
  (Optional: the log file  %LOCALAPPDATA%\Civil3DMcpPlugin\plugin.log  may be
   deleted as well.)

USEFUL COMMANDS
  HANK_SHOW      show/hide the assistant palette
  C3DMCPSTATUS   show port / health status
  C3DMCPSTART    start the background services manually
  C3DMCPSTOP     stop the background services

PORTS (loopback only, never exposed to the internet)
  8080   plugin <-> relay bridge
  19876  relay / built-in agent
  3000   MCP server (optional external compatibility)

REQUIREMENTS
  Windows x64, genuine AutoCAD / Civil 3D 2025 or 2026, a working internet
  connection for the AI provider you configure, and your own API key.

PRIVACY
  The publisher collects nothing. See the full policy at:
  https://jjo37.github.io/new-acad/PRIVACY.html

--------------------------------------------------------------------------------
中文速览
  安装：把整个 new-acad.bundle 文件夹复制到
        %APPDATA%\Autodesk\ApplicationPlugins\ ，然后启动 AutoCAD / Civil 3D，
        面板会自己弹出来（也可随时执行命令 HANK_SHOW）。之后在面板设置里填
        你自己的 LLM API Key 即可。
  卸载：关掉 CAD，删掉那个 new-acad.bundle 文件夹 —— 干净彻底，不留残余
        （设置、日志、对话记录、AI 记忆都在这个文件夹里）。
  依赖：bundle 里自带 Node 运行时、中继服务和 MCP 服务器；不改你的支持路径、
        不建计划任务。
  端口：8080 / 19876 / 3000，只监听本机。
  隐私：https://jjo37.github.io/new-acad/PRIVACY.html
