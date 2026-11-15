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
/// <b>同一個讀取面：</b>輸入是 <see cref="BootOrderReader"/> 的兩次唯讀輸出
/// （<c>/enum</c> 與 <c>/enum firmware</c>），解析共用 <see cref="BootOrderFactsService"/> 的
/// 公開助手——沒有第二個 BCD 解析器。欄位不存在＝Present 帶說明（「沒有設定」），
/// 讀不到才 ReadError；**「沒有設定」與「讀不到」是兩個斷言**。
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

    private const string Source = "bcdedit /enum 與 /enum firmware（唯讀）";

    /// <summary>純函式：輸入兩次唯讀輸出與各自的失敗理由。</summary>
    public static IReadOnlyList<HardwareFact> Collect(
        string? enumOutput, string? fwOutput, string? enumReason, string? fwReason, DateTimeOffset at)
    {
        bool enumFailed = enumOutput is null;
        var facts = new List<HardwareFact>(10);

        Dictionary<string, List<string>> bootMgr = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> loader = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> descriptions = new(StringComparer.OrdinalIgnoreCase);
        List<string> displayOrder = [];

        if (!enumFailed)
        {
            var blocks = BootOrderFactsService.ParseBlocks(enumOutput);
            bootMgr = blocks.FirstOrDefault(b => BootOrderFactsService.Identifiers(b)
                .Any(id => id.Equals("{bootmgr}", StringComparison.OrdinalIgnoreCase))) ?? bootMgr;
            descriptions = BootOrderFactsService.DescribeAll(blocks);
            displayOrder = BootOrderFactsService.Values(bootMgr, "displayorder");

            // 系統載入器＝{bootmgr}.default 指到的那個區塊；default 不在就找不到，如實說。
            string? defaultId = BootOrderFactsService.Values(bootMgr, "default").FirstOrDefault();
            if (defaultId is not null)
            {
                loader = blocks.FirstOrDefault(b => BootOrderFactsService.Identifiers(b)
                    .Any(id => id.Equals(defaultId, StringComparison.OrdinalIgnoreCase))) ?? loader;
            }
        }

        facts.Add(ToolsFact(bootMgr, descriptions, enumFailed, enumReason, at));
        facts.Add(BootSequenceFact(bootMgr, descriptions, enumFailed, enumReason, at));
        facts.Add(BootmgrDeviceFact(bootMgr, enumFailed, enumReason, at));
        facts.Add(LoaderDeviceFact(loader, enumFailed, enumReason, at));
        facts.Add(SingleFieldFact(NxKey, "DEP 政策（nx）", loader, "nx", enumFailed, enumReason,
            "沒有 nx 欄位——DEP 政策未在 BCD 明寫，現行值由 OS 決定，本程式不從 WMI 反推", at));
        facts.Add(SingleFieldFact(HvLaunchKey, "Hypervisor 啟動政策（hypervisorlaunchtype）", loader,
            "hypervisorlaunchtype", enumFailed, enumReason,
            "沒有 hypervisorlaunchtype 欄位——啟動政策未在 BCD 明寫；本程式不從 CPUID/ECFER 反推（與 virt.* 各自獨立）", at));
        facts.Add(BadMemoryFact(loader, enumFailed, enumReason, at));
        facts.Add(MemTrimFact(loader, enumFailed, enumReason, at));
        facts.Add(FwBootMgrFact(fwOutput, fwReason, descriptions, at));
        facts.Add(AccessStatementFact(enumFailed, fwOutput is null, at));

        return facts.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    private static HardwareFact ToolsFact(Dictionary<string, List<string>> bootMgr,
        Dictionary<string, string> descriptions, bool failed, string? reason, DateTimeOffset at)
    {
        if (failed) return ReadError(ToolsKey, "工具選單順序（toolsdisplayorder）", reason, at);
        var order = BootOrderFactsService.Values(bootMgr, "toolsdisplayorder");
        string value = order.Count == 0
            ? "沒有 toolsdisplayorder 欄位——工具選單未自訂（使用 Windows 預設集合）"
            : $"{order.Count} 項依序：{string.Join("、", order.Select((id, i) => $"{i + 1}.{descriptions.GetValueOrDefault(id, id)}"))}";
        return new HardwareFact(ToolsKey, Category, "工具選單順序（toolsdisplayorder）", value, "項",
            Source, FactTrustLevel.Reported, false, at,
            order.Count > 0 ? order.Count : null, FactAvailability.Present, null);
    }

    private static HardwareFact BootSequenceFact(Dictionary<string, List<string>> bootMgr,
        Dictionary<string, string> descriptions, bool failed, string? reason, DateTimeOffset at)
    {
        if (failed) return ReadError(BootSequenceKey, "一次性開機覆寫（bootsequence）", reason, at);
        var seq = BootOrderFactsService.Values(bootMgr, "bootsequence");
        string value = seq.Count == 0
            ? "沒有 bootsequence 欄位——沒有設定「下一次開機」的一次性順序覆寫"
            : $"{seq.Count} 項（下一次開機生效，開完即失效）：{string.Join("、", seq.Select(id => descriptions.GetValueOrDefault(id, id)))}";
        return new HardwareFact(BootSequenceKey, Category, "一次性開機覆寫（bootsequence）", value, "",
            Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static HardwareFact BootmgrDeviceFact(Dictionary<string, List<string>> bootMgr,
        bool failed, string? reason, DateTimeOffset at)
    {
        if (failed) return ReadError(BootmgrDeviceKey, "開機管理器位置（device／path）", reason, at);
        var device = BootOrderFactsService.Values(bootMgr, "device");
        var path = BootOrderFactsService.Values(bootMgr, "path");
        string value = (device.Count > 0 || path.Count > 0)
            ? $"device：{(device.Count > 0 ? device[0] : "沒有欄位")}；path：{(path.Count > 0 ? path[0] : "沒有欄位")}"
            : "{bootmgr} 區塊沒有 device／path 欄位——輸出可能被截斷";
        return new HardwareFact(BootmgrDeviceKey, Category, "開機管理器位置（device／path）", value, "",
            Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static HardwareFact LoaderDeviceFact(Dictionary<string, List<string>> loader,
        bool failed, string? reason, DateTimeOffset at)
    {
        if (failed) return ReadError(LoaderDeviceKey, "系統載入器定位（device／osdevice／path）", reason, at);
        var device = BootOrderFactsService.Values(loader, "device");
        var osdevice = BootOrderFactsService.Values(loader, "osdevice");
        var path = BootOrderFactsService.Values(loader, "path");
        string value = (device.Count > 0 || osdevice.Count > 0 || path.Count > 0)
            ? $"device：{(device.Count > 0 ? device[0] : "沒有")}；osdevice：{(osdevice.Count > 0 ? osdevice[0] : "沒有")}；path：{(path.Count > 0 ? path[0] : "沒有")}"
            + "・device 與 osdevice 不一致＝系統磁碟與開機磁碟分離（BitLocker 常見形狀），如實並排不解釋成因"
            : "找不到系統載入器區塊（{bootmgr}.default 沒有指到 /enum 輸出裡的任何區塊）";
        return new HardwareFact(LoaderDeviceKey, Category, "系統載入器定位（device／osdevice／path）", value, "",
            Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static HardwareFact SingleFieldFact(string key, string name,
        Dictionary<string, List<string>> block, string field, bool failed, string? reason,
        string absentText, DateTimeOffset at)
    {
        if (failed) return ReadError(key, name, reason, at);
        var values = BootOrderFactsService.Values(block, field);
        return new HardwareFact(key, Category, name,
            values.Count > 0 ? string.Join(" ", values) : absentText, "",
            Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static HardwareFact BadMemoryFact(Dictionary<string, List<string>> loader,
        bool failed, string? reason, DateTimeOffset at)
    {
        if (failed) return ReadError(BadMemoryKey, "壞記憶體清單（badmemorylist）", reason, at);
        var list = BootOrderFactsService.Values(loader, "badmemorylist");
        var access = BootOrderFactsService.Values(loader, "badmemoryaccess");
        string value = list.Count == 0
            ? "沒有 badmemorylist 欄位——韌體/OS 沒有標記任何壞記憶體頁框；「沒有標記」不等於「沒有壞頁」"
            : $"{list.Count} 個頁框：{string.Join(" ", list)}；badmemoryaccess：{(access.Count > 0 ? access[0] : "沒有欄位")}";
        return new HardwareFact(BadMemoryKey, Category, "壞記憶體清單（badmemorylist）", value, "頁框",
            Source, FactTrustLevel.Reported, false, at, list.Count > 0 ? list.Count : null,
            FactAvailability.Present, null);
    }

    private static HardwareFact MemTrimFact(Dictionary<string, List<string>> loader,
        bool failed, string? reason, DateTimeOffset at)
    {
        if (failed) return ReadError(MemTrimKey, "記憶體遮蔽（removememory／truncatedmemory）", reason, at);
        var remove = BootOrderFactsService.Values(loader, "removememory");
        var truncate = BootOrderFactsService.Values(loader, "truncatedmemory");
        string value = (remove.Count == 0 && truncate.Count == 0)
            ? "沒有 removememory／truncatedmemory 欄位——沒有記憶體遮蔽"
            : $"removememory：{(remove.Count > 0 ? remove[0] + " MB" : "沒有")}；truncatedmemory：{(truncate.Count > 0 ? truncate[0] + " MB" : "沒有")}"
            + "・「裝的記憶體比看到的少」時，這裡是第一個該看的地方";
        return new HardwareFact(MemTrimKey, Category, "記憶體遮蔽（removememory／truncatedmemory）", value, "",
            Source, FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static HardwareFact FwBootMgrFact(string? fwOutput, string? fwReason,
        Dictionary<string, string> descriptions, DateTimeOffset at)
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
                Source, FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "bcdedit /enum firmware 的輸出裡沒有 {fwbootmgr} 區塊——非 UEFI 開機（legacy MBR）或輸出被截斷；不從 WMI 推測");
        }

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
            "① 存取法＝<c>bcdedit /enum</c> 與 <c>/enum firmware</c> 各一次（唯讀、5 秒逾時、System32 絕對路徑）；"
            + "② 兩次都是**執行當下現讀**——沒有快取，新鮮度＝這一輪收集的時刻；"
            + "③ 不讀登錄檔 hive 副本（SYSTEM\\CurrentControlSet\\Control\\hivelist 的 BCD 字樣不是現值來源）；"
            + "④ 欄位不存在＝Present 帶「沒有設定」說明，讀不到（" + (enumFailed ? "os 層失敗" : "") + (enumFailed && fwFailed ? "＋" : "") + (fwFailed ? "韌體層失敗" : "") + "）才 ReadError；"
            + "⑤ 本服務不寫 BCD——/set、/delete 不在承諾範圍",
            "", "專案界線宣告（無外部來源）", FactTrustLevel.Measured, false, at, null);

    private static HardwareFact ReadError(string key, string name, string? reason, DateTimeOffset at) =>
        new HardwareFact(key, Category, name, "", "", Source,
            FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
            string.IsNullOrWhiteSpace(reason) ? "沒有讀到 bcdedit /enum 的輸出" : reason);
}
