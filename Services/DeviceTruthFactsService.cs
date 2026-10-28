namespace XinSpect;

/// <summary>
/// 把裝置診斷的結果投影成事實（U10 第三圈 OS-0002／OS-0003／OS-0004，第 309 輪）。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要有這一支：</b>裝置診斷頁早就把「這台裝置的問題碼是什麼、驅動哪一版、簽章對不對」
/// 算出來了，但它只活在畫面裡——時間膠囊、報告、CLI 看不到，於是「為什麼這台的讀值全是 —」
/// 這個問題只能靠人開著視窗看。這裡把它接進事實層，讓每一台有事的裝置都有自己的鍵。
/// </para>
/// <para>
/// <b>三條界線（與六態一致）：</b>
/// ①問題碼 0（健康）不進明細——把它列進來會讓「有 150 台裝置」看起來像「有 150 個問題」，
/// 健康數量由彙總鍵回答；②問題碼讀不到（<c>null</c>）寫 <see cref="FactAvailability.ReadError"/>，
/// 不是 0——「Windows 沒回報」與「Windows 說沒問題」是兩件事；③值裡不放 instance ID
/// （那串常含序號），裝置用類別＋名稱辨識。
/// </para>
/// </remarks>
public static class DeviceTruthFactsService
{
    /// <summary>逐裝置問題碼家族：<c>os.dev.issue.0001</c>…（依 instance ID 排序，鍵穩定）。</summary>
    public const string IssuePrefix = "os.dev.issue.";

    /// <summary>逐裝置狀態旗標家族：<c>os.dev.status.0001</c>…（只收「有 finding」的裝置）。</summary>
    public const string StatusPrefix = "os.dev.status.";

    /// <summary>逐裝置驅動證據家族：<c>os.dev.driver.0001</c>…（提供者／版本／日期／簽章）。</summary>
    public const string DriverPrefix = "os.dev.driver.";

    /// <summary>彙總鍵：在場／不在場／有問題／缺驅動／未簽章／過舊的台數。</summary>
    public const string SummaryKey = "os.dev.summary";

    /// <summary>每個家族最多列幾台——超過由彙總鍵說明還有幾台沒列出（不靜默截斷）。</summary>
    public const int MaxDevicesPerFamily = 200;

    private const string Category = "裝置（PnP）";
    private const string Source = "SetupAPI 裝置列舉＋驅動證據（唯讀）";

    /// <summary>把一份裝置診斷報告投影成事實；不做任何 I/O，可重跑、可測試。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DeviceDiagnosticReport report, DateTimeOffset at)
        => Collect(report, at, MaxDevicesPerFamily);

    /// <summary>同上，可指定每家族上限（測試用；正式路徑用預設值）。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DeviceDiagnosticReport report, DateTimeOffset at, int maxPerFamily)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (maxPerFamily < 1) throw new ArgumentOutOfRangeException(nameof(maxPerFamily));

        // 依 instance ID 排序：成員由機器決定，但**順序不能由機器決定**——
        // 用 FriendlyName 排（受語系影響）會讓同一台機器兩次執行得到不同的鍵。
        var devices = report.Devices
            .OrderBy(static d => d.InstanceId, StringComparer.Ordinal)
            .ToList();

        var facts = new List<HardwareFact>();

        // 問題碼 null（沒有回報）也要進來：那是「Windows 沒說」，不是「Windows 說沒問題」。
        // 只有明確回報 0 的裝置才算健康、不進明細。
        var issues = devices
            .Where(static d => d.ProblemCode is null or > 0 || d.IsGhost)
            .ToList();
        AppendFamily(facts, IssuePrefix, issues, maxPerFamily, at, BuildIssue);

        var flagged = devices.Where(static d => d.Findings.Count > 0).ToList();
        AppendFamily(facts, StatusPrefix, flagged, maxPerFamily, at, BuildStatus);

        // 只有拿得到驅動證據的裝置才進驅動家族；其餘（系統內建節點）由彙總說出台數——
        // 替每一台根節點寫一把「讀不到驅動」的鍵，只會把真正的問題淹掉。
        var drivers = devices
            .Where(static d => d.IsPresent && HasDriverEvidence(d))
            .ToList();
        AppendFamily(facts, DriverPrefix, drivers, maxPerFamily, at, BuildDriver);

        facts.Add(SummaryFact(report, devices.Count, issues, flagged.Count, drivers.Count, maxPerFamily, at));
        return facts;
    }

    private static void AppendFamily(
        List<HardwareFact> facts,
        string prefix,
        List<DeviceDiagnosticDevice> devices,
        int maxPerFamily,
        DateTimeOffset at,
        Func<DeviceDiagnosticDevice, DateTimeOffset, HardwareFact> build)
    {
        int take = Math.Min(devices.Count, maxPerFamily);
        for (int i = 0; i < take; i++)
        {
            var raw = build(devices[i], at);
            facts.Add(raw with { Key = prefix + (i + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    private static HardwareFact BuildIssue(DeviceDiagnosticDevice device, DateTimeOffset at)
    {
        string name = Describe(device);
        if (device.ProblemCode is not { } code)
        {
            return new HardwareFact(IssuePrefix, Category, name, "", "", Source, FactTrustLevel.Reported, false, at,
                null, FactAvailability.ReadError,
                "Windows 沒有回報這台裝置的問題碼——「沒回報」不等於「沒問題」。");
        }

        string text = code == 0
            ? "問題碼 0：Windows 未回報此裝置有問題。"
            : "問題碼 " + code.ToString(System.Globalization.CultureInfo.InvariantCulture) + "：" + Explanation(device);
        if (device.IsGhost)
            text = "目前不在場（幽靈裝置）；" + text;

        return new HardwareFact(IssuePrefix, Category, name, text, "", Source, FactTrustLevel.Reported, false, at,
            code, FactAvailability.Present, null);
    }

    private static HardwareFact BuildStatus(DeviceDiagnosticDevice device, DateTimeOffset at)
    {
        var summaries = device.Findings
            .Select(static f => f.Summary)
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 有 finding 卻一句話都寫不出來：這是「有問題但說不出」——如實標，不要給一個空字串假裝成功。
        if (summaries.Count == 0)
        {
            return new HardwareFact(StatusPrefix, Category, Describe(device), "", "", Source, FactTrustLevel.Reported,
                false, at, null, FactAvailability.ReadError,
                "這一台被判為有問題，但沒有任何一句可讀的說明（判定器沒有填 Summary）。");
        }

        string value = device.PresenceText + "；" + string.Join("；", summaries);
        return new HardwareFact(StatusPrefix, Category, Describe(device), value, "", Source,
            FactTrustLevel.Reported, false, at, summaries.Count, FactAvailability.Present, null);
    }

    private static HardwareFact BuildDriver(DeviceDiagnosticDevice device, DateTimeOffset at)
    {
        bool providerKnown = HasText(device.DriverProvider);
        bool versionKnown = HasText(device.DriverVersion);
        if (!providerKnown && !versionKnown)
        {
            return new HardwareFact(DriverPrefix, Category, Describe(device), "", "", Source, FactTrustLevel.Reported,
                false, at, null, FactAvailability.ReadError,
                "這台裝置在場，但讀不到驅動提供者與版本（多半是系統內建節點，沒有獨立驅動套件）。");
        }

        var parts = new List<string>();
        if (providerKnown) parts.Add("提供者 " + device.DriverProvider);
        if (versionKnown) parts.Add("版本 " + device.DriverVersion);
        if (device.DriverDate is { } date) parts.Add("日期 " + date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        parts.Add(device.SignedText);

        return new HardwareFact(DriverPrefix, Category, Describe(device), string.Join(" ・ ", parts), "", Source,
            FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static HardwareFact SummaryFact(
        DeviceDiagnosticReport report,
        int enumerated,
        List<DeviceDiagnosticDevice> issues,
        int flagged,
        int drivers,
        int maxPerFamily,
        DateTimeOffset at)
    {
        if (enumerated == 0)
        {
            return new HardwareFact(SummaryKey, Category, "PnP 裝置彙總", "", "台", Source, FactTrustLevel.Reported,
                false, at, null, FactAvailability.NotApplicable,
                "列舉回 0 台——這不是「這台機器沒有裝置」，是這一輪沒有讀到（見裝置診斷頁的來源狀態）。");
        }

        int problemCode = report.Devices.Count(static d => d.ProblemCode is > 0);
        int unreported = report.Devices.Count(static d => d.ProblemCode is null);
        int missingDriver = report.Devices.Count(static d => d.Findings.Any(f => f.Kind == DeviceDiagnosticFindingKind.MissingDriver));
        int unsigned = report.Devices.Count(static d => d.Findings.Any(f => f.Kind == DeviceDiagnosticFindingKind.UnsignedDriver));
        int oldDriver = report.Devices.Count(static d => d.Findings.Any(f => f.Kind == DeviceDiagnosticFindingKind.OldCriticalDriver));

        string value = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "在場 {0} 台／不在場 {1} 台；有問題碼 {2} 台、未回報問題碼 {3} 台、缺驅動 {4} 台、未簽章 {5} 台、關鍵驅動超過 5 年 {6} 台；有驅動證據 {7} 台",
            report.PresentCount, report.GhostCount, problemCode, unreported, missingDriver, unsigned, oldDriver, drivers);

        // 截斷要說出來：使用者看到的清單少於實際台數時，原因必須寫在同一個地方。
        if (issues.Count > maxPerFamily)
            value += "；問題清單只列前 " + maxPerFamily + " 台，另有 " + (issues.Count - maxPerFamily) + " 台未列出";
        if (flagged > maxPerFamily)
            value += "；狀態清單只列前 " + maxPerFamily + " 台，另有 " + (flagged - maxPerFamily) + " 台未列出";
        if (drivers > maxPerFamily)
            value += "；驅動清單只列前 " + maxPerFamily + " 台，另有 " + (drivers - maxPerFamily) + " 台未列出";

        return new HardwareFact(SummaryKey, Category, "PnP 裝置彙總", value, "台", Source,
            FactTrustLevel.Reported, false, at, report.PresentCount, FactAvailability.Present, null);
    }

    /// <summary>類別＋名稱（不含 instance ID：那串常含序號）。</summary>
    private static string Describe(DeviceDiagnosticDevice device)
    {
        string cls = HasText(device.DeviceClass) ? device.DeviceClass : "未分類";
        string name = HasText(device.FriendlyName) ? device.FriendlyName : device.Description;
        if (!HasText(name)) name = "（沒有名稱）";
        return cls + " ・ " + name;
    }

    private static string Explanation(DeviceDiagnosticDevice device)
    {
        if (HasText(device.ProblemExplanation)) return device.ProblemExplanation;
        if (HasText(device.ProblemCodeText)) return device.ProblemCodeText;
        return "Windows 回報了問題碼，但本程式沒有這個代碼的說明——請查 SetupAPI 的 CM_PROB 對照表。";
    }

    private static bool HasDriverEvidence(DeviceDiagnosticDevice device)
        => HasText(device.DriverProvider) || HasText(device.DriverVersion);

    private static bool HasText(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.Equals(DeviceDiagnosticUnknown.Value, StringComparison.Ordinal);
}
