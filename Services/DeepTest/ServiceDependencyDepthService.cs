using System.Diagnostics;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// U10-TS-0219「驅動服務啟動相依深度統計」：<c>ts.svc.depend.depth</c>。
/// </summary>
/// <remarks>
/// <para>
/// 來源：SCM 資料庫的登錄檔投影 <c>HKLM\SYSTEM\CurrentControlSet\Services</c>——
/// 每個服務/驅動鍵的 <c>Type</c>（服務類型）與 <c>DependOnService</c>（REG_MULTI_SZ 相依清單，
/// 即 <c>ServicesDependedOn</c> 的資料面）。usermode 唯讀、零網路；走訪有節點預算。
/// </para>
/// <para>
/// 誠實界線：①登錄檔是 SCM 的持久投影——「鍵存在」不等於「驅動此刻在跑」，
/// 這裡算的是<b>相依拓撲</b>不是執行狀態；②沒有 DependOnService 值的服務＝零相依，
/// 與「鍵讀不到」分開記；③深度是「該服務啟動前需先啟動多少層」的拓撲量，不是啟動時間判決。
/// </para>
/// </remarks>
public static class ServiceDependencyDepthService
{
    private const string Category = "系統";
    public const string Key = "ts.svc.depend.depth";
    public const string Source = "HKLM\\SYSTEM\\CurrentControlSet\\Services（Type／DependOnService；唯讀）";

    /// <summary>單一服務的相依列。</summary>
    public sealed record SvcRow(string Name, string ServiceType, string[] DependsOn, bool DependsOnReadable);

    /// <summary>收集相依深度統計。測試以注入取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<SvcRow>>? probe = null, int nodeBudget = 4000)
    {
        IReadOnlyList<SvcRow> rows;
        try { rows = probe is { } p ? p() : RealProbe(nodeBudget); }
        catch (Exception ex)
        {
            return [new HardwareFact(Key, Category, "服務相依深度", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "SCM 資料庫讀取失敗：" + ex.Message)];
        }
        if (rows.Count == 0)
            return [new HardwareFact(Key, Category, "服務相依深度", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "SCM 資料庫列舉到 0 個服務——來源可疑，不以 0 冒充")];
        return [BuildFact(at, rows)];
    }

    /// <summary>純函式：相依列 → 深度統計（含環路防護）。</summary>
    public static HardwareFact BuildFact(DateTimeOffset at, IReadOnlyList<SvcRow> rows)
    {
        var byName = rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var depthCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int unreadable = rows.Count(r => !r.DependsOnReadable);
        int zeroDep = rows.Count(r => r.DependsOnReadable && r.DependsOn.Length == 0);
        int maxDepth = 0;
        string maxDepthName = "";
        foreach (var r in rows)
        {
            int d = DepthOf(r.Name, byName, depthCache, new HashSet<string>(StringComparer.OrdinalIgnoreCase), out bool cycle);
            if (cycle || d < 0) continue;
            if (d > maxDepth) { maxDepth = d; maxDepthName = r.Name; }
        }
        var mostDepended = rows.Where(r => r.DependsOnReadable).SelectMany(r => r.DependsOn)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).FirstOrDefault();
        var typeCounts = rows.GroupBy(r => r.ServiceType).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}×{g.Count()}");
        string value = $"{rows.Count} 個服務/驅動（{string.Join("・", typeCounts)}）；"
            + $"最長相依鏈 {maxDepth} 層（{maxDepthName}）；零相依 {zeroDep} 個"
            + (mostDepended is { } md ? $"；被依賴最多：{md.Key}（{md.Count()} 個服務依賴它）" : "")
            + (unreadable > 0 ? $"；{unreadable} 個鍵的 Type 值讀不到（逐筆如實標）" : "");
        return new HardwareFact(Key, Category, "服務相依深度", value, "", Source,
            FactTrustLevel.Reported, false, at, maxDepth, FactAvailability.Present, null);
    }

    /// <summary>純函式：單服務的相依深度（DFS＋環路防護；環路回 -1）。</summary>
    public static int DepthOf(string name, IReadOnlyDictionary<string, SvcRow> byName,
        IDictionary<string, int> cache, ISet<string> stack, out bool cycle)
    {
        cycle = false;
        if (cache.TryGetValue(name, out int cached)) return cached;
        if (!stack.Add(name)) { cycle = true; return -1; }
        if (!byName.TryGetValue(name, out var row) || !row.DependsOnReadable)
        {
            cache[name] = 0;
            return 0; // 相依清單讀不到的視為終點（另在統計值裡如實標 unreadable）
        }
        int depth = 0;
        foreach (var dep in row.DependsOn)
        {
            int d = DepthOf(dep, byName, cache, stack, out bool depCycle);
            if (depCycle) { cycle = true; return -1; }
            if (d + 1 > depth) depth = d + 1;
        }
        stack.Remove(name);
        cache[name] = depth;
        return depth;
    }

    // ── 真實登錄檔走訪（薄；測試以注入取代）──

    private static IReadOnlyList<SvcRow> RealProbe(int nodeBudget)
    {
        var rows = new List<SvcRow>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services")
            ?? throw new InvalidOperationException(@"找不到 SYSTEM\CurrentControlSet\Services（本機沒有 SCM 資料庫）");
        foreach (var name in root.GetSubKeyNames())
        {
            if (rows.Count >= nodeBudget) break;
            try
            {
                using var svc = root.OpenSubKey(name);
                if (svc is null) { rows.Add(new SvcRow(name, "鍵開啟失敗", [], false)); continue; }
                var typeValue = svc.GetValue("Type");
                if (typeValue is not int type)
                {
                    rows.Add(new SvcRow(name, "Type 值讀不到", [], false));
                    continue;
                }
                var dependsValue = svc.GetValue("DependOnService");
                string[] dependsOn = dependsValue is string[] deps ? deps : [];
                rows.Add(new SvcRow(name, DescribeType(type), dependsOn, DependsOnReadable: true));
            }
            catch
            {
                rows.Add(new SvcRow(name, "讀取失敗", [], false));
            }
        }
        return rows;
    }

    /// <summary>純函式：服務類型位元（winnt.h SERVICE_*）→ 文字（主要者）。</summary>
    [SpecRef("Windows SDK shared/winnt.h：SERVICE_KERNEL_DRIVER 0x1、SERVICE_FILE_SYSTEM_DRIVER 0x2、"
        + "SERVICE_ADAPTER 0x4、SERVICE_RECOGNIZER_DRIVER 0x8、SERVICE_WIN32_OWN_PROCESS 0x10、"
        + "SERVICE_WIN32_SHARE_PROCESS 0x20、SERVICE_USER_OWN_PROCESS 0x50、SERVICE_USER_SHARE_PROCESS 0x60；"
        + "DependOnService 為 REG_MULTI_SZ（SCM 資料庫的登錄檔投影）")]
    public static string DescribeType(int type) => (type & 0x7F) switch
    {
        0x1 => "核心驅動",
        0x2 => "檔案系統驅動",
        0x4 => "介面卡（遺留）",
        0x8 => "識別驅動",
        0x10 => "使用者服務（自有程序）",
        0x20 => "使用者服務（共享程序）",
        0x50 => "使用者服務（自有程序，Per-User）",
        0x60 => "使用者服務（共享程序，Per-User）",
        _ => $"類型位元 0x{type:X}",
    };
}
