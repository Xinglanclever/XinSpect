using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// MADT 的事實輸出（ULTIMATE-1000 階段二 P2-006）。
///
/// <para>
/// <b>這一組的事實回答兩個不同的問題：</b>
/// ① 「這台機器宣告了幾個處理器」——它與 CPUID／OS 的說法是<b>三個獨立來源</b>，
/// 而三者不一致本身就是重要的事實（韌體宣告的處理器與 OS 看到的不一樣，
/// 通常意味著某種遮蔽或組態限制）。
/// ② 「中斷怎麼送到處理器」——x2APIC 與傳統 PIC 是否並存、ISA IRQ 被改送到哪個 GSI。
/// </para>
///
/// <para>
/// <b>界線：不宣稱中斷設定正確。</b>韌體說的和 OS 實際用的是不是同一套，
/// 報告只把兩邊並列——不一致是事實，不是判決。
/// </para>
/// </summary>
public static class AcpiMadtFactsService
{
    public const string SummaryKey = "ac.madt.summary";
    public const string LocalApicKey = "ac.madt.lapic";
    public const string X2ApicKey = "ac.madt.x2apic";
    public const string IoApicKey = "ac.madt.ioapic";
    public const string OverrideKey = "ac.madt.override";
    public const string X2ApicNmiKey = "ac.madt.x2apic.nmi";
    public const string UnrecordedKey = "ac.madt.unrecorded";

    private const string Category = "韌體";
    private const string Source = "ACPI 表列（MADT）";

    /// <summary>
    /// 從 ACPI 表來源收集 MADT 事實。表缺席＝不適用（很多平台確實沒有獨立 MADT 可讀），
    /// 表存在但壞掉＝讀取失敗——這兩者必須分開。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(IAcpiTableSource source, DateTimeOffset at)
    {
        if (!source.Available)
            return [Unavailable(SummaryKey, "MADT 彙總", at, FactAvailability.NotSupported,
                "ACPI 表來源不可用（需要管理員權限或韌體不提供）")];

        byte[]? madt = null;
        foreach (var table in source.ReadAll())
        {
            if (AcpiTable.TryParseHeader(table, out var header)
                && string.Equals(header.Signature, "MADT", StringComparison.OrdinalIgnoreCase))
            {
                madt = table;
                break;
            }
        }

        if (madt is null)
            return [Unavailable(SummaryKey, "MADT 彙總", at, FactAvailability.NotApplicable,
                "表列裡沒有 MADT——此平台未提供中斷控制器描述表，不是讀取失敗")];

        var h = AcpiMadt.Header(madt);
        if (h is null)
            return [Unavailable(SummaryKey, "MADT 彙總", at, FactAvailability.ReadError,
                "MADT 表短於表頭所需長度——如實拒解，不猜")];

        var structures = AcpiMadt.Structures(madt);
        if (structures.Count == 0)
            return [Unavailable(SummaryKey, "MADT 彙總", at, FactAvailability.ReadError,
                "MADT 沒有任何可走訪的子結構——宣告與資料不符")];

        var lapics = AcpiMadt.LocalApics(madt);
        var x2apics = AcpiMadt.LocalX2Apics(madt);
        var ioapics = AcpiMadt.IoApics(madt);
        var overrides = AcpiMadt.InterruptOverrides(madt);
        var nmis = AcpiMadt.LocalX2ApicNmis(madt);
        var counts = AcpiMadt.CountByType(madt);

        var facts = new List<HardwareFact>
        {
            Summary(at, h.Value, lapics.Count, x2apics.Count, ioapics.Count, overrides.Count, structures.Count),
            LocalApicFact(at, lapics),
            X2ApicFact(at, x2apics),
            IoApicFact(at, ioapics),
            OverrideFact(at, overrides),
            UnrecordedFact(at, counts),
        };

        if (nmis.Count > 0)
            facts.Add(new HardwareFact(X2ApicNmiKey, Category, "x2APIC NMI",
                string.Join("、", nmis.Select(n => $"UID {n.AcpiProcessorUid}→LINT{n.Lint}")),
                "條", Source, FactTrustLevel.Measured, false, at, nmis.Count));

        return facts;
    }

    private static HardwareFact Summary(DateTimeOffset at, (uint Address, uint Flags) h,
        int lapics, int x2apics, int ioapics, int overrides, int structures)
    {
        int declared = lapics + x2apics;
        string value = $"Local APIC 位址 0x{h.Address:X8}、旗標 0x{h.Flags:X}；"
                     + $"宣告 {declared} 個處理器（APIC {lapics}＋x2APIC {x2apics}）、"
                     + $"{ioapics} 個 I/O APIC、{overrides} 條中斷來源覆寫、共 {structures} 個子結構";

        return new HardwareFact(SummaryKey, Category, "MADT 彙總", value, "個", Source,
            FactTrustLevel.Measured, false, at, declared, FactAvailability.Present,
            declared == 0
                ? "表中宣告的處理器數是 0——這與 OS 看到的處理器數不同，兩者並列陳述不代為裁決"
                : null);
    }

    private static HardwareFact LocalApicFact(DateTimeOffset at, IReadOnlyList<MadtLocalApic> lapics)
    {
        if (lapics.Count == 0)
            return new HardwareFact(LocalApicKey, Category, "Local APIC 清單",
                "表中沒有傳統 Local APIC 結構", "個", Source, FactTrustLevel.Measured, false, at,
                0, FactAvailability.NotApplicable,
                "沒有傳統 Local APIC 通常表示平台走 x2APIC——這與 x2APIC 清單有無互相印證");

        bool anyEnabled = lapics.Any(l => (l.Flags & 1) != 0);
        return new HardwareFact(LocalApicKey, Category, "Local APIC 清單",
            $"APIC ID：{string.Join("、", lapics.Select(l => l.ApicId))}"
            + (anyEnabled ? "" : "（全部標為停用）"), "個", Source,
            FactTrustLevel.Measured, false, at, lapics.Count,
            anyEnabled ? FactAvailability.Present : FactAvailability.Unknown,
            anyEnabled ? null : "表中所有 Local APIC 的啟用位元都是 0——這是韌體自述，不代表 OS 沒在用");
    }

    private static HardwareFact X2ApicFact(DateTimeOffset at, IReadOnlyList<MadtLocalX2Apic> x2apics)
    {
        if (x2apics.Count == 0)
            return new HardwareFact(X2ApicKey, Category, "Local x2APIC 清單",
                "表中沒有 x2APIC 結構", "個", Source, FactTrustLevel.Measured, false, at,
                0, FactAvailability.NotApplicable,
                "沒有 x2APIC 結構表示平台以傳統 APIC 描述——255 個處理器以上不可能只用傳統 APIC");

        // x2APIC ID 可以超過 255，這是它存在的理由——本機若真的有超過 255 的值，值得指認。
        bool anyAboveByte = x2apics.Any(x => x.X2ApicId > 0xFF);
        return new HardwareFact(X2ApicKey, Category, "Local x2APIC 清單",
            $"x2APIC ID：{string.Join("、", x2apics.Select(x => x.X2ApicId))}", "個", Source,
            FactTrustLevel.Measured, false, at, x2apics.Count,
            anyAboveByte ? FactAvailability.Present : FactAvailability.Present,
            anyAboveByte
                ? "本機有 x2APIC ID 超過 255——那正是 x2APIC 存在的理由（傳統 APIC ID 只有 8 位元）"
                : null);
    }

    private static HardwareFact IoApicFact(DateTimeOffset at, IReadOnlyList<MadtIoApic> ioapics)
    {
        if (ioapics.Count == 0)
            return new HardwareFact(IoApicKey, Category, "I/O APIC 清單",
                "表中沒有 I/O APIC 結構", "個", Source, FactTrustLevel.Measured, false, at,
                0, FactAvailability.Unknown,
                "沒有 I/O APIC 在多處理器 x86 平台上不尋常——但這一項只陳述事實，不判異常");

        string value = string.Join("、", ioapics.Select(i =>
            $"#{i.Id} 位址 0x{i.Address:X8} GSI 基底 {i.GlobalSystemInterruptBase}"));

        // GSI 基底重疊＝兩顆 I/O APIC 宣告了同一段中斷號，那會讓 IRQ 的歸屬變成不確定的。
        var overlapping = ioapics
            .GroupBy(i => i.GlobalSystemInterruptBase)
            .Where(g => g.Count() > 1)
            .Select(g => $"GSI 基底 {g.Key}（{g.Count()} 顆）")
            .ToList();

        return new HardwareFact(IoApicKey, Category, "I/O APIC 清單", value, "個", Source,
            FactTrustLevel.Measured, false, at, ioapics.Count,
            overlapping.Count > 0 ? FactAvailability.Unknown : FactAvailability.Present,
            overlapping.Count > 0
                ? "有 I/O APIC 宣告相同的 GSI 基底：" + string.Join("、", overlapping)
                  + "——那會讓那一段中斷號的歸屬變成不確定的，這是事實陳述不是判決"
                : null);
    }

    private static HardwareFact OverrideFact(DateTimeOffset at, IReadOnlyList<MadtInterruptOverride> overrides)
    {
        if (overrides.Count == 0)
            return new HardwareFact(OverrideKey, Category, "中斷來源覆寫",
                "沒有任何覆寫——ISA IRQ 直接對應 GSI", "條", Source,
                FactTrustLevel.Measured, false, at, 0, FactAvailability.Present,
                "沒有覆寫是常態；有覆寫時才需要知道哪一條被改送到哪裡");

        string value = string.Join("、", overrides.Select(o =>
            $"bus{o.Bus} 來源{o.Source}→GSI{o.GlobalSystemInterrupt}(0x{o.Flags:X4})"));

        return new HardwareFact(OverrideKey, Category, "中斷來源覆寫", value, "條", Source,
            FactTrustLevel.Measured, false, at, overrides.Count, FactAvailability.Present,
            "覆寫代表 ISA IRQ 被改送到別的 GSI——這會影響中斷延遲類量測的解讀");
    }

    private static HardwareFact UnrecordedFact(DateTimeOffset at, IReadOnlyDictionary<byte, int> counts)
    {
        var unknown = counts.Where(kv => AcpiMadt.DescribeType(kv.Key) is null).ToList();
        if (unknown.Count == 0)
            return new HardwareFact(UnrecordedKey, Category, "未收錄的子結構型別",
                "表中所有子結構型別都有解碼器", "種", Source, FactTrustLevel.Measured,
                false, at, 0, FactAvailability.Present, null);

        string value = string.Join("、", unknown.Select(kv => $"型別 {kv.Key}（{kv.Value} 個）"));
        return new HardwareFact(UnrecordedKey, Category, "未收錄的子結構型別", value, "種", Source,
            FactTrustLevel.Measured, false, at, unknown.Count, FactAvailability.Unknown,
            "有本版沒有解碼器的子結構型別——它們只被計數、沒有被解讀，"
            + "解碼器覆蓋率因此不是 100%（未收錄不等於表有問題）");
    }

    private static HardwareFact Unavailable(string key, string name, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", Source, FactTrustLevel.Unknown, false, at,
            null, availability, reason);
}
