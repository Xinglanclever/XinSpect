namespace XinSpect;

/// <summary>
/// 本地安全審計組（v2.56，工業目錄批次一 SA-001／002／003／005／007）：登錄檔持久化劫持面——
/// IFEO Debugger、Winlogon Shell/Userinit、AppInit_DLLs、輔助功能 Debugger、系統代理。
/// <b>命中＝攻擊面事實，不是中毒判決</b>：這些位置被合法軟體正常使用的機率不低
/// （輸入法掛 Debugger、企業全域代理），所以只陳述「誰掛在這裡」，不做善惡仲裁。
/// 全部唯讀、usermode（HKLM 讀取不需提權）；通路極薄＋注入探測測試，讀不到三態不猜。
/// </summary>
public static class LocalSecurityAuditService
{
    private const string Category = "本地安全審計";

    private const string Source = "登錄檔（HKLM，唯讀）";

    // ── SA-001：IFEO（Image File Execution Options）Debugger／GfiSrcDll ──────

    /// <summary>IFEO 一個映像的觀察：Debugger／GfiSrcDll 任一非空才算掛了東西。</summary>
    public sealed record IfeoObservation(string Image, string? Debugger, string? GfiSrcDll);

    public static IReadOnlyList<HardwareFact> CollectIfeo(DateTimeOffset at,
        Func<IReadOnlyList<IfeoObservation>?>? probe = null)
    {
        var obs = (probe ?? FetchIfeo)();
        if (obs is null)
            return [new HardwareFact("sa.ifeo.scan", Category, "IFEO 掃描", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "IFEO 機碼無法列舉（讀取失敗）——讀不到就是不猜")];

        var facts = new List<HardwareFact>
        {
            new("sa.ifeo.scan", Category, "IFEO 掃描（Debugger／GfiSrcDll）",
                obs.Count == 0 ? "0 個映像掛有 Debugger／GfiSrcDll" : $"{obs.Count} 個映像掛有 Debugger／GfiSrcDll（逐條列於下）",
                "", Source, FactTrustLevel.Reported, false, at, obs.Count, FactAvailability.Present),
        };
        foreach (var o in obs.OrderBy(o => o.Image, StringComparer.OrdinalIgnoreCase))
        {
            string detail = string.Join("；", new[] {
                o.Debugger is { } d ? $"Debugger＝{d}" : null,
                o.GfiSrcDll is { } g ? $"GfiSrcDll＝{g}" : null }.Where(s => s is not null));
            facts.Add(new HardwareFact($"sa.ifeo.{o.Image}", Category, $"IFEO：{o.Image}",
                detail, "", $"{Source}（命中＝風險面非判決：輸入法等合法軟體也會掛 Debugger）",
                FactTrustLevel.Reported, false, at, null, FactAvailability.Present));
        }
        return facts;
    }

    private static IReadOnlyList<IfeoObservation>? FetchIfeo()
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var ifeo = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options");
            if (ifeo is null) return null;
            var result = new List<IfeoObservation>();
            foreach (string image in ifeo.GetSubKeyNames())
            {
                using var k = ifeo.OpenSubKey(image);
                string? dbg = k?.GetValue("Debugger") as string;
                string? gfi = k?.GetValue("GfiSrcDll") as string;
                if (!string.IsNullOrWhiteSpace(dbg) || !string.IsNullOrWhiteSpace(gfi))
                    result.Add(new IfeoObservation(image, Blank(dbg), Blank(gfi)));
            }
            return result;
        }
        catch { return null; }
    }

    // ── SA-002：Winlogon（Shell／Userinit／Notify）───────────────────────────

    public sealed record WinlogonSnapshot(string? Shell, string? Userinit, IReadOnlyList<string> NotifyKeys);

    public static IReadOnlyList<HardwareFact> CollectWinlogon(DateTimeOffset at,
        Func<WinlogonSnapshot?>? probe = null)
    {
        var s = (probe ?? FetchWinlogon)();
        if (s is null)
            return [new HardwareFact("sa.winlogon.read", Category, "Winlogon 審計", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "Winlogon 機碼無法讀取——讀不到就是不猜")];

        var facts = new List<HardwareFact>
        {
            new("sa.winlogon.shell", Category, "Winlogon Shell", s.Shell ?? "（未設定＝預設 explorer.exe）", "",
                Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present),
            new("sa.winlogon.userinit", Category, "Winlogon Userinit", s.Userinit ?? "（未設定＝預設 userinit.exe）", "",
                Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present),
            new("sa.winlogon.notify.count", Category, "Winlogon Notify 掛點",
                s.NotifyKeys.Count == 0 ? "0 個（Vista 起該機制已棄用，正常）" : $"{s.NotifyKeys.Count} 個：{string.Join("、", s.NotifyKeys)}",
                "", $"{Source}（命中＝風險面非判決）", FactTrustLevel.Reported, false, at,
                s.NotifyKeys.Count, FactAvailability.Present),
        };
        return facts;
    }

    private static WinlogonSnapshot? FetchWinlogon()
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var wl = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            if (wl is null) return null;
            var notify = wl.GetSubKeyNames().Where(n => n.Equals("Notify", StringComparison.OrdinalIgnoreCase)).ToList();
            IReadOnlyList<string> notifyKeys = [];
            if (notify.Count == 1)
                using (var nk = wl.OpenSubKey(notify[0]))
                    notifyKeys = nk?.GetSubKeyNames() ?? [];
            return new WinlogonSnapshot(
                Blank(wl.GetValue("Shell") as string),
                Blank(wl.GetValue("Userinit") as string),
                notifyKeys);
        }
        catch { return null; }
    }

    // ── SA-003：AppInit_DLLs（值＋載入開關）─────────────────────────────────

    public static IReadOnlyList<HardwareFact> CollectAppInit(DateTimeOffset at,
        Func<(string? Dlls, int? LoadFlag)?>? probe = null)
    {
        var s = (probe ?? FetchAppInit)();
        if (s is null)
            return [new HardwareFact("sa.appinit.read", Category, "AppInit_DLLs", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "AppInit_DLLs 機碼無法讀取——讀不到就是不猜")];

        bool armed = (s.Value.LoadFlag ?? 0) != 0 && !string.IsNullOrWhiteSpace(s.Value.Dlls);
        return
        [
            new HardwareFact("sa.appinit.value", Category, "AppInit_DLLs",
                string.IsNullOrWhiteSpace(s.Value.Dlls) ? "（空）" : s.Value.Dlls, "",
                Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present),
            new HardwareFact("sa.appinit.load", Category, "AppInit_DLLs 載入開關",
                $"{s.Value.LoadFlag ?? 0}{(armed ? "（已啟用：DLL 會被注入所有 GUI 行程）" : "（未生效）")}", "",
                Source, FactTrustLevel.Reported, false, at, s.Value.LoadFlag, FactAvailability.Present),
        ];
    }

    private static (string? Dlls, int? LoadFlag)? FetchAppInit()
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var k = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows");
            if (k is null) return null;
            return (Blank(k.GetValue("AppInit_DLLs") as string),
                    k.GetValue("LoadAppInit_DLLs") is int i ? i : null);
        }
        catch { return null; }
    }

    // ── SA-005：輔助功能劫持（sethc／utilman 的 Debugger）────────────────────

    public static IReadOnlyList<HardwareFact> CollectAccessibility(DateTimeOffset at,
        Func<IReadOnlyList<(string Image, string? Debugger)>?>? probe = null)
    {
        var obs = (probe ?? FetchAccessibility)();
        if (obs is null)
            return [new HardwareFact("sa.a11y.read", Category, "輔助功能審計", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "輔助功能 IFEO 機碼無法讀取——讀不到就是不猜")];

        var facts = new List<HardwareFact>();
        foreach (var o in obs)
        {
            string? d = o.Debugger;
            // 六把鍵以構造子字面值出現（覆蓋掃描認這個形狀）：清單是這六個固定輸入點，
            // 走 $"sa.a11y.{image}" 內插會讓申報看不見這批生產。
            facts.Add(o.Image switch
            {
                "sethc.exe" => new HardwareFact("sa.a11y.sethc.exe", Category, "輔助功能 Debugger：sethc.exe",
                    d ?? "（未掛）", "", $"{Source}（鎖定畫面可達的輸入點——命中值得親自確認）", FactTrustLevel.Reported, false, at),
                "utilman.exe" => new HardwareFact("sa.a11y.utilman.exe", Category, "輔助功能 Debugger：utilman.exe",
                    d ?? "（未掛）", "", $"{Source}（鎖定畫面可達的輸入點——命中值得親自確認）", FactTrustLevel.Reported, false, at),
                "osk.exe" => new HardwareFact("sa.a11y.osk.exe", Category, "輔助功能 Debugger：osk.exe",
                    d ?? "（未掛）", "", $"{Source}（鎖定畫面可達的輸入點——命中值得親自確認）", FactTrustLevel.Reported, false, at),
                "magnify.exe" => new HardwareFact("sa.a11y.magnify.exe", Category, "輔助功能 Debugger：magnify.exe",
                    d ?? "（未掛）", "", $"{Source}（鎖定畫面可達的輸入點——命中值得親自確認）", FactTrustLevel.Reported, false, at),
                "narrator.exe" => new HardwareFact("sa.a11y.narrator.exe", Category, "輔助功能 Debugger：narrator.exe",
                    d ?? "（未掛）", "", $"{Source}（鎖定畫面可達的輸入點——命中值得親自確認）", FactTrustLevel.Reported, false, at),
                _ => new HardwareFact("sa.a11y.displayswitch.exe", Category, "輔助功能 Debugger：displayswitch.exe",
                    d ?? "（未掛）", "", $"{Source}（鎖定畫面可達的輸入點——命中值得親自確認）", FactTrustLevel.Reported, false, at),
            });
        }
        return facts;
    }

    private static IReadOnlyList<(string Image, string? Debugger)>? FetchAccessibility()
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var ifeo = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options");
            if (ifeo is null) return null;
            var result = new List<(string, string?)>();
            foreach (string image in new[] { "sethc.exe", "utilman.exe", "osk.exe", "magnify.exe", "narrator.exe", "displayswitch.exe" })
                using (var k = ifeo.OpenSubKey(image))
                    result.Add((image, Blank(k?.GetValue("Debugger") as string)));
            return result;
        }
        catch { return null; }
    }

    // ── SA-007：系統代理（WinINET／WinHTTP）─────────────────────────────────

    public sealed record ProxySnapshot(int? WinInetEnable, string? WinInetServer, string? WinInetOverride, bool WinHttpConfigured);

    public static IReadOnlyList<HardwareFact> CollectProxy(DateTimeOffset at,
        Func<ProxySnapshot?>? probe = null)
    {
        var s = (probe ?? FetchProxy)();
        if (s is null)
            return [new HardwareFact("sa.proxy.read", Category, "系統代理", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "代理設定機碼無法讀取——讀不到就是不猜")];

        return
        [
            new HardwareFact("sa.proxy.wininet.enable", Category, "WinINET 代理開關",
                $"{s.WinInetEnable ?? 0}{((s.WinInetEnable ?? 0) != 0 ? "（啟用）" : "（停用）")}", "",
                Source, FactTrustLevel.Reported, false, at, s.WinInetEnable, FactAvailability.Present),
            new HardwareFact("sa.proxy.wininet.server", Category, "WinINET 代理伺服器",
                string.IsNullOrWhiteSpace(s.WinInetServer) ? "（未設定）" : s.WinInetServer, "",
                $"{Source}（代理≠惡意：企業環境的常態配置）", FactTrustLevel.Reported, false, at, null, FactAvailability.Present),
            new HardwareFact("sa.proxy.wininet.override", Category, "WinINET 例外清單",
                string.IsNullOrWhiteSpace(s.WinInetOverride) ? "（未設定）" : s.WinInetOverride, "",
                Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present),
            new HardwareFact("sa.proxy.winhttp.server", Category, "WinHTTP 代理（服務用）",
                s.WinHttpConfigured
                    ? "已設定（二進位 BLOB；內容未解析——佈局未對準公開文件，如實標注）"
                    : "（未設定）", "",
                Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present),
        ];
    }

    private static ProxySnapshot? FetchProxy()
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var inet = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings");
            using var http = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections");
            if (inet is null) return null;
            return new ProxySnapshot(
                inet.GetValue("ProxyEnable") is int e ? e : null,
                Blank(inet.GetValue("ProxyServer") as string),
                Blank(inet.GetValue("ProxyOverride") as string),
                http?.GetValue("WinHttpSettings") is byte[] blob && blob.Length > 12);
        }
        catch { return null; }
    }

    // ── SA-009：暴露面總覽（RDP／WinRM／遠端登錄／管理共用）──────────────────
    // 界線：這裡報的是「設定狀態」——服務啟動類型與開關，不等於「網路上真的可達」
    //（防火牆、NAT、VPN 都會改變實際暴露面）。狀態≠可達，如實分開講。

    public sealed record ExposureSnapshot(int? RdpDeny, int? WinRmStart, int? RemoteRegistryStart, int? AutoShareWks);

    public static IReadOnlyList<HardwareFact> CollectExposure(DateTimeOffset at,
        Func<ExposureSnapshot?>? probe = null)
    {
        var s = (probe ?? FetchExposure)();
        if (s is null)
            return [new HardwareFact("sa.exposure.read", Category, "暴露面總覽", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "暴露面相關機碼無法讀取——讀不到就是不猜")];

        string Start(int? v) => v switch
        {
            2 => "2（自動啟動）", 3 => "3（手動）", 4 => "4（停用）",
            null => "（未安裝／鍵不存在）", _ => $"{v}（其他啟動類型）",
        };
        return
        [
            new HardwareFact("sa.exposure.rdp", Category, "遠端桌面（RDP）",
                s.RdpDeny == null ? "（鍵不存在）" : s.RdpDeny == 0 ? "已允許連入（fDenyTSConnections=0）" : "已停用（fDenyTSConnections=1）", "",
                $"{Source}｜狀態≠可達：防火牆與網路位置另算", FactTrustLevel.Reported, false, at, s.RdpDeny, FactAvailability.Present),
            new HardwareFact("sa.exposure.winrm", Category, "WinRM 服務啟動類型",
                Start(s.WinRmStart), "", $"{Source}｜狀態≠可達", FactTrustLevel.Reported, false, at, s.WinRmStart, FactAvailability.Present),
            new HardwareFact("sa.exposure.remote_registry", Category, "遠端登錄服務啟動類型",
                Start(s.RemoteRegistryStart), "", $"{Source}｜狀態≠可達", FactTrustLevel.Reported, false, at, s.RemoteRegistryStart, FactAvailability.Present),
            new HardwareFact("sa.exposure.admin_shares", Category, "管理共用（自動共用）",
                s.AutoShareWks == 0 ? "已停用（AutoShareWks=0）" : "預設（C$／ADMIN$ 等存在）", "",
                $"{Source}｜狀態≠可達", FactTrustLevel.Reported, false, at, s.AutoShareWks, FactAvailability.Present),
        ];
    }

    private static ExposureSnapshot? FetchExposure()
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var ts = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
            using var winrm = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WinRM");
            using var rr = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\RemoteRegistry");
            using var lan = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters");
            return new ExposureSnapshot(
                ts?.GetValue("fDenyTSConnections") as int?,
                winrm?.GetValue("Start") as int?,
                rr?.GetValue("Start") as int?,
                lan?.GetValue("AutoShareWks") as int? ?? lan?.GetValue("AutoShareServer") as int?);
        }
        catch { return null; }
    }

    // ── SA-006：Winsock LSP（分層服務提供者）────────────────────────────────
    // 合法 LSP 存在聲明：防毒／家長監護常駐於此；列出來是事實，不是警報。

    public static IReadOnlyList<HardwareFact> CollectWinsockLsp(DateTimeOffset at,
        Func<IReadOnlyList<string>?>? probe = null)
    {
        var names = (probe ?? FetchWinsockLsp)();
        if (names is null)
            return [new HardwareFact("sa.lsp.read", Category, "Winsock LSP", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "Winsock 目錄無法列舉——讀不到就是不猜")];

        var facts = new List<HardwareFact>
        {
            new("sa.lsp.count", Category, "Winsock 分層服務提供者",
                names.Count == 0 ? "0 個（純 Windows 內建鏈）" : $"{names.Count} 個（逐條列於下；防毒等合法軟體常駐於此）",
                "", $"{Source}｜合法 LSP 存在聲明", FactTrustLevel.Reported, false, at, names.Count, FactAvailability.Present),
        };
        for (int i = 0; i < names.Count; i++)
            facts.Add(new HardwareFact($"sa.lsp.{i}", Category, $"LSP {i}",
                names[i], "", Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present));
        return facts;
    }

    private static IReadOnlyList<string>? FetchWinsockLsp()
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var entries = baseKey.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\Protocol_Catalog9\Catalog_Entries");
            if (entries is null) return [];
            var result = new List<string>();
            foreach (string k in entries.GetSubKeyNames())
                using (var item = entries.OpenSubKey(k))
                    if (item?.GetValue("ProtocolName") is string n && n.Trim().Length > 0)
                        result.Add(n.Trim());
            result.Sort(StringComparer.Ordinal);
            return result;
        }
        catch { return null; }
    }

    // ── 共用 ────────────────────────────────────────────────────────────────

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
