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
        public string Source = "";  // "table" | "connect"
        public string Note = "";
        public bool Optional;      // 2026-11-04: 发布版不需要 Gateway，未启动不应报红
    }

    public static List<PortInfo> Probe(int bridgePort, int relayPort, int mcpPort, int gatewayPort = 18789)
    {
        // 2026-11-04（真机抓到）: GetExtendedTcpTable 读失败时原来直接返回空表，于是四个端口一律
        // 显示「未启动」—— 而 8080 明明 Test-NetConnection 通、纯 CAD 冒烟 7/8 也过。
        // 现在：表读不到就用 TCP 试连兜底，并如实标注「按连通性判定」，绝不把读表失败说成没启动。
        var owners = GetTcpOwners();
        var specs = new (int Port, string Name, string Expect, bool Optional)[]
        {
            (bridgePort, Loc.T("C3D 插件", "C3D Plugin"), "acad", false),
            (relayPort,  "Relay",    "node", false),
            (mcpPort,    "MCP",      "node", false),
            // 2026-11-04: 发布版用内置 LLM，不装 OpenClaw 网关 -> 18789 未启动是正常态，不是错误
            (gatewayPort,"Gateway",  "node", true),
        };
        var list = new List<PortInfo>();
        foreach (var s in specs)
        {
            var info = new PortInfo { Port = s.Port, ServiceName = s.Name, Optional = s.Optional };
            if (owners != null && owners.TryGetValue(s.Port, out var pid))
            {
                info.Listening = true;
                info.OwnerPid = pid;
                info.Source = "table";
                try { info.OwnerName = Process.GetProcessById(pid).ProcessName; } catch { info.OwnerName = "?"; }
                info.Expected = info.OwnerName.Contains(s.Expect, StringComparison.OrdinalIgnoreCase);
            }
            else if (CanConnect(s.Port))
            {
                // 有东西在接，只是拿不到占用者 -> 不能说成「未启动」
                info.Listening = true;
                info.OwnerPid = 0;
                info.OwnerName = "";
                info.Expected = true;
                info.Source = "connect";
                // 如实区分两种情况：表没读到 vs 表读到了但里面没这个端口（可能是别的地址族）
                // 2026-11-05: 把「为什么只能试连」和「试连花了多久」一起写出来 —— 报告要能自证
                info.Note = owners == null
                    ? Loc.T("端口表读取失败（" + TableStatus + "），按连通性判定 " + LastConnectMs + "ms", "port table unreadable (" + TableStatus + "), judged by connect " + LastConnectMs + "ms")
                    : Loc.T("端口表里没有该端口（表内 " + LastTableRows + " 行），按连通性判定 " + LastConnectMs + "ms", "port not in the ipv4 table (" + LastTableRows + " rows), judged by connect " + LastConnectMs + "ms");
            }
            list.Add(info);
        }
        return list;
    }

    /// <summary>单个端口是否在听（复用读表 + 试连兜底）。2026-11-05: 给「relay 运行态」比对用。</summary>
    public static bool IsListening(int port)
    {
        var owners = GetTcpOwners();
        if (owners != null && owners.ContainsKey(port)) return true;
        return CanConnect(port);
    }

    /// <summary>
    /// 端口能不能接 —— 端口表读不到时唯一可信的判据（2026-11-04）。
    /// 2026-11-05: 预算 400ms → 2500ms。实测本机 .NET 对**无监听端口**要 ~2050ms 才抛
    /// ConnectionRefused（同一个端口 node 是瞬时 RST），400ms 预算下"没在听"永远靠"超时"猜；
    /// 进程一被抢占就把"在听"判成"没在听"，正是 11-04 要消灭的那个谎。
    /// </summary>
    private static bool CanConnect(int port)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync(System.Net.IPAddress.Loopback, port);
            if (!task.Wait(ConnectTimeoutMs)) { LastConnectMs = (int)sw.ElapsedMilliseconds; return false; }
            LastConnectMs = (int)sw.ElapsedMilliseconds;
            return client.Connected;
        }
        catch (Exception)
        {
            LastConnectMs = (int)sw.ElapsedMilliseconds;
            return false;   // 连不上就是没在听
        }
    }

    /// <summary>试连预算（ms）。依据见 CanConnect 注释：实测被拒耗时 ~2050ms，预算必须高于它。</summary>
    private const int ConnectTimeoutMs = 2500;

    /// <summary>上一次试连耗时（ms），写进诊断，让「被拒」和「超时」分得开（2026-11-05）。</summary>
    private static int LastConnectMs = -1;

    /// <summary>上一次读到的 IPv4 监听行数（0 = 没读到），诊断里自证用（2026-11-05）。</summary>
    public static int LastTableRows { get; private set; }

    /// <summary>读表失败的原因码，给诊断对话框自己交代清楚（2026-11-04）。</summary>
    private static string TableStatus = "";

    /// <summary>枚举监听端口 → 占用 PID；**读取失败返回 null**（调用方必须用连通性兜底）。</summary>
    private static Dictionary<int, int>? GetTcpOwners()
    {
        // 2026-11-04: 两次调用之间表会变大 -> ERROR_INSUFFICIENT_BUFFER。重试几次再认输。
        // 2026-11-05: **空表也算失败**。原来只要 `once != null` 就把空字典当成功返回，于是四个端口
        // 全部掉进试连兜底、占用者信息全丢，还贴上「端口表里没有该端口」这种误导话术
        // （2026-11-05 09:31 真机报告：19876/3000/18789 明明都在 IPv4 监听表里）。
        // 本机不可能 0 个监听端口（135/445 之类系统端口恒在），读到 0 行只可能是读表本身出了问题。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var once = ReadTcpOwnersOnce();
            if (once != null && once.Count > 0) { TableStatus = ""; LastTableRows = once.Count; return once; }
            if (once != null) { TableStatus = "empty"; LastTableRows = 0; }
        }
        LastTableRows = 0;
        return null;
    }

    private static Dictionary<int, int>? ReadTcpOwnersOnce()
    {
        var map = new Dictionary<int, int>();
        int size = 0;
        // 2026-08-13 修复: tableClass 应为 3 (TCP_TABLE_OWNER_PID_LISTENER), 误用 4 (=CONNECTIONS) 返回全连接无 LISTEN 行
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (size <= 0) { TableStatus = "sizing=" + size; return null; }
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            var rc = GetExtendedTcpTable(buf, ref size, false, 2, 3, 0);
            if (rc != 0)
            {
                TableStatus = "rc=" + rc;
                // 2026-11-04: 失败码必须落日志，否则用户只能看到「读表失败」而无法定位
                PluginLog.Warn("PortProbe", "GetExtendedTcpTable failed rc=" + rc + " (size=" + size + ")");
                return null;
            }
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
    private readonly int _bridge;                        // 本会话实际绑定（启动时定死，刷新也不会变）
    private int _relay, _mcp, _gateway;                  // config.json 当前值，刷新时重读（2026-11-05）
    private int _bridgeCfg;                              // config.json 里写的值（0 = 没写/读不到），刷新时重读
    private int _relayRuntimePort;                       // relay 运行态端口（server\.relay-runtime.json，2026-11-05）
    private bool _relayRuntimeLive;                      // 那个端口现在真的在听吗
    private readonly string _root;

    public PortStatusForm(int bridge, int relay, int mcp, int gateway, string root, int bridgeCfg = 0)
    {
        _bridge = bridge; _relay = relay; _mcp = mcp; _gateway = gateway; _root = root; _bridgeCfg = bridgeCfg;
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
        btnCopy.Click += (_, _) =>
        {
          try { Clipboard.SetText(BuildDiagnostic()); }
          catch
          {
            /* best-effort: the clipboard may be locked by another app */
          }
        };
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
        // 2026-11-05 真机：改了 config.json 后点「刷新」，界面纹丝不动 —— 因为 config 值在对话框构造时就冻住了，
        // 刷新只重新探端口。名字叫「刷新」就必须把配置也重读一遍（会话绑定的 bridgePort 除外，那个只能重启 CAD）。
        ReloadConfig();
        try
        {
            var list = await System.Threading.Tasks.Task.Run(() => PortProbe.Probe(_bridge, _relay, _mcp, _gateway));
            for (int i = 0; i < _rows.Length && i < list.Count; i++)
            {
                var p = list[i];
                string mark, color;
                if (!p.Listening) { mark = "○"; color = "Gray"; }
                else if (p.Expected) { mark = "●"; color = "Green"; }
                else { mark = "!"; color = "Red"; }
                var owner = !p.Listening
                    ? (p.Optional ? Loc.T("  未启动（可选，发布版不需要）", "  not running (optional)") : Loc.T("  未启动", "  Not running"))
                    : (p.Source == "connect" ? "  " + p.Note : $"  PID {p.OwnerPid} ({p.OwnerName})");
                var note = (p.Listening && p.Source == "table" && !p.Expected)
                    ? Loc.T("  ← 被其他程序占用！", "  ← taken by another program!")
                    : "";
                _rows[i].Text = $"  {mark} {p.Port,-6} {p.ServiceName,-10}{owner}{note}";
                // 2026-11-04: 端口只在**启动时**从 config.json 读一次 —— 改了配置但没重启 CAD 时，
                // 这里一直显示旧端口，看着像「配置没生效」。把两个值摆在一起。
                if (i == 0 && _bridgeCfg > 0 && _bridgeCfg != _bridge)
                {
                    _rows[i].Text += Loc.T($"  · config 写的是 {_bridgeCfg}，重启 CAD 才生效", $"  · config says {_bridgeCfg}, restart CAD to apply");
                }
                // 2026-11-05: relay 在别的端口上跑（改了 config 没重启 relay）—— 不说这条，
                // 用户只看得到「连不上」，其实 relay 好好的，只是在旧端口上。
                if (i == 1 && _relayRuntimeLive && _relayRuntimePort != _relay)
                {
                    _rows[i].Text += Loc.T($"  · relay 实际在 {_relayRuntimePort} 跑，重启 relay 才生效", $"  · relay is actually on {_relayRuntimePort}, restart relay to apply");
                }
                _rows[i].ForeColor = color switch { "Green" => Color.FromArgb(166, 227, 161), "Red" => Color.FromArgb(243, 139, 168), _ => Color.FromArgb(140, 150, 180) };
            }
        }
        catch (Exception ex)
        {
            for (int i = 0; i < _rows.Length; i++) _rows[i].Text = Loc.T("  探测失败: ", "  Probe failed: ") + ex.Message;
        }
    }

    /// <summary>重读 config.json 里的端口（2026-11-05）。读不到就保留旧值，不把界面刷成空。</summary>
    private void ReloadConfig()
    {
        try
        {
            var (relay, mcp, gateway) = HankPalette.ReadServicePorts();
            _relay = relay; _mcp = mcp; _gateway = gateway;
            _bridgeCfg = HankPalette.ReadConfigBridgePort(_root);
        }
        catch (Exception ex)
        {
            PluginLog.Debug("Palette", "reload ports on refresh failed: " + ex.Message);
        }
        ReloadRelayRuntime();
    }

    /// <summary>
    /// relay 的运行态端口（2026-11-05）。relay 只在启动时读 config.json，改了 relayPort 不重启它，
    /// 它就是还在旧端口上跑；而面板按 config 连新端口 -> SYN_SENT 一直重试 -> 用户看到「连不上」。
    /// 这个文件由 relay 自己在 listen 成功后落盘，此处比对「config 写的」与「实际在跑的」。
    /// </summary>
    private void ReloadRelayRuntime()
    {
        _relayRuntimePort = 0;
        _relayRuntimeLive = false;
        try
        {
            var f = Path.Combine(_root, "server", ".relay-runtime.json");
            if (!File.Exists(f)) return;
            var jo = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f)) as System.Text.Json.Nodes.JsonObject;
            if (jo == null) return;
            if (int.TryParse(PluginRuntime.JsonNodeToString(jo["port"]), out var p) && p > 0 && p < 65536)
            {
                _relayRuntimePort = p;
                _relayRuntimeLive = PortProbe.IsListening(p);
            }
        }
        catch (Exception ex)
        {
            PluginLog.Debug("Palette", "read relay runtime snapshot failed: " + ex.Message);
        }
    }

    private string BuildDiagnostic()
    {
        ReloadConfig();   // 2026-11-05: 复制的报告也要是当下的 config，不是对话框打开那一刻的
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("new-acad 端口诊断 ", "new-acad port diagnosis ") + PluginTime.LocalDisplay(DateTime.UtcNow));
        var list = PortProbe.Probe(_bridge, _relay, _mcp, _gateway);
        foreach (var p in list)
        {
            if (!p.Listening) sb.AppendLine(p.Optional
                ? Loc.T($"  [--] {p.Port} {p.ServiceName}: 未启动（可选，发布版不需要）", $"  [--] {p.Port} {p.ServiceName}: not running (optional)")
                : Loc.T($"  [--] {p.Port} {p.ServiceName}: 未启动", $"  [--] {p.Port} {p.ServiceName}: not running"));
            else if (p.Expected)
            {
                var who = p.Source == "connect" ? $"（{p.Note}）" : $" PID {p.OwnerPid} ({p.OwnerName})";
                var whoEn = p.Source == "connect" ? $" ({p.Note})" : $" PID {p.OwnerPid} ({p.OwnerName})";
                sb.AppendLine(Loc.T($"  [OK] {p.Port} {p.ServiceName}: 监听中{who}", $"  [OK] {p.Port} {p.ServiceName}: listening{whoEn}"));
            }
            else sb.AppendLine(Loc.T($"  [!!] {p.Port} {p.ServiceName}: 被 {p.OwnerName} 占用 PID {p.OwnerPid}（非本服务进程）", $"  [!!] {p.Port} {p.ServiceName}: held by {p.OwnerName} PID {p.OwnerPid} (not our service)"));
        }
        // 2026-11-05: 报告要能自证 —— 一眼看出哪几条是读表来的、哪几条只能靠试连。
        // 09:31 那份报告四条里三条“表里没有”却在监听，就是因为没写清来源和行数才让人犹豫。
        var byTable = 0;
        var byConnect = 0;
        foreach (var p in list)
        {
            if (p.Source == "table") byTable++;
            else if (p.Source == "connect") byConnect++;
        }
        sb.AppendLine(Loc.T($"判定来源: 端口表 {byTable} 条 / 试连 {byConnect} 条（IPv4 监听表 {PortProbe.LastTableRows} 行）",
            $"Verdict source: table {byTable} / connect {byConnect} (ipv4 table {PortProbe.LastTableRows} rows)"));
        // 2026-11-05: 原来这行标题写「配置: bridgePort=…」，可那里的 bridgePort 是**本会话启动时绑定的值**，
        // 而 relay/mcp/gateway 是当下现读的 config.json —— 两种来源混在一行，用户改了端口就会被误导成
        // 「配置没生效 / 显示是死代码」（10:05 真机：config 已改 8090，CAD 先起的所以还绑 8080）。
        if (_relayRuntimeLive && _relayRuntimePort != _relay)
        {
            sb.AppendLine(Loc.T($"注意: relay 实际在 :{_relayRuntimePort} 跑，config 写 {_relay}（重启 relay 才生效；tools\\restart-relay.ps1 可一键切）",
                $"Note: relay is actually on :{_relayRuntimePort}, config says {_relay} (restart relay; tools\\restart-relay.ps1 does it)"));
        }
        sb.AppendLine(Loc.T($"本会话绑定: bridgePort={_bridge}（本次 CAD 启动时确定，改配置需重启 CAD）",
            $"This session bound: bridgePort={_bridge} (fixed at CAD start; restart CAD to apply config)"));
        sb.AppendLine(Loc.T($"config.json: relayPort={_relay} mcpPort={_mcp} gatewayPort={_gateway}",
            $"config.json: relayPort={_relay} mcpPort={_mcp} gatewayPort={_gateway}"));
        if (_bridgeCfg > 0 && _bridgeCfg != _bridge)
        {
            sb.AppendLine(Loc.T($"注意: config.json 里 bridgePort={_bridgeCfg}，但本次 CAD 会话绑的是 {_bridge}（改端口需重启 CAD）",
                $"Note: config.json says bridgePort={_bridgeCfg}, this CAD session bound {_bridge} (restart CAD to apply)"));
        }
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
            try { MessageBox.Show(msg, Loc.T("端口配置", "Port Config"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch
            {
              /* best-effort: showing the message box failed (e.g. no UI thread) - non-fatal */
            }
        }
        catch (Exception ex)
        {
            PluginLog.Debug("Palette", "failed to open the port configuration wizard: " + ex.Message);
            try { MessageBox.Show(Loc.T("打开配置向导失败: ", "Open setup wizard failed: ") + ex.Message, Loc.T("端口配置", "Port Config"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch
            {
              /* best-effort: showing the message box failed - already logged above */
            }
        }
    }
}
