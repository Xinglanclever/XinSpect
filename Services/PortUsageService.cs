using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 連接埠占用檢視：列出本機所有 TCP／UDP 連線與監聽埠，並標出占用該埠的行程（PID／名稱）。
/// 純本機實作，透過 Windows IP Helper API（iphlpapi.dll 的 GetExtendedTcp/UdpTable）取得
/// 具「擁有者 PID」的連線表，無任何第三方相依；可一鍵結束占用某埠的行程。
/// </summary>
public sealed class PortRow
{
    public string Protocol { get; init; } = "";      // TCP / TCP6 / UDP / UDP6
    public string LocalAddress { get; init; } = "";
    public int LocalPort { get; init; }
    public string RemoteAddress { get; init; } = "";
    public int RemotePort { get; init; }
    public string State { get; init; } = "";          // TCP 狀態；UDP 為空
    public int Pid { get; init; }
    public string ProcessName { get; init; } = "";

    public string LocalText => LocalPort > 0 ? $"{LocalAddress}:{LocalPort}" : LocalAddress;
    public string RemoteText => RemotePort > 0 ? $"{RemoteAddress}:{RemotePort}" : (RemoteAddress.Length > 0 ? RemoteAddress : "—");
    public string PidText => Pid > 0 ? Pid.ToString() : "—";
    public string ProcessText => ProcessName.Length > 0 ? ProcessName : (Pid > 0 ? "（未知）" : "系統");
}

public sealed class PortUsageService : INotifyPropertyChanged
{
    public ObservableCollection<PortRow> Rows { get; } = new();

    private int _count;
    public int Count { get => _count; private set { _count = value; OnChanged(nameof(Count)); } }

    private string _status = "";
    public string Status { get => _status; private set { _status = value; OnChanged(nameof(Status)); } }

    // 依協定與本機埠排序後重建清單；PID→行程名以快取避免重複開啟行程控制代碼。
    public void Refresh()
    {
        try
        {
            var rows = new List<PortRow>();
            var nameCache = new Dictionary<int, string>();

            rows.AddRange(GetTcpTable(AF_INET, "TCP", nameCache));
            rows.AddRange(GetTcpTable(AF_INET6, "TCP6", nameCache));
            rows.AddRange(GetUdpTable(AF_INET, "UDP", nameCache));
            rows.AddRange(GetUdpTable(AF_INET6, "UDP6", nameCache));

            rows.Sort((a, b) =>
            {
                int c = a.LocalPort.CompareTo(b.LocalPort);
                return c != 0 ? c : string.CompareOrdinal(a.Protocol, b.Protocol);
            });

            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            Count = rows.Count;
            Status = $"共 {rows.Count} 筆連線／監聽埠 ・ {DateTime.Now:HH:mm:ss} 更新";
        }
        catch (Exception ex)
        {
            Status = "讀取連線表失敗：" + ex.Message;
        }
    }

    // 結束占用連接埠的行程（需足夠權限；系統行程通常無法結束）。
    public (bool ok, string message) KillProcess(int pid)
    {
        if (pid <= 0) return (false, "此連線由系統核心持有，無法結束。");
        if (pid == Environment.ProcessId) return (false, "無法結束曦覽自身的行程。");
        try
        {
            using var p = Process.GetProcessById(pid);
            string name = SafeName(p);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
            return (true, $"已結束行程 {name}（PID {pid}）。");
        }
        catch (ArgumentException)
        {
            return (true, $"行程（PID {pid}）已不存在。");
        }
        catch (Exception ex)
        {
            return (false, $"結束行程失敗：{ex.Message}（可能需以系統管理員身分執行）");
        }
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return "行程"; }
    }

    private static string ResolveName(int pid, Dictionary<int, string> cache)
    {
        if (pid <= 0) return "";
        if (cache.TryGetValue(pid, out var n)) return n;
        string name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
        catch { name = ""; }
        cache[pid] = name;
        return name;
    }

    // ── Windows IP Helper API ────────────────────────────────────────────────
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, int tableClass, uint reserved);

    private static readonly string[] TcpStates =
    {
        "", "已關閉", "監聽中", "SYN 已送", "SYN 已收", "已建立",
        "FIN_WAIT1", "FIN_WAIT2", "關閉等待", "關閉中", "LAST_ACK", "TIME_WAIT", "刪除中"
    };

    // 連接埠欄位為網路位元組序（大端）存於 DWORD 低 16 位元，取出後轉主機序。
    private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw & 0xFF00) >> 8));

    private List<PortRow> GetTcpTable(int family, string label, Dictionary<int, string> cache)
    {
        var list = new List<PortRow>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return list;
            int num = Marshal.ReadInt32(buf);
            IntPtr rowPtr = buf + 4;
            if (family == AF_INET)
            {
                int stride = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < num; i++)
                {
                    var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr + i * stride);
                    list.Add(new PortRow
                    {
                        Protocol = label,
                        LocalAddress = new IPAddress(r.localAddr).ToString(),
                        LocalPort = Port(r.localPort),
                        RemoteAddress = new IPAddress(r.remoteAddr).ToString(),
                        RemotePort = Port(r.remotePort),
                        State = StateText(r.state),
                        Pid = (int)r.owningPid,
                        ProcessName = ResolveName((int)r.owningPid, cache),
                    });
                }
            }
            else
            {
                int stride = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
                for (int i = 0; i < num; i++)
                {
                    var r = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr + i * stride);
                    list.Add(new PortRow
                    {
                        Protocol = label,
                        LocalAddress = new IPAddress(r.localAddr).ToString(),
                        LocalPort = Port(r.localPort),
                        RemoteAddress = new IPAddress(r.remoteAddr).ToString(),
                        RemotePort = Port(r.remotePort),
                        State = StateText(r.state),
                        Pid = (int)r.owningPid,
                        ProcessName = ResolveName((int)r.owningPid, cache),
                    });
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    private List<PortRow> GetUdpTable(int family, string label, Dictionary<int, string> cache)
    {
        var list = new List<PortRow>();
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, UDP_TABLE_OWNER_PID, 0);
        if (size == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buf, ref size, false, family, UDP_TABLE_OWNER_PID, 0) != 0) return list;
            int num = Marshal.ReadInt32(buf);
            IntPtr rowPtr = buf + 4;
            if (family == AF_INET)
            {
                int stride = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                for (int i = 0; i < num; i++)
                {
                    var r = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr + i * stride);
                    list.Add(new PortRow
                    {
                        Protocol = label,
                        LocalAddress = new IPAddress(r.localAddr).ToString(),
                        LocalPort = Port(r.localPort),
                        RemoteAddress = "",
                        RemotePort = 0,
                        State = "",
                        Pid = (int)r.owningPid,
                        ProcessName = ResolveName((int)r.owningPid, cache),
                    });
                }
            }
            else
            {
                int stride = Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();
                for (int i = 0; i < num; i++)
                {
                    var r = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(rowPtr + i * stride);
                    list.Add(new PortRow
                    {
                        Protocol = label,
                        LocalAddress = new IPAddress(r.localAddr).ToString(),
                        LocalPort = Port(r.localPort),
                        RemoteAddress = "",
                        RemotePort = 0,
                        State = "",
                        Pid = (int)r.owningPid,
                        ProcessName = ResolveName((int)r.owningPid, cache),
                    });
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    private static string StateText(uint state) =>
        state < TcpStates.Length ? TcpStates[state] : state.ToString();

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] remoteAddr;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public uint localPort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        public uint owningPid;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
