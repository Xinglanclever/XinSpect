using System.Management;

namespace XinSpect;

/// <summary>一個 Windows 選用功能的狀態（WMI Win32_OptionalFeature 原樣）。InstallState：1 啟用、2 停用、3 不存在。</summary>
public sealed record OptionalFeatureEntry(string Name, uint InstallState);

/// <summary>
/// WP16 虛擬化面：Windows 選用功能狀態（WMI Win32_OptionalFeature，usermode 零特權）。
/// 目標功能：Hyper-V Hypervisor／虛擬機器平台／WSL／容器——VBS 與 hypervisor 存在位
/// 由 PlatformTrustService 涵蓋，這裡補的是「哪些虛擬化元件被裝了、開了」的系統口徑。
/// 查詢結果沒回報的功能如實標「未回報」——不等於停用，更不等於不存在。
/// </summary>
public static class OptionalFeatureService
{
    private const string Category = "系統與軟體";

    private static readonly (string Key, string Name, string Feature)[] Targets =
    [
        ("feat.hyperv", "Hyper-V Hypervisor", "Microsoft-Hyper-V-Hypervisor"),
        ("feat.vmp", "虛擬機器平台", "VirtualMachinePlatform"),
        ("feat.wsl", "Windows 子系統 Linux（WSL）", "Microsoft-Windows-Subsystem-Linux"),
        ("feat.containers", "容器", "Containers"),
    ];

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<OptionalFeatureEntry>?>? probe = null)
    {
        var entries = (probe ?? FetchWmi)();
        if (entries is null)
            return Targets.Select(t => new HardwareFact(t.Key, Category, t.Name, "", "",
                "WMI Win32_OptionalFeature（選用功能狀態）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "WMI 查詢失敗——選用功能狀態讀不到就是不猜")).ToList();

        var byName = entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().InstallState, StringComparer.OrdinalIgnoreCase);

        var facts = new List<HardwareFact>();
        foreach (var (key, name, feature) in Targets)
        {
            facts.Add(byName.TryGetValue(feature, out uint state)
                ? Fact(key, name, Describe(state), feature, at, null)
                : Fact(key, name, "—（WMI 未回報此功能）", feature, at, null));
        }
        facts.Add(Fact("feat.count", "查詢到的選用功能數", entries.Count.ToString(), "Win32_OptionalFeature 全列", at, entries.Count));
        return facts;
    }

    private static string Describe(uint state) => state switch
    {
        1 => "啟用",
        2 => "停用（元件已存在）",
        3 => "不存在（此系統未提供）",
        var other => $"InstallState {other}（規範外，不解讀）",
    };

    private static HardwareFact Fact(string key, string name, string value, string feature,
        DateTimeOffset at, double? numeric) =>
        new(key, Category, name, value, "", $"WMI Win32_OptionalFeature（{feature}）",
            FactTrustLevel.Reported, false, at, numeric);

    /// <summary>WMI 通路（極薄）：任何失敗回 null，由 Collect 標三態。</summary>
    public static IReadOnlyList<OptionalFeatureEntry>? FetchWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, InstallState FROM Win32_OptionalFeature");
            var entries = new List<OptionalFeatureEntry>();
            foreach (var m in searcher.Get())
                entries.Add(new OptionalFeatureEntry(m["Name"]?.ToString() ?? "", (uint)m["InstallState"]));
            return entries;
        }
        catch { return null; }
    }
}
