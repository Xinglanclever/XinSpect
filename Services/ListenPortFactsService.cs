using System.Diagnostics;

namespace XinSpect;

/// <summary>監聽埠三方對照的一列：協定、埠、PID、映像路徑（或 Unknown 原因）、簽章判定。</summary>
public sealed record ListenPortRow(string Protocol, int Port, int Pid, string? Image, bool? Signed, string Note);

/// <summary>
/// 監聽埠三方對照（P0-03／SA-008）：socket（連線表）↔ 行程（PID）↔ 簽章（WinVerifyTrust）。
/// <para>
/// <b>前兩條腿重用既有 <see cref="PortUsageService"/>（IP Helper 連線表＋PID→行程名）——不寫第二份
/// iphlpapi P/Invoke（SV-001 教訓）；第三條腿走 <see cref="KernelModuleService.VerifyGenericAuthenticode"/>
/// （v2.56 抽出的共用簽章核心）。單次快照列舉；行程已退出記 Unknown（PID 競態）；高完整性行程讀不到
/// 映像也記 Unknown＋原因。<b>界線</b>：取樣瞬間必標；只列舉不阻擋、不主動連線；簽章「未通過」＝攻擊面
/// 事實不是中毒判決；被 hook 的表 API 偵測不做（超界聲明）。
/// </para>
/// </summary>
public static class ListenPortFactsService
{
    public const string Category = "網路";
    public const string CountKey = "net.listen.count";
    public const string EntriesKey = "net.listen.entries";
    public const string UnverifiedKey = "net.listen.unverified";

    /// <summary>明細最多列幾條（其餘以「等 N 條」收束）。</summary>
    public const int ListedMax = 15;

    private const string Source = "連線表（IP Helper）× 行程映像 × WinVerifyTrust";

    /// <summary>本機收集：tableProbe 重用 PortUsageService 的連線表；pidProbe／signProbe 注入式（測試餵假）。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<PortRow>?>? tableProbe = null,
        Func<int, string?>? imageProbe = null,
        Func<string, (bool? Ok, string Note)>? signProbe = null)
    {
        try
        {
            var rows = (tableProbe ?? RealTable)();
            if (rows is null)
                return [Unavailable(CountKey, at, FactAvailability.ReadError, "連線表讀取失敗——讀不到不等於沒有監聽埠")];

            imageProbe ??= RealImage;
            signProbe ??= KernelModuleService.VerifyGenericAuthenticode;

            var listeners = rows
                .Where(r => r.Protocol.StartsWith("TCP", StringComparison.Ordinal)
                    ? r.State is "LISTENING" or "監聽中" // 連線表的狀態字串是本機化文字（PortUsageService.TcpStates[2]）
                    : true) // UDP 沒有「連線」概念——整張表都是監聽
                .Where(r => r.LocalPort > 0)
                .GroupBy(r => (r.Protocol, r.LocalPort))
                .Select(g => BuildRow(g.Key.Protocol, g.Key.LocalPort, g.ToList(), imageProbe, signProbe))
                .OrderBy(r => r.Protocol, StringComparer.Ordinal).ThenBy(r => r.Port)
                .ToList();

            string sample = string.Join("；", listeners.Take(ListedMax).Select(Describe))
                + (listeners.Count > ListedMax ? "；等 " + (listeners.Count - ListedMax) + " 條" : "");
            int unverified = listeners.Count(r => r.Signed is null || r.Signed == false);
            string boundary = "取樣瞬間 " + at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "（不是持續監控）；"
                + "只列舉不阻擋、不主動連線；PID 競態或高完整性行程讀不到映像記 Unknown；"
                + "簽章「未通過」＝攻擊面事實不是中毒判決";

            var facts = new List<HardwareFact>(listeners.Count + 3)
            {
                new(CountKey, Category, "監聽埠快照",
                    $"TCP {listeners.Count(r => r.Protocol.StartsWith("TCP", StringComparison.Ordinal))} 條＋UDP {listeners.Count(r => !r.Protocol.StartsWith("TCP", StringComparison.Ordinal))} 條＝{listeners.Count} 條・{boundary}",
                    "條", Source, FactTrustLevel.Measured, false, at, listeners.Count),
                new(EntriesKey, Category, "監聽埠明細", sample, "", Source,
                    FactTrustLevel.Measured, true, at),
                new(UnverifiedKey, Category, "監聽埠簽章未驗證／未通過",
                    unverified == 0 ? "0 條（全部可驗且通過）——待確認計數是 0，不是『沒有問題』" : $"{unverified} 條（明細見上）——待查證不是已中招",
                    "條", Source, FactTrustLevel.Derived, false, at, unverified),
            };

            foreach (var r in listeners)
                facts.Add(new HardwareFact($"net.listen.{r.Protocol.ToLowerInvariant()}.{r.Port}", Category,
                    $"監聽 {r.Protocol} :{r.Port}", Describe(r), "", Source,
                    FactTrustLevel.Measured, false, at));
            return facts;
        }
        catch (Exception ex)
        {
            return [Unavailable(CountKey, at, FactAvailability.ReadError, "連線表列舉失敗（" + ex.GetType().Name + "）——讀不到不等於沒有")];
        }
    }

    private static ListenPortRow BuildRow(string protocol, int port, IEnumerable<PortRow> group,
        Func<int, string?> imageProbe, Func<string, (bool? Ok, string Note)> signProbe)
    {
        var first = group.OrderByDescending(r => r.Pid).First();
        int pid = first.Pid;
        if (pid <= 0)
            return new ListenPortRow(protocol, port, pid, null, null, "系統／核心持有（無 PID）——映像與簽章不適用");
        string? image = imageProbe(pid);
        if (image is null)
            return new ListenPortRow(protocol, port, pid, null, null,
                $"映像讀不到（行程已退出或權限不足）——Unknown，不是未簽章");
        var (ok, note) = signProbe(image);
        return new ListenPortRow(protocol, port, pid, image, ok, note);
    }

    private static string Describe(ListenPortRow r)
    {
        string who = r.Image is { } img ? $"PID {r.Pid}｜{img}" : $"PID {(r.Pid > 0 ? r.Pid.ToString() : "—") }｜{r.Note}";
        string sign = r.Signed switch
        {
            true => "簽章通過",
            false => "簽章未通過（待查證）",
            null => "簽章未驗證",
        };
        return $"{r.Protocol}:{r.Port}｜{who}｜{sign}";
    }

    private static HardwareFact Unavailable(string key, DateTimeOffset at, FactAvailability avail, string why) =>
        new(key, Category, "監聽埠快照", "", "條", Source, FactTrustLevel.Unknown, false, at, null, avail, why);

    private static IReadOnlyList<PortRow>? RealTable()
    {
        var svc = new PortUsageService();
        svc.Refresh();
        return svc.Rows.ToList();
    }

    private static string? RealImage(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
