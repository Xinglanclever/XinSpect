using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Security.Cryptography.X509Certificates;

namespace XinSpect;

/// <summary>
/// 安全鑑識組（WP：安全面事實，全部唯讀）：Defender 排除清單、事件記錄清除（1102）、
/// 非微軟根憑證、USBSTOR 使用痕跡。這些是「這台機器被動過手腳嗎」的直接證據面——
/// 只陳述觀察、不下中毒判決。通路全部極薄＋注入探測測試。
/// </summary>
public static class SecurityAuditFactsService
{
    private const string Category = "系統與軟體";

    // ── 1. Defender 排除清單（root\Microsoft\Windows\Defender，usermode）──

    public static IReadOnlyList<HardwareFact> CollectDefenderExclusions(DateTimeOffset at,
        Func<IReadOnlyList<string>?>? probe = null)
    {
        var exclusions = (probe ?? FetchDefenderExclusions)();
        if (exclusions is null)
            return [new HardwareFact("defender.exclusions.count", Category, "Defender 排除清單", "", "",
                "WMI root\\Microsoft\\Windows\\Defender（MSFT_MpPreference）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "MSFT_MpPreference 查詢失敗（Defender 未啟用或被協力防毒取代）——讀不到就是不猜")];

        var facts = new List<HardwareFact>
        {
            new("defender.exclusions.count", Category, "Defender 排除清單",
                exclusions.Count == 0 ? "0 條（沒有任何排除——掃毒涵蓋完整）" : $"{exclusions.Count} 條排除", "條",
                "WMI root\\Microsoft\\Windows\\Defender（MSFT_MpPreference）", FactTrustLevel.Reported, false, at, exclusions.Count),
        };
        for (int i = 0; i < exclusions.Count; i++)
            facts.Add(new HardwareFact($"defender.exclusion.{i}", Category, $"排除 {i}",
                exclusions[i], "", "WMI MSFT_MpPreference（ExclusionPath／Process／Extension）",
                FactTrustLevel.Reported, false, at, null));
        return facts;
    }

    private static IReadOnlyList<string>? FetchDefenderExclusions()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpPreference");
            var list = new List<string>();
            foreach (var m in searcher.Get())
            {
                foreach (var field in new[] { "ExclusionPath", "ExclusionProcess", "ExclusionExtension" })
                {
                    if (m[field] is string[] items)
                        list.AddRange(items);
                }
            }
            return list;
        }
        catch { return null; }
    }

    // ── 2. 事件記錄清除（Security log 1102，需提權）──

    public static IReadOnlyList<HardwareFact> CollectLogClearEvents(DateTimeOffset at,
        Func<IReadOnlyList<DateTimeOffset>?>? probe = null)
    {
        IReadOnlyList<DateTimeOffset>? times;
        bool failed = false;
        try { times = (probe ?? FetchLogClearEvents)(); }
        catch { times = null; failed = true; }

        if (times is null)
            return [new HardwareFact("audit.logclear.count", Category, "稽核記錄清除事件（1102）", "", "",
                "事件記錄 Security log（Event 1102）", FactTrustLevel.Unknown, false, at, null,
                failed ? FactAvailability.ReadError : FactAvailability.InsufficientPrivilege,
                failed ? "Security log 查詢失敗" : "Security log 需管理員權限——讀不到就是不猜")];

        var facts = new List<HardwareFact>
        {
            new("audit.logclear.count", Category, "稽核記錄清除事件（1102）",
                times.Count == 0 ? "沒有記錄清除事件（Security log 從未被清過，或稽核政策未啟用清除稽核）" : $"{times.Count} 次", "次",
                "事件記錄 Security log（Event 1102）", FactTrustLevel.Reported, false, at, times.Count),
        };
        var latest = times.OrderByDescending(t => t).FirstOrDefault();
        if (latest != default)
            facts.Add(new HardwareFact("audit.logclear.last", Category, "最近一次記錄清除",
                $"{latest:yyyy-MM-dd HH:mm:ss} UTC", "", "事件記錄 Security log（Event 1102）",
                FactTrustLevel.Reported, false, at, null));
        return facts;
    }

    private static IReadOnlyList<DateTimeOffset>? FetchLogClearEvents()
    {
        try
        {
            var query = new EventLogQuery("Security", PathType.LogName,
                "*[System[(EventID=1102)]]") { ReverseDirection = true };
            var times = new List<DateTimeOffset>();
            using var reader = new EventLogReader(query);
            while (reader.ReadEvent() is { } evt)
            {
                using (evt)
                    times.Add(new DateTimeOffset(evt.TimeCreated.Value, TimeSpan.Zero));
                if (times.Count >= 20) break; // 上限 20 筆
            }
            return times;
        }
        catch { return null; }
    }

    // ── 3. 非微軟根憑證（X509Store Root／LocalMachine，usermode）──

    public static IReadOnlyList<HardwareFact> CollectForeignRootCerts(DateTimeOffset at,
        Func<IReadOnlyList<(string Subject, DateTime NotAfter)>>? probe = null)
    {
        var roots = probe?.Invoke() ?? FetchRootCerts();
        var foreign = roots.Where(r => !r.Subject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)).ToList();

        var facts = new List<HardwareFact>
        {
            new("cert.foreign_roots", Category, "非微軟本機信任根",
                foreign.Count == 0 ? "0 個（本機信任根全部是微軟系）" : $"{foreign.Count} 個（中間人／監控憑證的風險面，列出供判讀）", "個",
                "X509Store（Root／LocalMachine）", FactTrustLevel.Derived, false, at, foreign.Count),
        };
        for (int i = 0; i < foreign.Count && i < 10; i++)
            facts.Add(new HardwareFact($"cert.foreign_root.{i}", Category, $"非微軟信任根 {i}",
                $"{foreign[i].Subject}（有效期至 {foreign[i].NotAfter:yyyy-MM-dd}）", "",
                "X509Store（Root／LocalMachine）", FactTrustLevel.Reported, false, at, null));
        return facts;
    }

    private static IReadOnlyList<(string Subject, DateTime NotAfter)> FetchRootCerts()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Cast<X509Certificate2>()
                .Select(c => (c.Subject, c.NotAfter)).ToList();
        }
        catch { return []; }
    }

    // ── 4. USBSTOR 使用痕跡（登錄檔唯讀）──

    public static IReadOnlyList<HardwareFact> CollectUsbstor(DateTimeOffset at,
        Func<IReadOnlyList<string>?>? probe = null)
    {
        var devices = probe?.Invoke() ?? FetchUsbstorDevices();
        if (devices is null)
            return [new HardwareFact("usbstor.count", Category, "USB 儲存裝置使用痕跡", "", "",
                "登錄檔 HKLM\\SYSTEM\\CurrentControlSet\\Enum\\USBSTOR", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "USBSTOR 登錄檔讀取失敗")];

        var facts = new List<HardwareFact>
        {
            new("usbstor.count", Category, "USB 儲存裝置使用痕跡",
                devices.Count == 0 ? "沒有 USB 儲存裝置的安裝痕跡" : $"{devices.Count} 款裝置曾安裝（含早已拔除的）", "款",
                "登錄檔 HKLM\\SYSTEM\\CurrentControlSet\\Enum\\USBSTOR", FactTrustLevel.Reported, false, at, devices.Count),
        };
        for (int i = 0; i < devices.Count && i < 10; i++)
            facts.Add(new HardwareFact($"usbstor.{i}", Category, $"USB 裝置 {i}",
                devices[i], "", "登錄檔 Enum\\USBSTOR", FactTrustLevel.Reported, false, at, null));
        return facts;
    }

    private static IReadOnlyList<string>? FetchUsbstorDevices()
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USBSTOR");
            return key?.GetSubKeyNames();
        }
        catch { return null; }
    }
}
