namespace XinSpect;

/// <summary>
/// BCD 開機元素現值（U10 第四圈批次三：BD-0002／0004～0007／0011／0014／0023／0024／0030），第 333 輪。
/// </summary>
/// <remarks>
/// <para>
/// <b>回答什麼：</b>第 315 輪答了「先開誰、等幾秒」；這一支答剩下使用者常問的——
/// 工具選單順序、一次性開機覆寫、開機管理器與系統載入器各在哪裡、DEP 政策（nx）、
/// Hypervisor 啟動政策、壞記憶體清單、記憶體遮蔽（「少 1GB」的常見答案）、以及韌體層的順序。
/// </para>
/// <para>
/// <b>同一個讀取面：</b>生產入口以一次 <c>/enum all</c> 快照供開機順序與 OS 元素共用，
/// 韌體物件由獨立的 <c>/enum firmware</c> 讀取。解析共用 <see cref="BootOrderFactsService"/>。
/// 物件缺失為 Unknown；物件存在但欄位缺失
/// 只表示未明寫，不推斷繼承後的有效政策。執行失敗為 ReadError。
/// </para>
/// </remarks>
public static class BcdElementsFactsService
{
    public const string Category = "開機";
    public const string ToolsKey = "bcd.bootmgr.tools";
    public const string BootSequenceKey = "bcd.bootmgr.bootsequence";
    public const string BootmgrDeviceKey = "bcd.bootmgr.device";
    public const string LoaderDeviceKey = "bcd.loader.device";
    public const string NxKey = "bcd.loader.nx";
    public const string HvLaunchKey = "bcd.loader.hvlaunch";
    public const string BadMemoryKey = "bcd.badmemory.list";
    public const string MemTrimKey = "bcd.mem.trim";
    public const string FwBootMgrKey = "bcd.fwbootmgr.order";
    public const string AccessStatementKey = "bcd.access.statement";

    private const string Source = "bcdedit /enum（唯讀；生產收集共用 /enum all 快照）";

    public static IReadOnlyList<HardwareFact> CollectAll(string? output, string? reason, DateTimeOffset at) =>
        Collect(output, output, reason, reason, at);

    /// <summary>純函式：輸入兩次唯讀輸出與各自的失敗理由。</summary>
    public static IReadOnlyList<HardwareFact> Collect(
        string? enumOutput, string? fwOutput, string? enumReason, string? fwReason, DateTimeOffset at)
    {
        bool enumFailed = enumOutput is null;
        var facts = new List<HardwareFact>(10);

        Dictionary<string, List<string>>? bootMgr = null;
        Dictionary<string, List<string>>? loader = null;
        Dictionary<string, List<string>>? badMemory = null;
        Dictionary<string, string> descriptions = new(StringComparer.OrdinalIgnoreCase);

        if (enumOutput is not null)
        {
            var blocks = BootOrderFactsService.ParseBlocks(enumOutput);
            bootMgr = blocks.FirstOrDefault(b => BootOrderFactsService.Identifiers(b)
                .Any(id => id.Equals("{bootmgr}", StringComparison.OrdinalIgnoreCase)));
            badMemory = blocks.FirstOrDefault(b => BootOrderFactsService.Identifiers(b)
                .Any(id => id.Equals("{badmemory}", StringComparison.OrdinalIgnoreCase)));
            descriptions = BootOrderFactsService.DescribeAll(blocks);

            // 系統載入器＝{bootmgr}.default 指到的那個區塊；default 不在就找不到，如實說。
            string? defaultId = bootMgr is null ? null : BootOrderFactsService.Values(bootMgr, "default").FirstOrDefault();
            if (defaultId is not null)
            {
                loader = blocks.FirstOrDefault(b => BootOrderFactsService.Identifiers(b)
                    .Any(id => id.Equals(defaultId, StringComparison.OrdinalIgnoreCase)));
            }
        }

        facts.Add(ToolsFact(bootMgr, descriptions, enumFailed, enumReason, at));
        facts.Add(BootSequenceFact(bootMgr, descriptions, enumFailed, enumReason, at));
        facts.Add(BootmgrDeviceFact(bootMgr, enumFailed, enumReason, at));
        facts.Add(LoaderDeviceFact(loader, enumFailed, enumReason, at));
        facts.Add(SingleFieldFact(NxKey, "DEP 政策（nx）", loader, "nx", enumFailed, enumReason,
            "本載入器沒有 nx 欄位——未在此物件明寫；可能繼承其他物件，不代表有效 DEP 政策", at));
        facts.Add(SingleFieldFact(HvLaunchKey, "Hypervisor 啟動政策（hypervisorlaunchtype）", loader,
            "hypervisorlaunchtype", enumFailed, enumReason,
            "本載入器沒有 hypervisorlaunchtype 欄位——未在此物件明寫；可能繼承其他物件，不代表有效啟動政策", at));
        facts.Add(BadMemoryFact(badMemory, enumFailed, enumReason, at));
        facts.Add(MemTrimFact(loader, enumFailed, enumReason, at));
        facts.Add(FwBootMgrFact(fwOutput, fwReason, at));
        facts.Add(AccessStatementFact(enumFailed, fwOutput is null, at));

        return facts.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    private static HardwareFact ToolsFact(Dictionary<string, List<string>>? bootMgr,
        Dictionary<string, string> descriptions, bool failed, string? reason, DateTimeOffset at)
    {
        if (failed) return ReadError(ToolsKey, "工具選單順序（toolsdisplayorder）", reason, at);
        if (bootMgr is null) return Missing(ToolsKey, "工具選單順序", "{bootmgr}", at);
        var order = BootOrderFactsService.Values(bootMgr, "toolsdisplayorder");
        string value = order.Count == 0
            ? "沒有 toolsdisplayorder 欄位——本物件未明寫工具選單順序，不推斷預設或繼承值"
            : $"{order.Count} 項依序：{string.Join("、", order.Select((id, i) => $"{i + 1}.{descriptions.GetValueOrDefault(id, id)}"))}";
        return new HardwareFact(ToolsKey, Category, "工具選單順序（toolsdisplayorder）", value, "項",
            Source, FactTrustLevel.Reported, false, at,
            order.Count > 0 ? order.Count : null, FactAvailability.Present, null);
    }

    private static HardwareFact BootSequenceFact(Dictionary<string, List<string>>? bootMgr,
        Dictionary<string, string> descriptions, bool failed, string? reason, DateTimeOffset at)
    {
        if (failed) return ReadError(BootSequenceKey, "一次性開機覆寫（bootsequence）", reason, at);
        if (bootMgr is null) return Missing(BootSequenceKey, "一次性開機覆寫", "{bootmgr}", at);
        var seq = BootOrderFactsService.Values(bootMgr, "bootsequence");
        string value = seq.Count == 0
            ? "沒有 bootsequence 欄位——本物件未明寫一次性開機順序，不推斷繼承值"
            : $"{seq.Count} 項（下一次開機生效，開完即失效）：{string.Join("、", seq.Select(id => descriptions.GetValueOrDefault(id, id)))}";
        return new HardwareFact(BootSequenceKey, Category, "一次性開機覆寫（bootsequence）", value, "",
            Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static HardwareFact BootmgrDeviceFact(Dictionary<string, List<string>>? bootMgr,
        bool failed, string? reason, DateTimeOffset at)
    {
        if (failed) return ReadError(BootmgrDeviceKey, "開機管理器位置（device／path）", reason, at);
        if (bootMgr is null) return Missing(BootmgrDeviceKey, "開機管理器位置", "{bootmgr}", at);
        var device = bootMgr.GetValueOrDefault("device", []);
        var path = bootMgr.GetValueOrDefault("path", []);
        string value = (device.Count > 0 || path.Count > 0)
            ? $"device：{(device.Count > 0 ? device[0] : "沒有欄位")}；path：{(path.Count > 0 ? path[0] : "沒有欄位")}"
            : "{bootmgr} 區塊沒有 device／path 欄位——輸出可能被截斷";
        return new HardwareFact(BootmgrDeviceKey, Category, "開機管理器位置（device／path）", value, "",
            Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static HardwareFact LoaderDeviceFact(Dictionary<string, List<string>>? loader,
        bool failed, string? reason, DateTimeOffset at)
    {
        if (failed) return ReadError(LoaderDeviceKey, "系統載入器定位（device／osdevice／path）", reason, at);
        if (loader is null) return Missing(LoaderDeviceKey, "系統載入器定位", "系統載入器（{bootmgr}.default）", at);
        var device = loader.GetValueOrDefault("device", []);
        var osdevice = loader.GetValueOrDefault("osdevice", []);
        var path = loader.GetValueOrDefault("path", []);
        string value = (device.Count > 0 || osdevice.Count > 0 || path.Count > 0)
            ? $"device：{(device.Count > 0 ? device[0] : "沒有")}；osdevice：{(osdevice.Count > 0 ? osdevice[0] : "沒有")}；path：{(path.Count > 0 ? path[0] : "沒有")}"
            : "本載入器未明寫 device／osdevice／path，不推斷預設或繼承值";
        return new HardwareFact(LoaderDeviceKey, Category, "系統載入器定位（device／osdevice／path）", value, "",
            Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static HardwareFact SingleFieldFact(string key, string name,
        Dictionary<string, List<string>>? block, string field, bool failed, string? reason,
        string absentText, DateTimeOffset at)
    {
        if (failed) return ReadError(key, name, reason, at);
        if (block is null) return Missing(key, name, "系統載入器（{bootmgr}.default）", at);
        var values = BootOrderFactsService.Values(block, field);
        return new HardwareFact(key, Category, name,
            values.Count > 0 ? string.Join(" ", values) : absentText, "",
            Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static HardwareFact BadMemoryFact(Dictionary<string, List<string>>? badMemory,
        bool failed, string? reason, DateTimeOffset at)
    {
        if (failed) return ReadError(BadMemoryKey, "壞記憶體清單（badmemorylist）", reason, at);
        if (badMemory is null) return Missing(BadMemoryKey, "壞記憶體清單", "{badmemory}", at);
        var list = BootOrderFactsService.Values(badMemory, "badmemorylist");
        var access = BootOrderFactsService.Values(badMemory, "badmemoryaccess");
        string value = list.Count == 0
            ? "{badmemory} 沒有明寫 badmemorylist 欄位"
            : $"{list.Count} 個頁框：{string.Join(" ", list)}；badmemoryaccess：{(access.Count > 0 ? access[0] : "沒有欄位")}";
        value += "；僅列 BCD 物件。Windows build 19042 起 PFA 壞頁清單另存 WHEA\\BadPages 登錄檔，本鍵未讀取；不能推斷 OS 沒有壞頁或記憶體健康";
        return new HardwareFact(BadMemoryKey, Category, "壞記憶體清單（badmemorylist）", value, "頁框",
            Source, FactTrustLevel.Reported, false, at, list.Count > 0 ? list.Count : null,
            FactAvailability.Present, null);
    }

    private static HardwareFact MemTrimFact(Dictionary<string, List<string>>? loader,
        bool failed, string? reason, DateTimeOffset at)
    {
        if (failed) return ReadError(MemTrimKey, "記憶體遮蔽（removememory／truncatememory）", reason, at);
        if (loader is null) return Missing(MemTrimKey, "記憶體遮蔽", "系統載入器（{bootmgr}.default）", at);
        var remove = BootOrderFactsService.Values(loader, "removememory");
        var truncate = BootOrderFactsService.Values(loader, "truncatememory");
        string value = (remove.Count == 0 && truncate.Count == 0)
            ? "沒有 removememory／truncatememory 欄位——本載入器未明寫，不排除繼承的記憶體限制"
            : $"removememory：{(remove.Count > 0 ? remove[0] + " MB" : "未明寫")}；truncatememory：{(truncate.Count > 0 ? truncate[0] + " bytes（實體位址上限）" : "未明寫")}"
            + "・「裝的記憶體比看到的少」時，這裡是第一個該看的地方";
        return new HardwareFact(MemTrimKey, Category, "記憶體遮蔽（removememory／truncatememory）", value, "",
            Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static HardwareFact FwBootMgrFact(string? fwOutput, string? fwReason, DateTimeOffset at)
    {
        if (fwOutput is null)
        {
            return new HardwareFact(FwBootMgrKey, Category, "韌體開機管理器順序（{fwbootmgr}）", "", "項",
                Source, FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                fwReason ?? "沒有讀到 bcdedit /enum firmware 的輸出");
        }

        var blocks = BootOrderFactsService.ParseBlocks(fwOutput);
        var fw = blocks.FirstOrDefault(b => BootOrderFactsService.Identifiers(b)
            .Any(id => id.Equals("{fwbootmgr}", StringComparison.OrdinalIgnoreCase)));
        if (fw is null)
        {
            return new HardwareFact(FwBootMgrKey, Category, "韌體開機管理器順序（{fwbootmgr}）", "", "項",
                Source, FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
                "輸出裡沒有 {fwbootmgr} 區塊——未確定是非 UEFI 或輸出不完整，不推斷平台類型");
        }

        var descriptions = BootOrderFactsService.DescribeAll(blocks);
        var order = BootOrderFactsService.Values(fw, "displayorder");
        string? defaultId = BootOrderFactsService.Values(fw, "default").FirstOrDefault();
        string value = order.Count == 0
            ? "{fwbootmgr} 存在但沒有 displayorder 欄位"
            : $"{order.Count} 項依序：{string.Join("、", order.Select((id, i) => $"{i + 1}.{descriptions.GetValueOrDefault(id, id)}"))}"
            + (defaultId is null ? "" : $"；預設：{defaultId}")
            + "・這是韌體層（UEFI BootOrder 的 BCD 視角），與 os 層的 displayorder 各自獨立";
        return new HardwareFact(FwBootMgrKey, Category, "韌體開機管理器順序（{fwbootmgr}）", value, "項",
            Source, FactTrustLevel.Reported, false, at, order.Count > 0 ? order.Count : null,
            FactAvailability.Present, null);
    }

    private static HardwareFact AccessStatementFact(bool enumFailed, bool fwFailed, DateTimeOffset at) =>
        new HardwareFact(AccessStatementKey, Category, "BCD 存取法與新鮮度宣告（BD-0030）",
            "① 生產存取法＝bcdedit /enum all 與 /enum firmware 各一次（唯讀、各 5 秒逾時、System32 絕對路徑），OS 層共用快照；"
            + "② 執行當下現讀——沒有快取；OS 層與韌體層各自記錄收集時刻；"
            + "③ 不讀登錄檔 hive 副本（SYSTEM\\CurrentControlSet\\Control\\hivelist 的 BCD 字樣不是現值來源）；"
            + "④ 缺物件＝Unknown；缺欄位僅表示本物件未明寫，未解析繼承政策。讀取狀態：" + (enumFailed ? "os 層失敗；" : "") + (fwFailed ? "韌體層失敗；" : "")
            + "⑤ 本服務不寫 BCD——/set、/delete 不在承諾範圍",
            "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, null);

    private static HardwareFact Missing(string key, string name, string block, DateTimeOffset at) =>
        new HardwareFact(key, Category, name, "", "", Source, FactTrustLevel.Unknown, false, at,
            null, FactAvailability.Unknown, $"找不到 {block} 區塊，不能把缺失物件當成沒有設定");

    private static HardwareFact ReadError(string key, string name, string? reason, DateTimeOffset at) =>
        new HardwareFact(key, Category, name, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
            string.IsNullOrWhiteSpace(reason) ? "沒有讀到 bcdedit /enum 的輸出" : reason);
}
