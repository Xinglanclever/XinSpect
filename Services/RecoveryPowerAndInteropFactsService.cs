using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// 電源、恢復與開發者互通（Vol2 推進第 143–152 項）：RC-013 休眠檔、RC-014 快速啟動、
/// PW-002 USB 選擇性暫停、PW-007 停機時長、IE-002 Grafana、IE-005 PowerShell 範本、
/// IE-006 deep-link、IE-007 參數補完、DK-003 別名、DK-008 快照期限。
/// </summary>
/// <remarks>
/// <para><b>唯讀與零網路</b>：只讀登錄、檔案 metadata、事件記錄或產生文字／JSON；不寫設定、不登錄 URI、不呼叫外部命令。</para>
/// <para><b>時間是痕跡</b>：停機時長是「關機事件到下次開機事件」的差，不是單次關機程序的精確耗時。</para>
/// </remarks>
public static class RecoveryPowerAndInteropFactsService
{
    private const string CatRecovery = "電源與恢復";
    private const string CatInterop = "整合與開發者";
    private const string CatData = "資料生命週期";

    public const string HibernateKey = "rc.hiber.summary";
    public const string FastStartupKey = "rc.fastboot.summary";
    public const string UsbSuspendKey = "pw.usbsel";
    public const string ShutdownKey = "pw.shutdown.summary";
    public const string GrafanaKey = "ie.grafana";
    public const string PowerShellTemplateKey = "ie.ps.template";
    public const string DeepLinkKey = "ie.deeplink";
    public const string CompletionKey = "ie.completion";
    public const string AliasKey = "dk.alias.summary";
    public const string SnapshotAgeKey = "dk.snapshot.age";

    public sealed record HibernationState(bool FileExists, long? SizeBytes, int? HiberbootEnabled);
    public sealed record UsbSuspendState(int? AcSettingIndex, int? DcSettingIndex);
    public sealed record ShutdownMarker(DateTimeOffset When, int EventId);

    public delegate HibernationState? HibernationProbe();
    public delegate UsbSuspendState? UsbSuspendProbe();
    public delegate IReadOnlyList<ShutdownMarker>? ShutdownProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at)
    {
        HibernationState? hiber;
        try { hiber = ReadHibernation(); } catch { hiber = null; }
        return
        [
            HibernationFact(at, hiber),
            FastStartupFact(at, hiber),
            UsbSuspendFact(at),
            ShutdownSummaryFact(at),
            GrafanaFact(at),
            PowerShellTemplateFact(at),
            DeepLinkFact(at),
            CompletionFact(at),
            AliasFact(at),
            SnapshotAgeFact(at),
        ];
    }

    // ── RC-013 休眠檔 ───────────────────────────────────────────────────────

    public static HardwareFact HibernationFact(DateTimeOffset at, HibernationProbe? probe = null)
    {
        HibernationState? state;
        try { state = (probe ?? new HibernationProbe(ReadHibernation))(); }
        catch { state = null; }
        return HibernationFact(at, state);
    }

    private static HardwareFact HibernationFact(DateTimeOffset at, HibernationState? state)
    {
        if (state is null)
            return Unavailable(at, HibernateKey, CatRecovery, "休眠檔審計",
                FactAvailability.ReadError, "休眠檔或 HiberbootEnabled 讀取失敗——不猜檔案大小");
        if (!state.FileExists)
        {
            if (state.HiberbootEnabled == 0)
                return new HardwareFact(HibernateKey, CatRecovery, "休眠檔審計",
                    "登錄顯示休眠／快速啟動停用，且系統根目錄未見 hiberfil.sys。界線：未見不等於檔案不存在（受保護路徑可能不可讀）。",
                    "", "系統磁碟根目錄＋HiberbootEnabled", FactTrustLevel.Reported, false, at, null,
                    FactAvailability.NotApplicable, "休眠功能停用，沒有啟用中的休眠檔");
            return new HardwareFact(HibernateKey, CatRecovery, "休眠檔審計",
                "系統根目錄未看到 hiberfil.sys，但休眠登錄不是明確停用。界線：不把未讀到當成 0 位元組。", "",
                "系統磁碟根目錄＋HiberbootEnabled", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.Unknown, "休眠檔不可見，且登錄狀態未能確認");
        }
        long size = state.SizeBytes ?? 0;
        string enabled = state.HiberbootEnabled switch
        {
            1 => "登錄啟用",
            0 => "登錄停用",
            _ => "登錄未設定",
        };
        return new HardwareFact(HibernateKey, CatRecovery, "休眠檔審計",
            $"hiberfil.sys 大小 {size / (1024.0 * 1024.0):F1} MiB（{enabled}）。界線：這是休眠檔案當下的大小，不代表休眠可用性或內容。",
            "MiB", "系統磁碟根目錄＋HiberbootEnabled", FactTrustLevel.Reported, false, at, size / (1024.0 * 1024.0),
            FactAvailability.Present);
    }

    private static HibernationState? ReadHibernation()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var info = new FileInfo(Path.Combine(root, "hiberfil.sys"));
        long? size = info.Exists ? info.Length : null;
        int? enabled = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            enabled = key?.GetValue("HiberbootEnabled") is int v ? v : null;
        }
        catch { /* 讀不到登錄如實留 null */ }
        return new HibernationState(info.Exists, size, enabled);
    }

    // ── RC-014 快速啟動 ─────────────────────────────────────────────────────

    public static HardwareFact FastStartupFact(DateTimeOffset at, HibernationProbe? probe = null)
    {
        HibernationState? state;
        try { state = (probe ?? new HibernationProbe(ReadHibernation))(); }
        catch { state = null; }
        return FastStartupFact(at, state);
    }

    private static HardwareFact FastStartupFact(DateTimeOffset at, HibernationState? state)
    {
        if (state is null)
            return Unavailable(at, FastStartupKey, CatRecovery, "快速啟動設定",
                FactAvailability.ReadError, "HiberbootEnabled 讀取失敗——不猜預設值");
        return state.HiberbootEnabled switch
        {
            1 => new HardwareFact(FastStartupKey, CatRecovery, "快速啟動設定", "已啟用（HiberbootEnabled=1）", "",
                "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power", FactTrustLevel.Reported, false, at, 1),
            0 => new HardwareFact(FastStartupKey, CatRecovery, "快速啟動設定", "已停用（HiberbootEnabled=0）", "",
                "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power", FactTrustLevel.Reported, false, at, 0),
            _ => new HardwareFact(FastStartupKey, CatRecovery, "快速啟動設定", "", "",
                "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.Unknown, "HiberbootEnabled 未設定；Windows 預設值不能代填成這台機器的設定"),
        };
    }

    // ── PW-002 USB 選擇性暫停 ───────────────────────────────────────────────

    public static HardwareFact UsbSuspendFact(DateTimeOffset at, UsbSuspendProbe? probe = null)
    {
        UsbSuspendState? state;
        try { state = (probe ?? new UsbSuspendProbe(ReadUsbSuspend))(); }
        catch { state = null; }
        if (state is null)
            return Unavailable(at, UsbSuspendKey, CatRecovery, "USB 選擇性暫停",
                FactAvailability.ReadError, "作用中電源計畫的選擇性暫停設定讀取失敗——不猜支援");
        if (state.AcSettingIndex is null && state.DcSettingIndex is null)
            return Unavailable(at, UsbSuspendKey, CatRecovery, "USB 選擇性暫停",
                FactAvailability.Unknown, "作用中電源計畫沒有這兩個設定索引");
        return new HardwareFact(UsbSuspendKey, CatRecovery, "USB 選擇性暫停",
            $"AC={SuspendText(state.AcSettingIndex)}；DC={SuspendText(state.DcSettingIndex)}。界線：這是目前電源計畫設定，不代表每個 USB 埠都一定進入暫停。",
            "", "作用中電源計畫：2a737441-1930-4402-8d77-b2bebba308a3／48e6b7a6-50f5-4782-a5d4-53bb8f07e226",
            FactTrustLevel.Reported, false, at, state.AcSettingIndex ?? state.DcSettingIndex);
    }

    private static string SuspendText(int? value) => value switch
    {
        0 => "停用",
        1 => "啟用",
        null => "未提供",
        _ => $"未收錄值 {value}",
    };

    private static UsbSuspendState? ReadUsbSuspend()
    {
        const string basePath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var schemes = baseKey.OpenSubKey(basePath);
        string? active = schemes?.GetValue("ActivePowerScheme") as string;
        if (string.IsNullOrWhiteSpace(active)) return null;
        using var key = schemes!.OpenSubKey(active + @"\2a737441-1930-4402-8d77-b2bebba308a3\48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        if (key is null) return new UsbSuspendState(null, null);
        return new UsbSuspendState(ReadInt(key, "ACSettingIndex"), ReadInt(key, "DCSettingIndex"));
    }

    private static int? ReadInt(RegistryKey key, string name) => key.GetValue(name) is int v ? v : null;

    // ── PW-007 停機時長 ────────────────────────────────────────────────────

    public static HardwareFact ShutdownSummaryFact(DateTimeOffset at, ShutdownProbe? probe = null)
    {
        IReadOnlyList<ShutdownMarker>? events;
        try { events = (probe ?? new ShutdownProbe(ReadShutdownMarkers))(); }
        catch { events = null; }
        if (events is null)
            return Unavailable(at, ShutdownKey, CatRecovery, "最近停機時長",
                FactAvailability.ReadError, "System 事件記錄的 6005／6006 查詢失敗——不猜停機時長");
        var ordered = events.OrderBy(x => x.When).ToList();
        var pairs = new List<(DateTimeOffset Shutdown, DateTimeOffset Start)>();
        for (int i = 0; i + 1 < ordered.Count; i++)
            if (ordered[i].EventId == 6006 && ordered[i + 1].EventId == 6005 && ordered[i + 1].When >= ordered[i].When)
                pairs.Add((ordered[i].When, ordered[i + 1].When));
        if (pairs.Count == 0)
            return Unavailable(at, ShutdownKey, CatRecovery, "最近停機時長",
                FactAvailability.NotApplicable, "沒有可配對的 6006（關機）與 6005（開機）事件——不是 0 分鐘");
        var last = pairs.OrderByDescending(x => x.Start).First();
        double minutes = (last.Start - last.Shutdown).TotalMinutes;
        return new HardwareFact(ShutdownKey, CatRecovery, "最近停機時長",
            $"最近一段停機 {minutes:F1} 分鐘（{last.Shutdown:yyyy-MM-dd HH:mm} → {last.Start:yyyy-MM-dd HH:mm}）；可配對樣本 {pairs.Count} 段。界線：這是關機事件到下次開機事件，不是單次關機程序的精確耗時。",
            "分鐘", "System EventLog 6006（關機）／6005（開機）", FactTrustLevel.Reported, false, at, minutes);
    }

    private static IReadOnlyList<ShutdownMarker>? ReadShutdownMarkers()
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[(EventID=6005 or EventID=6006)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            var list = new List<ShutdownMarker>();
            for (int i = 0; i < 200; i++)
            {
                using var evt = reader.ReadEvent();
                if (evt is null) break;
                if (evt.TimeCreated is { } time && evt.Id is 6005 or 6006)
                    list.Add(new ShutdownMarker(new DateTimeOffset(time, TimeSpan.Zero), evt.Id));
            }
            return list;
        }
        catch { return null; }
    }

    // ── IE-002 Grafana ─────────────────────────────────────────────────────

    [SpecRef("Grafana dashboard JSON schema：title／schemaVersion／panels；輸出只含鍵名與面板骨架，不含本機值。")]
    public static string BuildGrafanaDashboard(IEnumerable<string> keys, string title = "曦覽 XinSpect")
    {
        var panels = (keys ?? []).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)
            .Select((key, i) => new { id = i + 1, title = key, type = "stat", gridPos = new { h = 5, w = 6, x = (i % 4) * 6, y = (i / 4) * 5 } })
            .ToList();
        return JsonSerializer.Serialize(new { title, schemaVersion = 39, panels, boundary = "只含鍵名與面板骨架；不連網、不上傳、不內嵌本機值。" },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static HardwareFact GrafanaFact(DateTimeOffset at)
        => Feature(at, GrafanaKey, CatInterop, "Grafana 匯出",
            "可由固定鍵清單產生 Grafana dashboard JSON（title／schemaVersion／panels 骨架）；零網路，值不內嵌。界線：匯出的是面板骨架，不是 Grafana 連線或資料來源。",
            "RecoveryPowerAndInteropFactsService.BuildGrafanaDashboard");

    // ── IE-005 PowerShell 範本 ─────────────────────────────────────────────

    [SpecRef("PowerShell 範本只示範本機 CLI 呼叫；查詢由 --json evidence --query 完成，不用網路 cmdlet。")]
    public static string GeneratePowerShellTemplate(string queryPrefix, string outputPath)
    {
        string q = EscapePowerShell(queryPrefix);
        string outPath = EscapePowerShell(outputPath);
        return $"$out = '{outPath}'\n" +
               $"& .\\XinSpect.exe --json evidence --query '{q}' --out $out\n" +
               "if ($LASTEXITCODE -eq 0) { Write-Host '全部 Present' }\n" +
               "elseif ($LASTEXITCODE -eq 2) { Write-Host '部分事實讀不到；詳見 JSON' }\n" +
               "else { throw '收集失敗' }\n";
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static HardwareFact PowerShellTemplateFact(DateTimeOffset at)
        => Feature(at, PowerShellTemplateKey, CatInterop, "PowerShell 查詢範本",
            "可產生只呼叫本機 XinSpect.exe 的 PowerShell 範本（--json evidence --query --out；退出碼 0/2/1）。零網路，不含 Invoke-WebRequest／curl。界線：範例不執行，也不上傳結果。",
            "RecoveryPowerAndInteropFactsService.GeneratePowerShellTemplate");

    // ── IE-006 deep link ─────────────────────────────────────────────────────

    [SpecRef("xinspect URI scheme 的本程式約定：xinspect://<page>?key=<url-encoded-key>；產生不等於註冊。")]
    public static string BuildDeepLink(string page, string? key = null)
    {
        string p = Uri.EscapeDataString(page);
        return key is null ? $"xinspect://{p}" : $"xinspect://{p}?key={Uri.EscapeDataString(key)}";
    }

    private static HardwareFact DeepLinkFact(DateTimeOffset at)
        => Feature(at, DeepLinkKey, CatInterop, "deep-link scheme",
            "可把頁面與事實鍵格式化成 xinspect://page?key=…（percent-encoded）。界線：只產生字串，不註冊 scheme、不啟動瀏覽器、不上傳。",
            "RecoveryPowerAndInteropFactsService.BuildDeepLink");

    // ── IE-007 參數補完 ─────────────────────────────────────────────────────

    [SpecRef("PowerShell Register-ArgumentCompleter 語法；補完清單由 CliService 的公開常數組成，零網路。")]
    public static string GeneratePowerShellCompleter()
    {
        string[] flags = [CliService.JsonArg, CliService.CompareArg, CliService.VerifyAuditArg, CliService.IntegrityBaselineArg, CliService.SbomArg, CliService.DiskUsageArg, CliService.ErrorsArg, CliService.HelpArg, CliService.QueryArg, CliService.OutArg, CliService.DeepArg, CliService.StdinBatchArg];
        string values = string.Join(",", flags.Select(x => "'" + x + "'"));
        return "Register-ArgumentCompleter -Native -CommandName XinSpect.exe -ScriptBlock {\n" +
               "    param($wordToComplete, $commandAst, $cursorPosition)\n" +
               $"    @({values}) | Where-Object {{ $_ -like \"$wordToComplete*\" }} | ForEach-Object {{ [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterName', $_) }}\n" +
               "}\n";
    }

    private static HardwareFact CompletionFact(DateTimeOffset at)
        => Feature(at, CompletionKey, CatInterop, "CLI 參數自動完成",
            "可產生 PowerShell Register-ArgumentCompleter 腳本，補完 CLI 參數；零網路。界線：只產生腳本，不自動寫入使用者 profile。",
            "RecoveryPowerAndInteropFactsService.GeneratePowerShellCompleter");

    // ── DK-003 別名 ────────────────────────────────────────────────────────

    public static string? ResolveAlias(string alias)
        => AliasMap.TryGetValue(alias.Trim(), out string? key) ? key : null;

    private static readonly IReadOnlyDictionary<string, string> AliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"] = "cpu.generation",
        ["gpu"] = "display.hw.gpu",
        ["boot"] = "boot.duration_ms",
        ["tpm"] = "tpm.present",
        ["secureboot"] = "platform.secure_boot",
        ["net"] = "net.offload",
        ["audio"] = "audio.endpoints",
        ["storage"] = "storage.reliability.count",
        ["printer"] = "sg.print",
    };

    private static HardwareFact AliasFact(DateTimeOffset at)
        => Feature(at, AliasKey, CatData, "鍵別名字典",
            "提供有限別名到正式事實鍵的映射（cpu／gpu／boot／tpm／secureboot／net／audio／storage／printer）；未知別名回 null，不猜。界線：別名只供查詢，不改變事實鍵。",
            "RecoveryPowerAndInteropFactsService.ResolveAlias");

    // ── DK-008 快照期限 ─────────────────────────────────────────────────────

    [SpecRef("本程式的快照生命週期標籤：capturedAtUtc 早於 warnDays＝新鮮、到 expireDays＝提醒、超過＝過期；時鐘倒轉不判新鮮。")]
    public static string SnapshotAgeLabel(DateTimeOffset capturedAtUtc, DateTimeOffset nowUtc, int warnDays = 30, int expireDays = 90)
    {
        if (warnDays < 0 || expireDays < warnDays) throw new ArgumentOutOfRangeException(nameof(expireDays));
        var age = nowUtc - capturedAtUtc;
        if (age < TimeSpan.Zero) return "時間戳晚於現在（時鐘異常，不判新鮮）";
        if (age.TotalDays <= warnDays) return "新鮮";
        if (age.TotalDays <= expireDays) return "提醒（快照已超過建議新鮮期）";
        return "過期（請重新擷取再比較）";
    }

    private static HardwareFact SnapshotAgeFact(DateTimeOffset at)
        => Feature(at, SnapshotAgeKey, CatData, "快照期限標籤",
            "可依擷取時間與現在時間標示新鮮／提醒／過期；時間戳晚於現在時不判新鮮。界線：標籤是保存政策提示，不是硬體狀態。",
            "RecoveryPowerAndInteropFactsService.SnapshotAgeLabel");

    // ── helpers ─────────────────────────────────────────────────────────────

    private static HardwareFact Feature(DateTimeOffset at, string key, string category, string name, string value, string source)
        => new(key, category, name, value, "", source, FactTrustLevel.Derived, false, at);

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
