using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Civil3DMcpPlugin;

/// <summary>
/// 端口探测：GetExtendedTcpTable 拿监听占用者进程 + TCP 试连。
/// 2026-08-13 P3-B：面板「端口」按钮 → PortStatusForm 可视化诊断。
/// </summary>
internal static class PortProbe
{
    public sealed class PortInfo
    {
        public int Port;
        public string ServiceName = "";
        public bool Listening;
        public int OwnerPid;
        public string OwnerName = "";
        public bool Expected; // 占用者是期望进程（acad/node），否则视为"被抢"
    }

    public static List<PortInfo> Probe(int bridgePort, int relayPort, int mcpPort)
    {
        var owners = GetTcpOwners();
        var specs = new (int Port, string Name, string Expect)[]
        {
            (bridgePort, Loc.T("C3D 插件", "C3D Plugin"), "acad"),
            (relayPort,  "Relay",    "node"),
            (mcpPort,    "MCP",      "node"),
            (18789,      "Gateway",  "node"),
        };
        var list = new List<PortInfo>();
        foreach (var s in specs)
        {
            var info = new PortInfo { Port = s.Port, ServiceName = s.Name };
            if (owners.TryGetValue(s.Port, out var pid))
            {
                info.Listening = true;
                info.OwnerPid = pid;
                try { info.OwnerName = Process.GetProcessById(pid).ProcessName; } catch { info.OwnerName = "?"; }
                info.Expected = info.OwnerName.Contains(s.Expect, StringComparison.OrdinalIgnoreCase);
            }
            list.Add(info);
        }
        return list;
    }

    /// <summary>枚举监听端口 → 占用 PID；失败返回空字典（降级为仅显示"未监听"）。</summary>
    private static Dictionary<int, int> GetTcpOwners()
    {
        var map = new Dictionary<int, int>();
        int size = 0;
        // 2026-08-13 修复: tableClass 应为 3 (TCP_TABLE_OWNER_PID_LISTENER), 误用 4 (=CONNECTIONS) 返回全连接无 LISTEN 行
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (size <= 0) return map;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, 2, 3, 0) != 0) return map;
            var count = Marshal.ReadInt32(buf);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var ptr = buf + 4;
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(ptr);
                ptr += rowSize;
                if (row.state != 2) continue; // MIB_TCP_STATE_LISTEN
                // localPort 网络字节序（大端）→ 主机序
                var port = (int)((row.localPort >> 8) | ((row.localPort & 0xFF) << 8));
                map[port] = (int)row.owningPid;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return map;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);
}

/// <summary>
/// 端口状态诊断对话框（暗色风格，跟随面板）。
/// 2026-08-13 P3-B：逐端口 状态灯 + 占用者进程；[刷新][复制诊断][打开配置向导][关闭]。
/// UI 线程禁止 .Wait()——刷新走 async/await（Task.Run 探测）。
/// </summary>
internal sealed class PortStatusForm : Form
{
    private readonly Label[] _rows = new Label[4];
    private readonly int _bridge, _relay, _mcp;
    private readonly string _root;

    public PortStatusForm(int bridge, int relay, int mcp, string root)
    {
        _bridge = bridge; _relay = relay; _mcp = mcp; _root = root;
        Text = Loc.T("端口状态诊断", "Port Status");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(420, 210);
        BackColor = Color.FromArgb(30, 30, 46);   // 2026-08-13: 配色对齐面板主背景
        ForeColor = Color.FromArgb(205, 214, 244);
        Font = new Font("Microsoft YaHei", 9);

        for (int i = 0; i < 4; i++)
        {
            _rows[i] = new Label
            {
                Location = new Point(16, 12 + i * 26), Size = new Size(388, 22),
                ForeColor = Color.FromArgb(205, 214, 244),
                Text = Loc.T($"  {i + 1}. 加载中…", $"  {i + 1}. Loading…"),
            };
            Controls.Add(_rows[i]);
        }

        var btnClose = new Button { Text = Loc.T("关闭", "Close"), Location = new Point(324, 128), Size = new Size(72, 30) };
        btnClose.Click += (_, _) => Close();
        var btnConfig = new Button { Text = Loc.T("打开配置向导", "Open Setup Wizard"), Location = new Point(196, 128), Size = new Size(116, 30) };
        btnConfig.Click += (_, _) => OpenConfigWizard();
        var btnCopy = new Button { Text = Loc.T("复制诊断", "Copy Diagnosis"), Location = new Point(108, 128), Size = new Size(80, 30) };
        btnCopy.Click += (_, _) => { try { Clipboard.SetText(BuildDiagnostic()); } catch { } };
        var btnRefresh = new Button { Text = Loc.T("刷新", "Refresh"), Location = new Point(24, 128), Size = new Size(72, 30) };
        btnRefresh.Click += async (_, _) => await RefreshAsync();
        foreach (var b in new[] { btnClose, btnConfig, btnCopy, btnRefresh })
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(49, 50, 68);
            b.ForeColor = Color.FromArgb(205, 214, 244);
            Controls.Add(b);
        }

        _ = RefreshAsync(); // 打开即刷新（fire-and-forget，内部 try/catch）
    }

    /// <summary>异步刷新：Task.Run 探测（不卡 UI 线程，不 .Wait()）。</summary>
    private async System.Threading.Tasks.Task RefreshAsync()
    {
        foreach (var r in _rows) r.Text = Loc.T("  刷新中…", "  Refreshing…");
        try
        {
            var list = await System.Threading.Tasks.Task.Run(() => PortProbe.Probe(_bridge, _relay, _mcp));
            for (int i = 0; i < _rows.Length && i < list.Count; i++)
            {
                var p = list[i];
                string mark, color;
                if (!p.Listening) { mark = "○"; color = "Gray"; }
                else if (p.Expected) { mark = "●"; color = "Green"; }
                else { mark = "!"; color = "Red"; }
                var owner = p.Listening ? $"  PID {p.OwnerPid} ({p.OwnerName})" : Loc.T("  未启动", "  Not running");
                var note = p.Listening && !p.Expected ? Loc.T("  ← 被其他程序占用！", "  ← taken by another program!") : "";
                _rows[i].Text = $"  {mark} {p.Port,-6} {p.ServiceName,-10}{owner}{note}";
                _rows[i].ForeColor = color switch { "Green" => Color.FromArgb(166, 227, 161), "Red" => Color.FromArgb(243, 139, 168), _ => Color.FromArgb(140, 150, 180) };
            }
        }
        catch (Exception ex)
        {
            for (int i = 0; i < _rows.Length; i++) _rows[i].Text = Loc.T("  探测失败: ", "  Probe failed: ") + ex.Message;
        }
    }

    private string BuildDiagnostic()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("new-acad 端口诊断 ", "new-acad port diagnosis ") + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var list = PortProbe.Probe(_bridge, _relay, _mcp);
        foreach (var p in list)
        {
            if (!p.Listening) sb.AppendLine(Loc.T($"  [--] {p.Port} {p.ServiceName}: 未启动", $"  [--] {p.Port} {p.ServiceName}: not running"));
            else if (p.Expected) sb.AppendLine(Loc.T($"  [OK] {p.Port} {p.ServiceName}: 监听中 PID {p.OwnerPid} ({p.OwnerName})", $"  [OK] {p.Port} {p.ServiceName}: listening, PID {p.OwnerPid} ({p.OwnerName})"));
            else sb.AppendLine(Loc.T($"  [!!] {p.Port} {p.ServiceName}: 被 {p.OwnerName} 占用 PID {p.OwnerPid}（非本服务进程）", $"  [!!] {p.Port} {p.ServiceName}: held by {p.OwnerName} PID {p.OwnerPid} (not our service)"));
        }
        sb.AppendLine(Loc.T($"配置: bridgePort={_bridge} relayPort={_relay} mcpPort={_mcp}", $"Config: bridgePort={_bridge} relayPort={_relay} mcpPort={_mcp}"));
        return sb.ToString();
    }

    private void OpenConfigWizard()
    {
        try
        {
            var bat = Path.Combine(_root, "tools", "port-config.bat");
            if (File.Exists(bat))
            {
                Process.Start(new ProcessStartInfo { FileName = bat, WorkingDirectory = Path.GetDirectoryName(bat) });
                return;
            }
            var cfg = Path.Combine(_root, "config.json");
            if (File.Exists(cfg))
            {
                Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{cfg}\"" });
                return;
            }
            // 2026-09-15: 旧版在这里静默结束 → 用户看到"按钮没用"。改为明确告知找过哪里
            var msg = Loc.T("找不到配置文件，无法打开配置向导。", "Cannot open setup wizard - config not found.") +
                      "\n\n" + Loc.T("已查找目录：", "Searched in: ") + _root +
                      "\n  - tools\\port-config.bat\n  - config.json";
            try { MessageBox.Show(msg, Loc.T("端口配置", "Port Config"), MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
        }
        catch (Exception ex)
        {
            PluginLog.Debug("Palette", "打开配置向导失败: " + ex.Message);
            try { MessageBox.Show(Loc.T("打开配置向导失败: ", "Open setup wizard failed: ") + ex.Message, Loc.T("端口配置", "Port Config"), MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
        }
    }
}
