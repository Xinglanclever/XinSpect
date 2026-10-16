using System.Text;

namespace XinSpect;

/// <summary>
/// FIT 表與 Boot Guard 事實服務（P0-02／FW-001）。
/// <para>
/// 讀快閃尾端 0x400（經既有 SPIBAR MMIO 後端——<see cref="SpiFlashService.ReadController"/>）→
/// <see cref="FitDecoder"/> 向下找 "_FIT_" 簽名解析條目。<b>界線</b>：只呈現「目錄上有什麼」，
/// 不判「是否被繞過」、不驗 ACM 簽章；<b>Boot Guard profile 需要 flash descriptor（FLMAP／FMLF）佈局——
/// 未對準出處前不出值</b>（如實標 NotSupported，不是 0 也不是 Off）；驅動未載＝通路未就緒三態。
/// </para>
/// </summary>
public static class SpiFitFactsService
{
    public const string Category = "韌體安全";
    public const string SummaryKey = "fw.fit.summary";
    public const string CountKey = "fw.fit.count";
    public const string BootGuardKey = "fw.bg.profile";

    private const string Source = "SPI 快閃尾端（唯讀 MMIO）＋金標 FIT 解碼器";

    public static IReadOnlyList<HardwareFact> Collect(IPciConfigReader pci, IMmioReader mmio, DateTimeOffset at)
    {

        var access = SpiFlashService.ReadController(pci, mmio, out var availability, out string error);
        if (access is null)
            return
            [
                Unavailable(SummaryKey, at, availability, error),
                Unavailable(CountKey, at, availability, error),
                Unavailable(BootGuardKey, at, availability, error),
            ];

        var fregs = Enumerable.Range(0, 6).Select(i => BitConverter.ToUInt32(access.Block, 0x54 + i * 4)).ToArray();
        var map = SpiFlashMap.Decode(fregs);
        if (map is null)
            return
            [
                Unavailable(SummaryKey, at, FactAvailability.NotApplicable, "FREG0-5 全空或範圍異常——無從定址快閃尾端，不猜"),
                Unavailable(CountKey, at, FactAvailability.NotApplicable, "快閃地圖無法推導"),
                Unavailable(BootGuardKey, at, FactAvailability.NotApplicable, "快閃地圖無法推導"),
            ];

        var (tail, _, readError) = SpiFlashHashService.ReadRange(
            mmio, map.MappedBase + map.FlashSizeBytes - FitDecoder.TailSearchBytes, FitDecoder.TailSearchBytes);
        if (tail is null)
            return
            [
                Unavailable(SummaryKey, at, FactAvailability.InsufficientPrivilege,
                    "快閃尾端讀取失敗" + (readError is null ? "" : "（" + readError + "）") + "——讀不到不等於沒有 FIT"),
                Unavailable(CountKey, at, FactAvailability.InsufficientPrivilege, "快閃尾端讀取失敗"),
                Unavailable(BootGuardKey, at, FactAvailability.InsufficientPrivilege, "快閃尾端讀取失敗"),
            ];

        var table = FitDecoder.Parse(tail);
        if (table is null)
            return
            [
                Unavailable(SummaryKey, at, FactAvailability.NotApplicable,
                    $"尾端 {FitDecoder.TailSearchBytes} 位元組內沒有 FIT 簽名——老平台沒有 FIT 是正常答案，不猜"),
                Unavailable(CountKey, at, FactAvailability.NotApplicable, "沒有 FIT 簽名"),
                Unavailable(BootGuardKey, at, FactAvailability.NotSupported,
                    "Boot Guard profile 需要 flash descriptor（FLMAP／FMLF）佈局——未對準出處前不出值"),
            ];

        string entries = string.Join("；", table.Entries.Take(12).Select(e =>
                $"0x{e.Type:X4} {e.TypeName}（位移 0x{e.Offset:X}／{e.Size}B）"))
            + (table.Entries.Count > 12 ? "；等 " + (table.Entries.Count - 12) + " 條" : "")
            + (table.TruncatedReason is null ? "" : "；" + table.TruncatedReason);
        int microcode = table.Entries.Count(e => e.Type == 0x00);
        int acm = table.Entries.Count(e => e.Type == 0x02 || e.Type == 0x07);

        return
        [
            new HardwareFact(SummaryKey, Category, "FIT 表（開機信任根目錄）",
                $"快閃 {map.FlashSizeBytes / 1048576} MiB・條目 {table.Entries.Count} 條（微碼 {microcode}／ACM·Startup {acm}）：{entries}・"
                + "界線：只呈現目錄上有什麼，不判「是否被繞過」、不驗 ACM 簽章",
                "條", Source, FactTrustLevel.Measured, false, at, table.Entries.Count),
            new HardwareFact(CountKey, Category, "FIT 條目數", table.Entries.Count + " 條", "條", Source,
                FactTrustLevel.Measured, false, at, table.Entries.Count),
            new HardwareFact(BootGuardKey, Category, "Boot Guard profile", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "Boot Guard profile 需要 flash descriptor（FLMAP／FMLF）佈局——未對準出處前不出值；"
                + "有 FIT 目錄不代表 Boot Guard 啟用，兩件事"),
            // ULTIMATE-1000 階段二 P2-054／P2-055：把同一張 FIT 表按信任鏈分類。
            // **不另外再讀一次快閃**——同一份表來源，與 MADT／DMAR／SRAT 接在同一次讀取上同理。
            .. BootGuardFactsService.Collect(at, table),
        ];
    }

    private static HardwareFact Unavailable(string key, DateTimeOffset at, FactAvailability avail, string why) =>
        new(key, Category, key switch
        {
            SummaryKey => "FIT 表（開機信任根目錄）",
            CountKey => "FIT 條目數",
            _ => "Boot Guard profile",
        }, "", key == SummaryKey ? "條" : "", Source, FactTrustLevel.Unknown, false, at, null, avail, why);
}
