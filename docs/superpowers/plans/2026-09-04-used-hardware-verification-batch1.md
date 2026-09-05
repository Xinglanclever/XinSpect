# 二手驗機・矛盾稽核（第一批）實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 讓曦覽把已讀到的硬體事實互相對帳，把記憶體與儲存裝置的矛盾連同證據列出來——不給分數、不下結論。

**Architecture:** 三層，沿用專案既有的 `XxxDecoder`（純函式）／`XxxService`（讀硬體）／`XxxFactsCollector`（攤平 ViewModel）分工。事實層帶完整血統（值／單位／讀取方法／所需權限／信賴度），規則宣告它依賴哪些事實，引擎在跑規則前先查依賴，缺就回「無法判定：缺 X」。

**Tech Stack:** .NET 10 / WPF / xunit・既有 `SmbiosParser`、`NvmeLogDecoder`、`StorageSmartService`、`WinRing0Bridge`

**Spec:** `docs/superpowers/specs/2026-09-04-used-hardware-verification-design.md`

## Global Constraints

- 誠實主軸：讀不到就是「讀不到」，不以典型值或估計值代替；任何顯示的值都必須說得出讀取方法。
- 判定只有三種：`Match`／`Conflict`／`Unread`，掛全站共用的 `Severity`（`Models/HardwareModels.cs:4`：`Neutral, Good, Warning, Serious, Critical`）。
- `VerifyFinding.BenignCause` 是必填欄位（可為 `null` 但每條規則都必須明確給值），避免誘導使用者認定賣家有惡意。
- 規則不自己處理缺資料：一律由引擎依 `RequiredFacts` 判定 `Unread`。
- 解碼器的正確性一律用**本機真實位元組**（`Tests/Fixtures/*.bin`）驗證；合成資料只用來造邊界情況。
- 繁體中文介面字串，不帶政治／地域立場。
- 測試指令一律 `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q`；目前基線 1566 通過／0 失敗。
- 只讀不寫：本批不下任何會改變硬體狀態的指令。

## File Structure

| 檔案 | 責任 |
|---|---|
| `Models/VerifyModels.cs`（新） | `FactId`／`FactSource`／`FactTrust`／`VerifyFact`／`VerifyVerdict`／`VerifyFinding`／`VerifyFacts` |
| `Services/VerifyRules.cs`（新） | 規則表與引擎（純函式，零硬體相依） |
| `Services/NvmeHealth.cs`（新） | 512 位元組健康記錄 → 型別化快照（純函式） |
| `Services/AtaIdentify.cs`（新） | IDENTIFY DEVICE 256 words 讀取與解碼 |
| `Services/SmbiosFacts.cs`（新） | SMBIOS Type 16／17 → 型別化事實（純函式） |
| `Services/StorageSmartService.cs`（改） | `SmartRow` 增 `Id`／`RawValue`；NVMe 原始記錄以位元組曝露 |
| `Services/VerifyFactsCollector.cs`（新） | 把服務讀到的東西裝進 `VerifyFacts` |
| `Services/VerifyService.cs`（新） | 跑引擎、依部件分組給 UI |
| `Views/VerifyView.xaml(.cs)`（新） | 驗機稽核頁 |
| `Nav/PageRegistry.cs`（改） | 註冊新頁 |
| `Tests/VerifyModelsTests.cs`、`VerifyRulesTests.cs`、`NvmeHealthTests.cs`、`AtaIdentifyTests.cs`、`SmbiosFactsTests.cs`、`VerifyRuleTableTests.cs`（新） | 對應測試 |

---

### Task 1：事實模型

**Files:**
- Create: `Models/VerifyModels.cs`
- Test: `Tests/VerifyModelsTests.cs`

**Interfaces:**
- Produces: `FactId`（列舉）、`VerifyFact`（record）、`VerifyFacts`（事實袋，`Has(FactId)`／`Num(FactId)`／`Text(FactId)`／`Get(FactId)`）、`VerifyVerdict`、`VerifyFinding`

- [ ] **Step 1：寫失敗的測試**

```csharp
using Xunit;
namespace XinSpect.Tests;

public class VerifyModelsTests
{
    private static VerifyFact Fact(FactId id, double? num, string value = "x") => new(
        id, "標籤", value, num, "", FactSource.Smbios, "SMBIOS Type 17 +0x15",
        NeedsAdmin: false, FactTrust.FirmwareReported, DateTime.UnixEpoch);

    [Fact]
    public void 事實袋_取不到的事實一律回報缺少()
    {
        var facts = new VerifyFacts([Fact(FactId.DimmCount, 2)]);
        Assert.True(facts.Has(FactId.DimmCount));
        Assert.Equal(2, facts.Num(FactId.DimmCount));
        Assert.False(facts.Has(FactId.NvmePowerOnHours));
        Assert.Null(facts.Num(FactId.NvmePowerOnHours));   // 不得回 0——0 是一個值，缺少不是
    }

    [Fact]
    public void 事實袋_同一個FactId重複裝入時以後者為準()
    {
        var facts = new VerifyFacts([Fact(FactId.DimmCount, 2), Fact(FactId.DimmCount, 4)]);
        Assert.Equal(4, facts.Num(FactId.DimmCount));
    }
}
```

- [ ] **Step 2：跑測試確認失敗**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~VerifyModelsTests"`
Expected: 編譯失敗——找不到型別 `VerifyFacts`／`FactId`

- [ ] **Step 3：寫最小實作**

```csharp
namespace XinSpect;

public enum FactId
{
    // 記憶體
    DimmCount, DimmManufacturers, DimmPartNumbers, DimmSerials,
    DimmSpeedMts, DimmConfiguredMts, DimmSizeTotalMiB, ArrayMaxCapacityMiB, ArraySlotCount,
    // 儲存（每顆碟一組，索引由 collector 併進 Label）
    NvmePowerOnHours, NvmeDataUnitsWritten, NvmePercentageUsed,
    NvmePowerCycles, NvmeUnsafeShutdowns, NvmeCriticalWarning,
    SmartPowerOnHours, SmartHostWritesGiB, SmartPowerCycles,
    AtaRotationRate, AtaTotalLba, AtaAcsVersion, DiskClaimedCapacityGB, DiskModel,
    // 電池
    BatteryDesignCapacityMWh, BatteryFullCapacityMWh,
}

public enum FactSource { Cpuid, Msr, Smbios, AtaIdentify, SmartAttr, NvmeLog, Edid, PciConfig, Wmi, Derived }
public enum FactTrust { Native, FirmwareReported, Derived }
public enum VerifyVerdict { Match, Conflict, Unread }

public sealed record VerifyFact(
    FactId Id, string Label, string Value, double? Numeric, string Unit,
    FactSource Source, string Method, bool NeedsAdmin, FactTrust Trust, DateTime ReadAt);

public sealed record VerifyFinding(
    string Id, string Part, string Title, VerifyVerdict Verdict, Severity Severity,
    string Explanation, string? BenignCause, VerifyFact[] Evidence);

/// <summary>事實袋。取不到就回 null——呼叫端不准把「缺少」當成 0。</summary>
public sealed class VerifyFacts
{
    private readonly Dictionary<FactId, VerifyFact> _map = [];
    public VerifyFacts(IEnumerable<VerifyFact> facts) { foreach (var f in facts) _map[f.Id] = f; }
    public bool Has(FactId id) => _map.ContainsKey(id);
    public VerifyFact? Get(FactId id) => _map.TryGetValue(id, out var f) ? f : null;
    public double? Num(FactId id) => _map.TryGetValue(id, out var f) ? f.Numeric : null;
    public string? Text(FactId id) => _map.TryGetValue(id, out var f) ? f.Value : null;
}
```

- [ ] **Step 4：跑測試確認通過**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~VerifyModelsTests"`
Expected: PASS（2 個測試）

- [ ] **Step 5：提交**

```bash
git add Models/VerifyModels.cs Tests/VerifyModelsTests.cs
git commit -m "feat(verify): 事實模型與事實袋（缺少不等於 0）"
```

---

### Task 2：規則引擎 ＋ 第一條規則 R-MEM-01（記憶體混批）

**Files:**
- Create: `Services/VerifyRules.cs`
- Test: `Tests/VerifyRulesTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `VerifyFacts`／`VerifyFact`／`VerifyFinding`／`FactId`
- Produces: `VerifyRule`（record）、`VerifyRules.All`、`VerifyEngine.Run(VerifyFacts)`、`FactCatalog.Name(FactId)`、`FactCatalog.NeedsAdmin(FactId)`

- [ ] **Step 1：寫失敗的測試**

```csharp
using Xunit;
namespace XinSpect.Tests;

public class VerifyRulesTests
{
    internal static VerifyFact Text(FactId id, string value) => new(
        id, FactCatalog.Name(id), value, null, "", FactSource.Smbios,
        "SMBIOS Type 17", false, FactTrust.FirmwareReported, DateTime.UnixEpoch);

    internal static VerifyFact Num(FactId id, double n, string unit = "") => new(
        id, FactCatalog.Name(id), n.ToString("0.##"), n, unit, FactSource.Smbios,
        "SMBIOS Type 17", false, FactTrust.FirmwareReported, DateTime.UnixEpoch);

    private static VerifyFinding One(string ruleId, params VerifyFact[] facts)
        => VerifyEngine.Run(new VerifyFacts(facts)).Single(x => x.Id == ruleId);

    [Fact]
    public void 缺事實時_由引擎判為無法判定_並指出缺哪一個()
    {
        var f = One("R-MEM-01", Num(FactId.DimmCount, 2));      // 故意不給製造商與料號
        Assert.Equal(VerifyVerdict.Unread, f.Verdict);
        Assert.Equal(Severity.Neutral, f.Severity);
        Assert.Contains(FactCatalog.Name(FactId.DimmManufacturers), f.Explanation);
    }

    [Fact]
    public void R_MEM_01_同廠同料號判為相符()
    {
        var f = One("R-MEM-01", Num(FactId.DimmCount, 2),
            Text(FactId.DimmManufacturers, "Micron|Micron"),
            Text(FactId.DimmPartNumbers, "MTA8ATF1G64AZ|MTA8ATF1G64AZ"));
        Assert.Equal(VerifyVerdict.Match, f.Verdict);
    }
    [Fact]
    public void R_MEM_01_不同料號判為矛盾_且必須附上正當成因()
    {
        var f = One("R-MEM-01", Num(FactId.DimmCount, 2),
            Text(FactId.DimmManufacturers, "Micron|SK Hynix"),
            Text(FactId.DimmPartNumbers, "MTA8ATF1G64AZ|HMA81GU6JJR8N"));
        Assert.Equal(VerifyVerdict.Conflict, f.Verdict);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.False(string.IsNullOrWhiteSpace(f.BenignCause));   // 混批常常只是使用者自己加的
        Assert.Equal(2, f.Evidence.Length);
    }

    [Fact]
    public void R_MEM_01_只有一條模組時無從混批_判為相符()
    {
        var f = One("R-MEM-01", Num(FactId.DimmCount, 1),
            Text(FactId.DimmManufacturers, "Micron"),
            Text(FactId.DimmPartNumbers, "MTA8ATF1G64AZ"));
        Assert.Equal(VerifyVerdict.Match, f.Verdict);
    }
}
```

- [ ] **Step 2：跑測試確認失敗**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~VerifyRulesTests"`
Expected: 編譯失敗——找不到 `VerifyEngine`／`FactCatalog`

- [ ] **Step 3a：規則型別與事實目錄**

```csharp
namespace XinSpect;

public sealed record VerifyRule(
    string Id, string Part, string Title,
    FactId[] RequiredFacts,
    Func<VerifyFacts, VerifyFinding> Evaluate);

/// <summary>FactId 的顯示名與權限需求：缺事實時要說得出缺什麼、以及怎樣才讀得到。</summary>
public static class FactCatalog
{
    private static readonly Dictionary<FactId, (string Name, bool Admin)> Map = new()
    {
        [FactId.DimmCount] = ("記憶體模組數", false),
        [FactId.DimmManufacturers] = ("各條模組製造商", false),
        [FactId.DimmPartNumbers] = ("各條模組料號", false),
        [FactId.DimmSerials] = ("各條模組序號", false),
        [FactId.DimmSpeedMts] = ("模組標稱速度", false),
        [FactId.DimmConfiguredMts] = ("模組實際運行速度", false),
        [FactId.DimmSizeTotalMiB] = ("已安裝記憶體總量", false),
        [FactId.ArrayMaxCapacityMiB] = ("記憶體陣列宣稱上限", false),
        [FactId.ArraySlotCount] = ("記憶體插槽數", false),
        [FactId.NvmePowerOnHours] = ("NVMe 通電小時", true),
        [FactId.NvmeDataUnitsWritten] = ("NVMe 累計寫入量", true),
        [FactId.NvmePercentageUsed] = ("NVMe 已用壽命", true),
        [FactId.NvmePowerCycles] = ("NVMe 通電次數", true),
        [FactId.NvmeUnsafeShutdowns] = ("NVMe 不安全關機次數", true),
        [FactId.NvmeCriticalWarning] = ("NVMe 關鍵警告", true),
        [FactId.SmartPowerOnHours] = ("SMART 通電小時", true),
        [FactId.SmartHostWritesGiB] = ("SMART 主機寫入量", true),
        [FactId.SmartPowerCycles] = ("SMART 通電次數", true),
        [FactId.AtaRotationRate] = ("標稱旋轉速率", true),
        [FactId.AtaTotalLba] = ("可定址 LBA 總數", true),
        [FactId.AtaAcsVersion] = ("ACS 版本", true),
        [FactId.DiskClaimedCapacityGB] = ("宣稱容量", false),
        [FactId.DiskModel] = ("磁碟型號", false),
        [FactId.BatteryDesignCapacityMWh] = ("電池設計容量", false),
        [FactId.BatteryFullCapacityMWh] = ("電池滿充容量", false),
    };

    public static string Name(FactId id) => Map.TryGetValue(id, out var v) ? v.Name : id.ToString();
    public static bool NeedsAdmin(FactId id) => Map.TryGetValue(id, out var v) && v.Admin;
}
```
- [ ] **Step 3b：引擎（依賴不齊就自己判「無法判定」）**

```csharp
public static class VerifyEngine
{
    public static IReadOnlyList<VerifyFinding> Run(VerifyFacts facts)
        => VerifyRules.All.Select(r => Evaluate(r, facts)).ToList();

    internal static VerifyFinding Evaluate(VerifyRule rule, VerifyFacts facts)
    {
        var missing = rule.RequiredFacts.Where(id => !facts.Has(id)).ToArray();
        if (missing.Length == 0) return rule.Evaluate(facts);

        bool admin = missing.Any(FactCatalog.NeedsAdmin);
        string why = "無法判定：缺 " + string.Join("、", missing.Select(FactCatalog.Name))
                   + (admin ? "（需要管理員權限才讀得到）" : "");
        return new(rule.Id, rule.Part, rule.Title, VerifyVerdict.Unread, Severity.Neutral, why, null, []);
    }
}
```

- [ ] **Step 3c：規則表與 R-MEM-01**

```csharp
public static class VerifyRules
{
    public const string PartMemory = "記憶體";
    private const string T01 = "各條記憶體模組並非同批";

    public static readonly VerifyRule[] All =
    [
        new("R-MEM-01", PartMemory, T01,
            [FactId.DimmCount, FactId.DimmManufacturers, FactId.DimmPartNumbers], MixedModules),
    ];

    internal static string[] Split(string? s) =>
        (s ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static VerifyFinding MixedModules(VerifyFacts f)
    {
        var evidence = new[] { f.Get(FactId.DimmManufacturers)!, f.Get(FactId.DimmPartNumbers)! };

        if ((f.Num(FactId.DimmCount) ?? 0) < 2)
            return new("R-MEM-01", PartMemory, T01, VerifyVerdict.Match, Severity.Good,
                "只有一條模組，無從混批。", null, evidence);

        bool mixed = Split(f.Text(FactId.DimmManufacturers)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                  || Split(f.Text(FactId.DimmPartNumbers)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

        return mixed
            ? new("R-MEM-01", PartMemory, T01, VerifyVerdict.Conflict, Severity.Warning,
                "製造商或料號不一致，這幾條不是同一批出廠的模組。",
                "使用者自己後來加裝的模組也會這樣；混批不影響保固，但可能造成時序回退。", evidence)
            : new("R-MEM-01", PartMemory, T01, VerifyVerdict.Match, Severity.Good,
                "所有模組的製造商與料號一致。", null, evidence);
    }
}
```

- [ ] **Step 4：跑測試確認通過**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~VerifyRulesTests"`
Expected: PASS（4 個測試）

- [ ] **Step 5：提交**

```bash
git add Services/VerifyRules.cs Tests/VerifyRulesTests.cs
git commit -m "feat(verify): 規則引擎（依賴宣告→自動判無法判定）與 R-MEM-01"
```

---
### Task 3：其餘三條記憶體規則

**Files:**
- Modify: `Services/VerifyRules.cs`（`All` 增三列 ＋ 三個私有函式）
- Test: `Tests/VerifyRulesTests.cs`（沿用 Task 2 的 `Text`／`Num`／`One` 輔助）

**Interfaces:**
- Consumes: Task 2 的 `VerifyRules.All`、`VerifyRules.Split`
- Produces: 規則 `R-MEM-02`、`R-MEM-03`、`R-MEM-04`

- [ ] **Step 1：寫失敗的測試**

```csharp
[Theory]
[InlineData("0000000000000000|1234ABCD", VerifyVerdict.Conflict)]   // 全 0
[InlineData("FFFFFFFF|1234ABCD", VerifyVerdict.Conflict)]          // 全 F
[InlineData("1234ABCD|1234ABCD", VerifyVerdict.Conflict)]          // 兩條同序號
[InlineData("1234ABCD|5678EF01", VerifyVerdict.Match)]
public void R_MEM_02_序號異常(string serials, VerifyVerdict expected)
    => Assert.Equal(expected, One("R-MEM-02", Num(FactId.DimmCount, 2),
        Text(FactId.DimmSerials, serials)).Verdict);

[Theory]
[InlineData(3200, 3200, VerifyVerdict.Match)]
[InlineData(3200, 2133, VerifyVerdict.Conflict)]    // 沒開 XMP／被降頻
public void R_MEM_03_實際速度低於標稱(double rated, double configured, VerifyVerdict expected)
    => Assert.Equal(expected, One("R-MEM-03",
        Num(FactId.DimmSpeedMts, rated, "MT/s"),
        Num(FactId.DimmConfiguredMts, configured, "MT/s")).Verdict);

[Theory]
[InlineData(32768, 65536, 4, 2, VerifyVerdict.Match)]
[InlineData(32768, 16384, 4, 2, VerifyVerdict.Conflict)]   // 安裝量超過陣列宣稱上限
[InlineData(32768, 65536, 2, 4, VerifyVerdict.Conflict)]   // 模組數多於插槽數
public void R_MEM_04_陣列宣稱與實際對不上(double totalMiB, double maxMiB, double slots, double dimms, VerifyVerdict expected)
    => Assert.Equal(expected, One("R-MEM-04",
        Num(FactId.DimmSizeTotalMiB, totalMiB, "MiB"), Num(FactId.ArrayMaxCapacityMiB, maxMiB, "MiB"),
        Num(FactId.ArraySlotCount, slots), Num(FactId.DimmCount, dimms)).Verdict);
```

- [ ] **Step 2：跑測試確認失敗**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~VerifyRulesTests"`
Expected: FAIL——`Single()` 找不到 `R-MEM-02`／`R-MEM-03`／`R-MEM-04`

- [ ] **Step 3：寫最小實作（加進 `All` 並實作三個函式）**

```csharp
new("R-MEM-02", PartMemory, "記憶體模組序號異常",
    [FactId.DimmCount, FactId.DimmSerials], BadSerials),
new("R-MEM-03", PartMemory, "記憶體未跑在標稱速度",
    [FactId.DimmSpeedMts, FactId.DimmConfiguredMts], UnderclockedMemory),
new("R-MEM-04", PartMemory, "記憶體陣列宣稱與實際安裝對不上",
    [FactId.DimmSizeTotalMiB, FactId.ArrayMaxCapacityMiB, FactId.ArraySlotCount, FactId.DimmCount], ArrayMismatch),
```

```csharp
private static bool IsBogusSerial(string s) =>
    s.Length > 0 && (s.All(c => c == '0') || s.All(c => c is 'F' or 'f'));

private static VerifyFinding BadSerials(VerifyFacts f)
{
    const string title = "記憶體模組序號異常";
    var serials = Split(f.Text(FactId.DimmSerials));
    var ev = new[] { f.Get(FactId.DimmSerials)! };
    bool bogus = serials.Any(IsBogusSerial);
    bool dup = serials.Length > 1 &&
               serials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != serials.Length;
    return bogus || dup
        ? new("R-MEM-02", PartMemory, title, VerifyVerdict.Conflict, Severity.Serious,
            bogus ? "有模組的序號是全 0 或全 F——韌體沒燒序號，或序號被抹掉。"
                  : "兩條以上模組回報同一組序號，正常模組不會重複。",
            "極少數白牌模組出廠就沒燒序號；但翻新與貼牌模組也是這個樣子。", ev)
        : new("R-MEM-02", PartMemory, title, VerifyVerdict.Match, Severity.Good,
            "各條模組序號互異且非全 0／全 F。", null, ev);
}

private static VerifyFinding UnderclockedMemory(VerifyFacts f)
{
    const string title = "記憶體未跑在標稱速度";
    double rated = f.Num(FactId.DimmSpeedMts)!.Value, cur = f.Num(FactId.DimmConfiguredMts)!.Value;
    var ev = new[] { f.Get(FactId.DimmSpeedMts)!, f.Get(FactId.DimmConfiguredMts)! };
    return cur < rated
        ? new("R-MEM-03", PartMemory, title, VerifyVerdict.Conflict, Severity.Warning,
            $"模組標稱 {rated:0} MT/s，實際只跑 {cur:0} MT/s。",
            "多數情況是主機板沒啟用 XMP／EXPO，或處理器的記憶體控制器上限較低——不是模組本身的問題。", ev)
        : new("R-MEM-03", PartMemory, title, VerifyVerdict.Match, Severity.Good,
            "實際運行速度已達標稱值。", null, ev);
}

private static VerifyFinding ArrayMismatch(VerifyFacts f)
{
    const string title = "記憶體陣列宣稱與實際安裝對不上";
    double total = f.Num(FactId.DimmSizeTotalMiB)!.Value, max = f.Num(FactId.ArrayMaxCapacityMiB)!.Value;
    double slots = f.Num(FactId.ArraySlotCount)!.Value, dimms = f.Num(FactId.DimmCount)!.Value;
    var ev = new[] { f.Get(FactId.DimmSizeTotalMiB)!, f.Get(FactId.ArrayMaxCapacityMiB)!,
                     f.Get(FactId.ArraySlotCount)!, f.Get(FactId.DimmCount)! };
    if (total > max)
        return new("R-MEM-04", PartMemory, title, VerifyVerdict.Conflict, Severity.Warning,
            $"已安裝 {total:0} MiB，但陣列宣稱上限只有 {max:0} MiB。",
            "部分主機板的 SMBIOS 把上限寫錯，這種情況機器照樣正常運作。", ev);
    if (dimms > slots)
        return new("R-MEM-04", PartMemory, title, VerifyVerdict.Conflict, Severity.Warning,
            $"回報 {dimms:0} 條模組，但陣列只宣告 {slots:0} 個插槽。",
            "SMBIOS 表寫錯也會這樣；但也可能是有一條沒被正確列舉。", ev);
    return new("R-MEM-04", PartMemory, title, VerifyVerdict.Match, Severity.Good,
        "安裝總量與插槽數都在陣列宣告範圍內。", null, ev);
}
```

- [ ] **Step 4：跑測試確認通過**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~VerifyRulesTests"`
Expected: PASS（4 ＋ 11 個測試）

- [ ] **Step 5：提交**

```bash
git add Services/VerifyRules.cs Tests/VerifyRulesTests.cs
git commit -m "feat(verify): 記憶體另外三條規則（序號異常／未達標稱速度／陣列對不上）"
```

---
### Task 4：SMBIOS 型別化事實讀取器 ＋ 真實位元組基準

**Files:**
- Create: `Services/SmbiosFacts.cs`、`Tests/Fixtures/smbios-real.bin`
- Test: `Tests/SmbiosFactsTests.cs`

**Interfaces:**
- Consumes: 既有 `SmbiosParser.Parse(byte[]) → List<SmbiosStruct>`、`SmbiosStruct.WordAt/DwordAt/ByteAt/GetString`
- Produces: `SmbiosFacts.From(IEnumerable<SmbiosStruct>, DateTime) → List<VerifyFact>`

- [ ] **Step 1：把本機的真實表傾印成基準檔**

在 `Tests` 專案裡跑一次性的傾印（跑完把檔案入庫，程式碼不必留）：

```csharp
// 一次性：dotnet run 或以測試臨時執行皆可
uint size = GetSystemFirmwareTable(0x52534D42, 0, null, 0);
var buf = new byte[size];
GetSystemFirmwareTable(0x52534D42, 0, buf, size);
File.WriteAllBytes(@"Tests\Fixtures\smbios-real.bin", buf);
```

入庫前把序號字串就地遮成同長度的 `X`（**長度與位移必須保持原樣**，否則就失去驗證位移的意義），
並在 `Tests/Fixtures/README.md` 註明機型與傾印日期。

- [ ] **Step 2：寫失敗的測試**

```csharp
public class SmbiosFactsTests
{
    private static List<VerifyFact> Real()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "smbios-real.bin"));
        return SmbiosFacts.From(SmbiosParser.Parse(bytes), DateTime.UnixEpoch);
    }

    [Fact]
    public void 真實表_解得出模組數與標稱速度()
    {
        var facts = new VerifyFacts(Real());
        Assert.True(facts.Num(FactId.DimmCount) >= 1);
        Assert.True(facts.Num(FactId.DimmSpeedMts) >= 800);        // 任何 DDR 世代都高於此
        Assert.Equal(FactSource.Smbios, facts.Get(FactId.DimmCount)!.Source);
        Assert.Contains("Type 17", facts.Get(FactId.DimmSpeedMts)!.Method);
    }

    [Fact]
    public void 每個事實都必須帶讀取方法與信賴度()
        => Assert.All(Real(), f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Method));
            Assert.Equal(FactTrust.FirmwareReported, f.Trust);      // SMBIOS 是韌體轉述，不是原生讀取
        });
}
```

- [ ] **Step 3：跑測試確認失敗**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SmbiosFactsTests"`
Expected: 編譯失敗——找不到 `SmbiosFacts`

- [ ] **Step 4：寫最小實作**

```csharp
namespace XinSpect;

/// <summary>SMBIOS → 型別化事實。位移全部照規格寫死並在註解標明，供測試核對。</summary>
public static class SmbiosFacts
{
    public static List<VerifyFact> From(IEnumerable<SmbiosStruct> structs, DateTime now)
    {
        var list = new List<VerifyFact>();
        var dimms = structs.Where(s => s.Type == 17 && s.Length > 0x21).ToList();
        var array = structs.FirstOrDefault(s => s.Type == 16 && s.Length > 0x0E);

        var installed = dimms.Where(d => SizeMiB(d) > 0).ToList();
        if (installed.Count > 0)
        {
            list.Add(F(FactId.DimmCount, installed.Count, "", "SMBIOS Type 17 逐筆計數", now));
            list.Add(T(FactId.DimmManufacturers, Join(installed, d => d.GetString(d.ByteAt(0x17))),
                "SMBIOS Type 17 +0x17", now));
            list.Add(T(FactId.DimmSerials, Join(installed, d => d.GetString(d.ByteAt(0x18))),
                "SMBIOS Type 17 +0x18", now));
            list.Add(T(FactId.DimmPartNumbers, Join(installed, d => d.GetString(d.ByteAt(0x1A))),
                "SMBIOS Type 17 +0x1A", now));
            list.Add(F(FactId.DimmSizeTotalMiB, installed.Sum(SizeMiB), "MiB", "SMBIOS Type 17 +0x0C／+0x1C", now));

            ushort rated = installed.Max(d => d.WordAt(0x15));
            ushort cur = installed.Max(d => d.WordAt(0x20));
            if (rated > 0) list.Add(F(FactId.DimmSpeedMts, rated, "MT/s", "SMBIOS Type 17 +0x15", now));
            if (cur > 0) list.Add(F(FactId.DimmConfiguredMts, cur, "MT/s", "SMBIOS Type 17 +0x20", now));
        }
        if (array is not null)
        {
            list.Add(F(FactId.ArrayMaxCapacityMiB, array.DwordAt(0x07) / 1024.0, "MiB",
                "SMBIOS Type 16 +0x07（KB）", now));
            list.Add(F(FactId.ArraySlotCount, array.WordAt(0x0D), "", "SMBIOS Type 16 +0x0D", now));
        }
        return list;
    }

    // Type 17 +0x0C：bit15 0＝MB、1＝KB；0x7FFF 代表改看 +0x1C 的擴充容量（MB）
    private static double SizeMiB(SmbiosStruct d)
    {
        ushort raw = d.WordAt(0x0C);
        if (raw == 0 || raw == 0xFFFF) return 0;
        if (raw == 0x7FFF) return d.Length > 0x1F ? d.DwordAt(0x1C) : 0;
        return (raw & 0x8000) != 0 ? (raw & 0x7FFF) / 1024.0 : raw;
    }

    private static string Join(List<SmbiosStruct> d, Func<SmbiosStruct, string?> pick)
        => string.Join("|", d.Select(x => (pick(x) ?? "").Trim() is { Length: > 0 } s ? s : "—"));

    private static VerifyFact F(FactId id, double n, string unit, string method, DateTime now)
        => new(id, FactCatalog.Name(id), n.ToString("0.##") + (unit.Length > 0 ? " " + unit : ""),
               n, unit, FactSource.Smbios, method, false, FactTrust.FirmwareReported, now);

    private static VerifyFact T(FactId id, string value, string method, DateTime now)
        => new(id, FactCatalog.Name(id), value, null, "", FactSource.Smbios, method,
               false, FactTrust.FirmwareReported, now);
}
```

- [ ] **Step 5：跑測試確認通過，然後提交**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SmbiosFactsTests"`

```bash
git add Services/SmbiosFacts.cs Tests/SmbiosFactsTests.cs Tests/Fixtures/
git commit -m "feat(verify): SMBIOS 型別化事實讀取器，以本機真實位元組為測試基準"
```

> `Tests/Fixtures/*.bin` 要在 `Tests/XinSpect.Tests.csproj` 加
> `<Content Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" />`，否則測試在輸出目錄找不到檔案。

---
### Task 5：磁碟讀取看門狗（先做這個，才有辦法安全地傾印基準檔）

**Files:**
- Create: `Services/DiskIo.cs`
- Modify: `Services/StorageSmartService.cs`（`TryReadNvmeLog`／`TryGetBusType`／`TryReadNvmeIdentify`／`TryReadNvmeFeature`／`TryReadPowerOnHours` 全部改走看門狗）
- Modify: `Services/MachineAgeService.cs`（逐碟掃描套同一個看門狗）
- Test: `Tests/DiskIoTests.cs`

**Interfaces:**
- Produces: `DiskIo.Guarded<T>(Func<T?>, int timeoutMs) → T?`、`DiskIo.DefaultTimeoutMs`

**為什麼有這個任務**：2026-09-04 傾印基準檔時，一支列舉 `PhysicalDrive 0–7` 的程式卡在某顆碟的
IOCTL 裡沒回來，那個程序連提權後的 `taskkill /F` 都殺不掉（核心模式不可中斷 I/O），並握著
測試組件的檔案鎖直到重開機。同一組 IOCTL 就是現有儲存頁在用的——**這是產品級風險，不是測試意外**。

- [ ] **Step 1：寫失敗的測試（完全不碰真實磁碟）**

```csharp
using System.Diagnostics;
using Xunit;

namespace XinSpect.Tests;

public class DiskIoTests
{
    [Fact]
    public void 正常完成時原樣回傳()
        => Assert.Equal("ok", DiskIo.Guarded(() => "ok", 1000));

    [Fact]
    public void 逾時就放棄_並且在時限內返回()
    {
        var sw = Stopwatch.StartNew();
        var result = DiskIo.Guarded<string>(() => { Thread.Sleep(5000); return "太慢了"; }, 200);
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"應在時限內返回，實際 {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void 讀取拋例外時回null_不得往外炸()
        => Assert.Null(DiskIo.Guarded<string>(() => throw new IOException("裝置沒回應"), 1000));

    [Fact]
    public void 逾時後那條工作自己完成也不得造成未觀察例外()
    {
        DiskIo.Guarded<string>(() => { Thread.Sleep(300); throw new IOException("遲到又失敗"); }, 50);
        Thread.Sleep(600);
        GC.Collect();
        GC.WaitForPendingFinalizers();   // 若未觀察例外沒被吃掉，這裡會把行程帶走
    }
}
```

- [ ] **Step 2：跑測試確認失敗**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~DiskIoTests"`
Expected: 編譯失敗——找不到 `DiskIo`

- [ ] **Step 3：寫最小實作**

```csharp
namespace XinSpect;

/// <summary>
/// 磁碟 I/O 看門狗：把可能卡在核心裡的同步 IOCTL 圈起來，逾時就放棄。
/// </summary>
/// <remarks>
/// 卡住的 IOCTL 是**不可中斷**的核心模式等待：.NET 沒有辦法中止那條執行緒，
/// 提權後的 <c>taskkill /F</c> 也殺不掉整個程序。所以這裡的做法是「不等它」——
/// 逾時就把那條執行緒留在原地自己爛掉，呼叫端拿到 null 當作「讀不到」。
/// 洩掉一條執行緒，換整個程式不被凍住，這個交換是划算的。
/// <para>
/// 逾時後**不重試、不換路徑**：卡住的那條 I/O 還在核心裡排隊，再發一次只是多卡一條。
/// </para>
/// </remarks>
public static class DiskIo
{
    /// <summary>單顆碟的讀取上限。正常的 IOCTL 是毫秒級；到秒級就已經不對了。</summary>
    public const int DefaultTimeoutMs = 3000;

    public static T? Guarded<T>(Func<T?> read, int timeoutMs = DefaultTimeoutMs) where T : class
    {
        var task = Task.Run(read);
        try
        {
            if (task.Wait(timeoutMs)) return task.Result;
        }
        catch
        {
            return null;                       // 讀取自己拋例外
        }

        // 逾時：把遲到的結果與例外都吃掉，免得日後變成未觀察例外把行程帶走
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return null;
    }
}
```

- [ ] **Step 4：跑測試確認通過**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~DiskIoTests"`
Expected: PASS（4 個測試）

- [ ] **Step 5：把現有的磁碟讀取路徑全部包起來**

`StorageSmartService` 裡每一個 `OpenDrive` → `DeviceIoControl` 的公開入口，
以及 `MachineAgeService` 的逐碟掃描，一律改成 `DiskIo.Guarded(() => …)`。
逐碟獨立：一顆碟逾時不得影響其餘碟的結果，且訊息要指出是哪一顆碟沒回應。

- [ ] **Step 6：跑全套測試，然後提交**

```bash
git add Services/DiskIo.cs Services/StorageSmartService.cs Services/MachineAgeService.cs Tests/DiskIoTests.cs
git commit -m "fix(storage): 磁碟 IOCTL 一律走逾時看門狗，避免卡死整個程序"
```

---

### Task 6：NVMe 健康記錄 → 型別化快照

**Files:**
- Create: `Services/NvmeHealth.cs`、`Tests/Fixtures/nvme-health-real.bin`（512 位元組）
- Modify: `Services/NvmeLogDecoder.cs`（把 128 位元計數器的讀取改成公開純函式）
- Modify: `Services/StorageSmartService.cs:289-330`（顯示列改用同一個快照，避免兩套位移）
- Test: `Tests/NvmeHealthTests.cs`

**Interfaces:**
- Consumes: 既有 `NvmeLogDecoder.Off*` 常數（`OffPowerOnHours = 0x80` 等，1.9.1-B1 已修正）
- Produces: `NvmeLogDecoder.Counter(ReadOnlySpan<byte>, int) → ulong`、`NvmeHealthSnapshot`、`NvmeHealth.Decode(ReadOnlySpan<byte>) → NvmeHealthSnapshot?`

- [ ] **Step 1：傾印本機真實記錄當基準**

在儲存頁按一次讀取時，把 `StorageSmartService` 拿到的 512 位元組 log 寫成
`Tests/Fixtures/nvme-health-real.bin`（臨時加一行 `File.WriteAllBytes`，入庫後移除）。
這個緩衝區沒有序號欄位，可原樣入庫。

- [ ] **Step 2：寫失敗的測試**

```csharp
public class NvmeHealthTests
{
    private static byte[] RealLog() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nvme-health-real.bin"));

    [Fact]
    public void 真實記錄_解出的數值必須落在物理上合理的範圍()
    {
        var h = NvmeHealth.Decode(RealLog())!.Value;
        Assert.InRange(h.PercentageUsed, 0, 255);
        Assert.True(h.PowerOnHours < 200_000);          // 位移錯位時這個值會爆成天文數字
        Assert.True(h.PowerCycles < 1_000_000);
        Assert.True(h.DataUnitsWritten < (ulong)1e15);
    }

    [Fact]
    public void 長度不足的緩衝區回null_不得回一組零()
        => Assert.Null(NvmeHealth.Decode(new byte[16]));

    [Fact]
    public void 位移以規格值為準_128位元計數器只取低64位元()
    {
        var log = new byte[512];
        log[NvmeLogDecoder.OffPowerOnHours] = 0x2A;       // 低位元組 42
        log[NvmeLogDecoder.OffPowerOnHours + 8] = 0x01;   // 高 64 位元有值也不該影響低 64
        Assert.Equal(42u, NvmeHealth.Decode(log)!.Value.PowerOnHours);
    }
}
```

- [ ] **Step 3：跑測試確認失敗**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~NvmeHealthTests"`
Expected: 編譯失敗——找不到 `NvmeHealth`／`NvmeLogDecoder.Counter`

- [ ] **Step 4：寫最小實作**

```csharp
// NvmeLogDecoder.cs：把原本藏在 StorageSmartService 裡的區域函式提出來，兩邊共用同一份位移邏輯
public static ulong Counter(ReadOnlySpan<byte> log, int offset)
    => offset + 8 <= log.Length ? BitConverter.ToUInt64(log.Slice(offset, 8)) : 0;
```

```csharp
namespace XinSpect;

public readonly record struct NvmeHealthSnapshot(
    byte CriticalWarning, int PercentageUsed, ulong DataUnitsRead, ulong DataUnitsWritten,
    ulong PowerCycles, ulong PowerOnHours, ulong UnsafeShutdowns);

/// <summary>NVMe SMART／Health Information（Log Page 0x02）→ 型別化快照。長度不足回 null。</summary>
public static class NvmeHealth
{
    public const int LogSize = 512;

    public static NvmeHealthSnapshot? Decode(ReadOnlySpan<byte> log)
    {
        if (log.Length < LogSize) return null;
        return new(
            log[NvmeLogDecoder.OffCriticalWarning],
            log[NvmeLogDecoder.OffPercentageUsed],
            NvmeLogDecoder.Counter(log, NvmeLogDecoder.OffDataUnitsRead),
            NvmeLogDecoder.Counter(log, NvmeLogDecoder.OffDataUnitsWritten),
            NvmeLogDecoder.Counter(log, NvmeLogDecoder.OffPowerCycles),
            NvmeLogDecoder.Counter(log, NvmeLogDecoder.OffPowerOnHours),
            NvmeLogDecoder.Counter(log, NvmeLogDecoder.OffUnsafeShutdowns));
    }

    /// <summary>Data Units 換算 GiB：一個單位＝1000 個 512B 磁區。</summary>
    public static double DataUnitsToGiB(ulong units) => units * 1000.0 * 512 / (1024 * 1024 * 1024);
}
```

- [ ] **Step 5：把顯示列改用同一個快照**

`StorageSmartService` 建 NVMe 顯示列的那段（約 289–330 行）改成先 `NvmeHealth.Decode(log)`，
再由快照取值產生文字。**同一份位移只能存在一處**——1.9.1-B1 的位移錯誤就是兩套位移各自漂移造成的。

- [ ] **Step 6：跑全套測試確認沒回歸，然後提交**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q`
Expected: 1566 ＋ 新增測試全通過

```bash
git add Services/NvmeHealth.cs Services/NvmeLogDecoder.cs Services/StorageSmartService.cs Tests/NvmeHealthTests.cs Tests/Fixtures/
git commit -m "feat(verify): NVMe 健康記錄型別化快照，顯示與規則共用同一份位移"
```

---

### Task 7：SMART 屬性型別化曝露

**Files:**
- Modify: `Services/StorageSmartService.cs`（`SmartRow` 增 `Id`／`RawValue`；新增 `TryReadNvmeLog`／`TryReadAtaAttributes` 型別化入口）
- Test: `Tests/SmartRowTests.cs`

**Interfaces:**
- Produces: `SmartRow.Id`（`byte`）、`SmartRow.RawValue`（`ulong?`）、
  `StorageSmartService.TryReadNvmeLog(int index) → byte[]?`、
  `StorageSmartService.TryReadAtaAttributes(int index) → IReadOnlyList<SmartRow>?`

- [ ] **Step 1：寫失敗的測試**

```csharp
public class SmartRowTests
{
    [Fact]
    public void 屬性列必須同時帶屬性編號與可算術的原始值()
    {
        var row = new SmartRow(0x09, "通電時間", "100", "100", "1,234", 1234);
        Assert.Equal(0x09, row.Id);
        Assert.Equal(1234u, row.RawValue);
        Assert.Equal("1,234", row.RawText);      // 顯示字串與數值分家
    }

    [Fact]
    public void 讀不到原始值時RawValue為null而非零()
        => Assert.Null(new SmartRow(0xC5, "重新配置磁區", "100", "100", "—", null).RawValue);
}
```

- [ ] **Step 2：跑測試確認失敗**（建構子簽章不符）

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~SmartRowTests"`

- [ ] **Step 3：實作**

```csharp
// SmartRow：加上屬性編號與可算術的原始值。RawText 留著給顯示用，兩者不得互相推導。
public sealed class SmartRow
{
    public SmartRow(byte id, string name, string valueText, string worstText, string rawText, ulong? rawValue)
    { Id = id; Name = name; ValueText = valueText; WorstText = worstText; RawText = rawText; RawValue = rawValue; }

    public byte Id { get; }              // ATA 屬性編號（0x09 通電小時…）；NVMe 路徑填 0x00
    public string Name { get; }
    public string ValueText { get; }
    public string WorstText { get; }
    public string RawText { get; }        // 顯示字串（含千分位）
    public ulong? RawValue { get; }       // 規則用；解析不出來時為 null，不得填 0
}
```

同時更新既有所有 `new SmartRow(...)` 呼叫點，並新增兩個型別化入口：

```csharp
/// <summary>回原始 512 位元組健康記錄；失敗回 null（不回全零緩衝區）。</summary>
public static byte[]? TryReadNvmeLog(int physicalDriveIndex) { /* 抽自既有讀取邏輯 */ }

/// <summary>回帶編號與原始值的 ATA 屬性列；失敗回 null（不回空集合——空集合會被誤讀成「沒有屬性」）。</summary>
public static IReadOnlyList<SmartRow>? TryReadAtaAttributes(int physicalDriveIndex) { /* 同上 */ }
```


- [ ] **Step 4：跑全套測試，然後提交**

```bash
git add Services/StorageSmartService.cs Tests/SmartRowTests.cs
git commit -m "refactor(smart): 屬性列帶編號與可算術原始值，讀取失敗回 null"
```

---
### Task 8：ATA IDENTIFY DEVICE 讀取與解碼

**Files:**
- Create: `Services/AtaIdentify.cs`、`Tests/Fixtures/ata-identify-real.bin`（512 位元組）
- Test: `Tests/AtaIdentifyTests.cs`

**Interfaces:**
- Consumes: `StorageSmartService` 既有的 `CreateFile`／`DeviceIoControl`／`IoctlSmartRcvDriveData = 0x7C82C` 樣式
- Produces: `AtaIdentifyInfo`（record）、`AtaIdentify.Decode(ReadOnlySpan<byte>) → AtaIdentifyInfo?`、`AtaIdentify.TryRead(int physicalDriveIndex) → byte[]?`

- [ ] **Step 1：寫失敗的測試**

```csharp
public class AtaIdentifyTests
{
    private static byte[] Real() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ata-identify-real.bin"));

    [Fact]
    public void 真實資料_型號字串必須是可讀文字且位元序已還原()
    {
        var info = AtaIdentify.Decode(Real())!;
        Assert.False(string.IsNullOrWhiteSpace(info.Model));
        Assert.DoesNotContain('\0', info.Model);
        Assert.True(info.TotalLba > 0);
    }

    [Fact]
    public void 全零緩衝區必須判為讀不到_不得解成旋轉速率0的SSD()
        => Assert.Null(AtaIdentify.Decode(new byte[512]));

    [Theory]
    [InlineData(1, true)]        // word 217 == 1 → 非機械
    [InlineData(7200, false)]
    public void 旋轉速率決定是否為固態(int rate, bool expectedSolid)
    {
        var buf = new byte[512];
        buf[27 * 2] = (byte)'X'; buf[27 * 2 + 1] = (byte)'X';        // 讓型號非空，否則整筆判為讀不到
        BitConverter.GetBytes((ushort)rate).CopyTo(buf, 217 * 2);
        BitConverter.GetBytes((ulong)1000).CopyTo(buf, 100 * 2);
        Assert.Equal(expectedSolid, AtaIdentify.Decode(buf)!.IsSolidState);
    }
}
```

- [ ] **Step 2：跑測試確認失敗**

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~AtaIdentifyTests"`

- [ ] **Step 3：寫解碼器（純函式，先做這半，才測得動）**

```csharp
namespace XinSpect;

public sealed record AtaIdentifyInfo(
    string Model, string Firmware, string Serial,
    ulong TotalLba, int RotationRate, int AcsMajorVersion)
{
    public bool IsSolidState => RotationRate == 1;
    public bool IsMechanical => RotationRate is >= 1000 and <= 0xFFFE;
    public double CapacityGB => TotalLba * 512.0 / 1_000_000_000;
}

/// <summary>IDENTIFY DEVICE 的 256 個 word。字串欄位在 ATA 規格裡是位元組交換過的。</summary>
public static class AtaIdentify
{
    public static AtaIdentifyInfo? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length < 512) return null;
        string model = Str(d, 27, 20);
        if (model.Length == 0) return null;          // 全零／不轉送 ATA 指令的外接盒 → 讀不到

        ulong lba48 = BitConverter.ToUInt64(d.Slice(100 * 2, 8)) & 0x0000_FFFF_FFFF_FFFFul;
        uint lba28 = BitConverter.ToUInt32(d.Slice(60 * 2, 4));
        return new(model, Str(d, 23, 8), Str(d, 10, 20),
            lba48 > 0 ? lba48 : lba28,
            BitConverter.ToUInt16(d.Slice(217 * 2, 2)),
            BitConverter.ToUInt16(d.Slice(80 * 2, 2)));
    }

    private static string Str(ReadOnlySpan<byte> d, int firstWord, int words)
    {
        var chars = new char[words * 2];
        for (int i = 0; i < words; i++)
        {
            chars[i * 2] = (char)d[(firstWord + i) * 2 + 1];      // ATA 字串以 word 為單位交換位元組
            chars[i * 2 + 1] = (char)d[(firstWord + i) * 2];
        }
        return new string(chars).Trim('\0', ' ');
    }
}
```

- [ ] **Step 4：寫讀取端（`TryRead`）**

以 `\\.\PhysicalDrive{index}` 開檔，`DeviceIoControl(0x7C82C)` 帶
`SENDCMDINPARAMS { irDriveRegs.bCommandReg = 0xEC (IDENTIFY DEVICE), cBufferSize = 512 }`，
回傳 `SENDCMDOUTPARAMS.bBuffer` 的 512 位元組。**任何失敗一律回 `null`**，
不回全零緩衝區——全零會被解成「旋轉速率 0」而變成假結論。

- [ ] **Step 5：跑測試確認通過，然後提交**

```bash
git add Services/AtaIdentify.cs Tests/AtaIdentifyTests.cs Tests/Fixtures/
git commit -m "feat(verify): ATA IDENTIFY 讀取與解碼（全零視為讀不到）"
```

---
### Task 9：儲存六條規則

**Files:**
- Modify: `Services/VerifyRules.cs`
- Test: `Tests/VerifyRulesTests.cs`

**Interfaces:**
- Consumes: Task 2 的引擎、Task 5 的 `NvmeHealth.DataUnitsToGiB`
- Produces: 規則 `R-SSD-01` 至 `R-SSD-06`、公開門檻常數 `VerifyThresholds`

- [ ] **Step 1：寫失敗的測試**

```csharp
[Theory]
[InlineData(5000, 100, VerifyVerdict.Match)]        // 50 GiB/h：高但可能
[InlineData(80000, 100, VerifyVerdict.Conflict)]    // 800 GiB/h：物理上說不通
public void R_SSD_01_通電小時與寫入量對帳(double writtenGiB, double hours, VerifyVerdict expected)
    => Assert.Equal(expected, One("R-SSD-01",
        Num(FactId.NvmeDataUnitsWritten, writtenGiB, "GiB"),
        Num(FactId.NvmePowerOnHours, hours, "小時")).Verdict);

[Theory]
[InlineData(0, 200_000, VerifyVerdict.Conflict)]    // 寫了 200 TiB 而壽命still 0%
[InlineData(3, 200_000, VerifyVerdict.Match)]
public void R_SSD_02_已用壽命與寫入量對帳(double percentUsed, double writtenGiB, VerifyVerdict expected)
    => Assert.Equal(expected, One("R-SSD-02",
        Num(FactId.NvmePercentageUsed, percentUsed, "%"),
        Num(FactId.NvmeDataUnitsWritten, writtenGiB, "GiB")).Verdict);

[Theory]
[InlineData(50, 100, VerifyVerdict.Match)]
[InlineData(150, 100, VerifyVerdict.Conflict)]      // 不安全關機不可能多於通電次數
public void R_SSD_03_不安全關機不得多於通電次數(double unsafeCount, double cycles, VerifyVerdict expected)
    => Assert.Equal(expected, One("R-SSD-03",
        Num(FactId.NvmeUnsafeShutdowns, unsafeCount),
        Num(FactId.NvmePowerCycles, cycles)).Verdict);

[Theory]
[InlineData(0, VerifyVerdict.Match)]
[InlineData(0b0000_1000, VerifyVerdict.Conflict)]   // bit3：介質已進入唯讀
public void R_SSD_04_關鍵警告位元(double flags, VerifyVerdict expected)
    => Assert.Equal(expected, One("R-SSD-04", Num(FactId.NvmeCriticalWarning, flags)).Verdict);

[Theory]
[InlineData(1000, 1_953_525_168, VerifyVerdict.Match)]      // 1TB 碟的正常 LBA 數
[InlineData(2000, 1_953_525_168, VerifyVerdict.Conflict)]   // 宣稱 2TB 但只定址得到 1TB
public void R_SSD_05_宣稱容量與可定址容量(double claimedGB, double totalLba, VerifyVerdict expected)
    => Assert.Equal(expected, One("R-SSD-05",
        Num(FactId.DiskClaimedCapacityGB, claimedGB, "GB"), Num(FactId.AtaTotalLba, totalLba)).Verdict);

[Theory]
[InlineData(1, 0, VerifyVerdict.Match)]          // SSD 且無機械屬性
[InlineData(1, 1, VerifyVerdict.Conflict)]       // 自稱 SSD 卻有起轉時間屬性
[InlineData(7200, 1, VerifyVerdict.Match)]
public void R_SSD_06_轉速宣稱與屬性集(double rate, double spinUpPresent, VerifyVerdict expected)
    => Assert.Equal(expected, One("R-SSD-06",
        Num(FactId.AtaRotationRate, rate), Num(FactId.SmartSpinUpPresent, spinUpPresent)).Verdict);
```

- [ ] **Step 2：跑測試確認失敗**（找不到六條規則，以及新的 `FactId.SmartSpinUpPresent`）

- [ ] **Step 3：加事實與門檻常數**

`FactId` 增 `SmartSpinUpPresent`（值 1／0：SMART 是否存在 0x03 起轉時間屬性），
並在 `FactCatalog.Map` 補 `("是否存在機械屬性", true)`。

```csharp
public static class VerifyThresholds
{
    /// 全生命週期的平均寫入速率上限。120 GiB/h ≈ 34 MB/s 連續不斷寫整個通電期間——
    /// 消費級用途幾乎到不了，故超過即視為「通電小時被歸零」的訊號。取保守值，寧可漏判。
    public const double MaxPlausibleGiBPerHour = 120;
    /// 已用壽命仍為 0% 卻已寫入這麼多，代表壽命計數被歸零（TLC 消費級碟的 TBW 多在 600 TB 以下）。
    public const double ZeroWearImplausibleGiB = 100_000;
    /// 容量誤差容許值：宣稱與可定址相差超過此比例即為矛盾（保留 OP 與單位換算差異）。
    public const double CapacityTolerance = 0.10;
    /// 電池衰退門檻。
    public const double BatteryWornRatio = 0.80, BatteryBadlyWornRatio = 0.50;
}
```

- [ ] **Step 4：實作六條規則**

六條都遵循同一形狀：取兩個事實 → 比對 → 回 `Match`／`Conflict`，`Conflict` 必附 `BenignCause`。
關鍵判斷式：

```csharp
// R-SSD-01
double rate = written / Math.Max(hours, 1);
conflict = rate > VerifyThresholds.MaxPlausibleGiBPerHour;
benign = "曾長期用於影音錄製、虛擬機主機或監控錄影的碟會有很高的平均寫入速率。";

// R-SSD-02
conflict = percentUsed == 0 && written > VerifyThresholds.ZeroWearImplausibleGiB;
benign = "少數企業級碟的壽命計數解析度較粗，長時間仍顯示 0%。";

// R-SSD-03（物理上的不可能，故 Severity.Serious）
conflict = unsafeCount > cycles;
benign = null;   // 這一條沒有正當成因：不安全關機是通電次數的子集

// R-SSD-04
conflict = flags != 0;   // 逐位元說明沿用既有 NvmeLogDecoder.CriticalWarnings(byte)
benign = "剛經歷異常斷電或溫度過高的碟會亮起警告，冷卻後未必仍成立。";

// R-SSD-05
conflict = Math.Abs(claimedGB - lba * 512.0 / 1e9) / claimedGB > VerifyThresholds.CapacityTolerance;
benign = "廠商標示的 TB 與作業系統的 TiB 換算差約 7%，已納入容許值；仍超出者才列為矛盾。";

// R-SSD-06
conflict = (rate == 1 && spinUpPresent > 0) || (rate >= 1000 && spinUpPresent == 0);
benign = "部分 USB 外接盒與 RAID 控制器會轉述錯誤的旋轉速率。";
```

- [ ] **Step 5：跑測試確認通過，然後提交**

```bash
git add Services/VerifyRules.cs Models/VerifyModels.cs Tests/VerifyRulesTests.cs
git commit -m "feat(verify): 儲存六條規則與門檻常數（翻新碟對帳）"
```

---

### Task 10：電池規則 R-BAT-01

**Files:** Modify `Services/VerifyRules.cs`；Test `Tests/VerifyRulesTests.cs`

- [ ] **Step 1：寫失敗的測試**

```csharp
[Theory]
[InlineData(50000, 48000, VerifyVerdict.Match)]        // 96%
[InlineData(50000, 35000, VerifyVerdict.Conflict)]     // 70%：明顯衰退
[InlineData(50000, 0, VerifyVerdict.Unread)]           // 滿充讀不到（0 不是有效值）
public void R_BAT_01_電池衰退(double design, double full, VerifyVerdict expected)
{
    var facts = full > 0
        ? new VerifyFacts([Num(FactId.BatteryDesignCapacityMWh, design, "mWh"),
                           Num(FactId.BatteryFullCapacityMWh, full, "mWh")])
        : new VerifyFacts([Num(FactId.BatteryDesignCapacityMWh, design, "mWh")]);
    Assert.Equal(expected, VerifyEngine.Run(facts).Single(x => x.Id == "R-BAT-01").Verdict);
}
```

- [ ] **Step 2：實作**

```csharp
new("R-BAT-01", PartBattery, "電池容量已衰退",
    [FactId.BatteryDesignCapacityMWh, FactId.BatteryFullCapacityMWh], BatteryWorn),
```

```csharp
public const string PartBattery = "電池";

private static VerifyFinding BatteryWorn(VerifyFacts f)
{
    const string title = "電池容量已衰退";
    double design = f.Num(FactId.BatteryDesignCapacityMWh)!.Value;
    double full = f.Num(FactId.BatteryFullCapacityMWh)!.Value;
    var ev = new[] { f.Get(FactId.BatteryDesignCapacityMWh)!, f.Get(FactId.BatteryFullCapacityMWh)! };
    if (design <= 0)
        return new("R-BAT-01", PartBattery, title, VerifyVerdict.Unread, Severity.Neutral,
            "設計容量回報為 0，無法計算衰退比例。", null, ev);

    double ratio = full / design;
    if (ratio >= VerifyThresholds.BatteryWornRatio)
        return new("R-BAT-01", PartBattery, title, VerifyVerdict.Match, Severity.Good,
            $"滿充容量為設計容量的 {ratio:P0}。", null, ev);

    return new("R-BAT-01", PartBattery, title, VerifyVerdict.Conflict,
        ratio < VerifyThresholds.BatteryBadlyWornRatio ? Severity.Serious : Severity.Warning,
        $"滿充容量只有設計容量的 {ratio:P0}。",
        "長期插電使用的機器電池衰退屬正常老化，與二手翻新無關。", ev);
}
```

- [ ] **Step 3–4：跑測試、提交**


```bash
git add Services/VerifyRules.cs Tests/VerifyRulesTests.cs
git commit -m "feat(verify): R-BAT-01 電池衰退"
```

---
### Task 11：事實收集器

**Files:**
- Create: `Services/VerifyFactsCollector.cs`
- Modify: `Services/SmbiosService.cs`（新增 `public IReadOnlyList<SmbiosStruct> Structs { get; }`——目前只曝露解讀後的顯示列，`SmbiosFacts.From` 需要原始結構）
- Test: `Tests/VerifyFactsCollectorTests.cs`

**Interfaces:**
- Produces: `VerifyFactsCollector.Build(BuildInput) → VerifyFacts`（純函式，可測）、
  `VerifyFactsCollector.Collect(MainViewModel, int diskIndex) → VerifyFacts`（讀硬體的薄殼）
- `BuildInput` 欄位：`IReadOnlyList<SmbiosStruct>? Smbios`、`byte[]? NvmeLog`、`byte[]? AtaIdentify`、
  `IReadOnlyList<SmartRow>? SmartAttrs`、`string? DiskModel`、`double? ClaimedCapacityGB`、
  `long? BatteryDesignMWh`、`long? BatteryFullMWh`、`DateTime Now`

**取值與判斷分家**：`Build` 是純函式（給什麼算什麼、缺什麼就不放進事實袋），
`Collect` 只負責去讀。這是專案既有 `SpecFactsCollector` 的同一套分工。

- [ ] **Step 1：寫失敗的測試**

```csharp
[Fact]
public void 讀不到的部分不得放進事實袋_也不得填零()
{
    var facts = VerifyFactsCollector.Build(new() { Now = DateTime.UnixEpoch });   // 什麼都沒給
    Assert.False(facts.Has(FactId.DimmCount));
    Assert.False(facts.Has(FactId.NvmePowerOnHours));
    Assert.False(facts.Has(FactId.BatteryDesignCapacityMWh));
}

[Fact]
public void NVMe記錄存在時_通電小時與寫入量都要換成規則用的單位()
{
    var log = new byte[NvmeHealth.LogSize];
    BitConverter.GetBytes(1234ul).CopyTo(log, NvmeLogDecoder.OffPowerOnHours);
    BitConverter.GetBytes(1_000_000ul).CopyTo(log, NvmeLogDecoder.OffDataUnitsWritten);
    var facts = VerifyFactsCollector.Build(new() { NvmeLog = log, Now = DateTime.UnixEpoch });
    Assert.Equal(1234, facts.Num(FactId.NvmePowerOnHours));
    Assert.Equal(NvmeHealth.DataUnitsToGiB(1_000_000), facts.Num(FactId.NvmeDataUnitsWritten)!.Value, 3);
    Assert.Equal(FactTrust.Native, facts.Get(FactId.NvmePowerOnHours)!.Trust);   // 直接問控制器
    Assert.True(facts.Get(FactId.NvmePowerOnHours)!.NeedsAdmin);
}
```

- [ ] **Step 2–4：實作 `Build`／`Collect`、跑測試、提交**

`Build` 依序把 SMBIOS（`SmbiosFacts.From`）、NVMe（`NvmeHealth.Decode`）、
ATA IDENTIFY（`AtaIdentify.Decode`）、SMART 屬性（依編號取 0x09／0xF1／0x0C／0x03）、
電池四類事實併進一個 `VerifyFacts`。**任何一步失敗就跳過那一類，不放任何預設值。**

```bash
git add Services/VerifyFactsCollector.cs Services/SmbiosService.cs Tests/VerifyFactsCollectorTests.cs
git commit -m "feat(verify): 事實收集器（取值與判斷分家，缺就不放）"
```

---

### Task 12：驗機稽核頁

**Files:**
- Create: `Services/VerifyService.cs`、`Views/VerifyView.xaml`、`Views/VerifyView.xaml.cs`
- Modify: `Nav/PageRegistry.cs`（註冊「驗機稽核」）
- Test: 由既有 `Tests/UiSmokeTests.cs` 自動涵蓋（它會走訪 `PageRegistry.Pages` 建構每一頁）

**範圍限制（要寫進頁面說明）**：事實袋是平的，一次只驗**一顆碟**——使用者在頁面上選哪顆就驗哪顆。
多碟同時驗需要為 `FactId` 加索引或每碟一個事實袋，留給後續一輪。

- [ ] **Step 1：`VerifyService`**

`RunAsync(MainViewModel vm, int diskIndex)` → `VerifyFactsCollector.Collect` → `VerifyEngine.Run`
→ 依 `Part` 分組填入 `ObservableCollection<VerifyGroup>`；另曝露
`MatchCount`／`ConflictCount`／`UnreadCount` 三個數字（**不得相加成分數**）。

- [ ] **Step 2：`VerifyView`**

頂部一句話狀態 ＋ 「本工具未檢查的項目」清單（照規格第十節逐項列出）＋ 依部件分組的卡片。
每張卡片：判定色塊（`Severity` → 既有 `SeverityToBrushConverter`，**注意資源鍵是 XAML 裡的鍵名，
不是 C# 類別名**——1.9.1 那個 `XamlParseException` 就是這樣來的）、標題、說明、正當成因、
並排的證據列（每筆顯示值與 `Method`）。

- [ ] **Step 3：註冊頁面並跑煙霧測試**

在 `Nav/PageRegistry.cs` 的 `Pages` 陣列加一列（照既有形狀，Group 用 `GHardware`）：

```csharp
new()
{
    Key = "verify", Title = "驗機稽核", Group = GHardware,
    IconData = "F1 M12,1 L3,5 V11 C3,16.55 6.84,21.74 12,23 17.16,21.74 21,16.55 21,11 V5 L12,1 Z "
             + "M10.94,15.54 L7.4,12 8.81,10.59 10.94,12.72 15.19,8.47 16.6,9.88 Z",
    Factory = () => new VerifyView(),
    Hint = "把讀到的硬體事實互相對帳，列出矛盾與證據",
    Keywords = ["verify", "驗機", "二手", "翻新", "矛盾", "稽核"],
},
```

Run: `dotnet test Tests/XinSpect.Tests.csproj --nologo -v q --filter "FullyQualifiedName~UiSmoke"`
Expected: PASS——若 XAML 的資源鍵或繫結路徑寫錯，這裡就會紅

> **虛擬機橫幅不在第一批**：判斷 hypervisor 的 R-CPU-04 屬第二批，橫幅隨它一起上。
> 第一批不要放一條讀不到資料的空橫幅。


- [ ] **Step 4：提交**

```bash
git add Services/VerifyService.cs Views/VerifyView.xaml Views/VerifyView.xaml.cs Nav/PageRegistry.cs
git commit -m "feat(verify): 驗機稽核頁（含未檢查項目的明列）"
```

---

### Task 13：規則表不變式

**Files:** Create `Tests/VerifyRuleTableTests.cs`

照 `Tests/DeviceIconsTableTests.cs` 的作法，釘住整張規則表的形狀：

- [ ] **Step 1：寫測試**

```csharp
public class VerifyRuleTableTests
{
    /// <summary>每條規則的「會判成矛盾」樣本。加規則就要加一筆，否則本檔會紅——
    /// 這是防止寫出一條永遠不觸發的死規則的唯一辦法。</summary>
    private static readonly Dictionary<string, VerifyFact[]> ConflictSamples = new()
    {
        ["R-MEM-01"] = [N(FactId.DimmCount, 2), T(FactId.DimmManufacturers, "A|B"), T(FactId.DimmPartNumbers, "P1|P2")],
        ["R-MEM-02"] = [N(FactId.DimmCount, 2), T(FactId.DimmSerials, "00000000|1234ABCD")],
        ["R-MEM-03"] = [N(FactId.DimmSpeedMts, 3200), N(FactId.DimmConfiguredMts, 2133)],
        ["R-MEM-04"] = [N(FactId.DimmSizeTotalMiB, 32768), N(FactId.ArrayMaxCapacityMiB, 16384),
                        N(FactId.ArraySlotCount, 4), N(FactId.DimmCount, 2)],
        ["R-SSD-01"] = [N(FactId.NvmeDataUnitsWritten, 80000), N(FactId.NvmePowerOnHours, 100)],
        ["R-SSD-02"] = [N(FactId.NvmePercentageUsed, 0), N(FactId.NvmeDataUnitsWritten, 200_000)],
        ["R-SSD-03"] = [N(FactId.NvmeUnsafeShutdowns, 150), N(FactId.NvmePowerCycles, 100)],
        ["R-SSD-04"] = [N(FactId.NvmeCriticalWarning, 0b1000)],
        ["R-SSD-05"] = [N(FactId.DiskClaimedCapacityGB, 2000), N(FactId.AtaTotalLba, 1_953_525_168)],
        ["R-SSD-06"] = [N(FactId.AtaRotationRate, 1), N(FactId.SmartSpinUpPresent, 1)],
        ["R-BAT-01"] = [N(FactId.BatteryDesignCapacityMWh, 50000), N(FactId.BatteryFullCapacityMWh, 35000)],
    };

    private static VerifyFact N(FactId id, double n) => new(id, FactCatalog.Name(id), n.ToString("0.##"),
        n, "", FactSource.Derived, "測試合成", false, FactTrust.Derived, DateTime.UnixEpoch);
    private static VerifyFact T(FactId id, string v) => new(id, FactCatalog.Name(id), v,
        null, "", FactSource.Derived, "測試合成", false, FactTrust.Derived, DateTime.UnixEpoch);

    [Fact] public void 規則編號不得重複()
        => Assert.Empty(VerifyRules.All.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key));

    [Fact] public void 每條規則都必須宣告至少一個依賴事實()
        => Assert.All(VerifyRules.All, r => Assert.NotEmpty(r.RequiredFacts));

    [Fact] public void 依賴的事實都必須在FactCatalog裡有名字()
        => Assert.All(VerifyRules.All, r => Assert.All(r.RequiredFacts,
            id => Assert.NotEqual(id.ToString(), FactCatalog.Name(id))));

    [Fact] public void 空事實袋時_每條規則都回無法判定_且不得拋例外()
        => Assert.All(VerifyEngine.Run(new VerifyFacts([])),
            f => Assert.Equal(VerifyVerdict.Unread, f.Verdict));

    [Fact] public void 每條規則都要有矛盾樣本_且該樣本真的判成矛盾()
    {
        Assert.Equal(VerifyRules.All.Select(r => r.Id).OrderBy(x => x),
                     ConflictSamples.Keys.OrderBy(x => x));
        foreach (var (id, facts) in ConflictSamples)
            Assert.Equal(VerifyVerdict.Conflict,
                VerifyEngine.Run(new VerifyFacts(facts)).Single(x => x.Id == id).Verdict);
    }

    [Fact] public void 判為矛盾時必須附正當成因_除了物理上不可能的那一條()
    {
        foreach (var (id, facts) in ConflictSamples.Where(kv => kv.Key != "R-SSD-03"))
        {
            var f = VerifyEngine.Run(new VerifyFacts(facts)).Single(x => x.Id == id);
            Assert.False(string.IsNullOrWhiteSpace(f.BenignCause), $"{id} 缺正當成因");
        }
    }
}
```

> **「缺一邊 → 無法判定」不逐條重複測**：那是引擎統一處理的行為（Task 2 已測），
> 全體規則的涵蓋由上面的空事實袋測試負責，比抄 11 次同樣的測試可靠。


- [ ] **Step 2–3：跑測試、提交**

```bash
git add Tests/VerifyRuleTableTests.cs
git commit -m "test(verify): 規則表不變式（編號唯一／依賴有名字／空袋不炸／矛盾樣本齊全）"
```

---

## 完成後的驗收

```bash
dotnet test Tests/XinSpect.Tests.csproj --nologo -v q
```

預期：1566（現有基線）＋ 約 45 個新測試全通過、0 失敗。
另外在本機實跑一次驗機頁，確認：① 有管理員與沒有管理員兩種情況下的訊息都說得清楚；
② 每一筆證據都顯示得出讀取方法；③「未檢查的項目」清單有出現在頁面上。








