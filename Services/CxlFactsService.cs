namespace XinSpect;

/// <summary>
/// WP19 CXL 事實：ACPI CEDT 的存在與固定記憶體窗口。本機無 CEDT＝**NotApplicable**（無此硬體），
/// 不是讀取錯誤——三態的「不適用」就是為這種情況準備的。
/// </summary>
public static class CxlFactsService
{
    private const string Category = "系統與軟體";
    private const string Source = "ACPI 表列（CEDT）＋CXL 規格解碼";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, IAcpiTableSource acpi)
    {
        byte[]? cedt = null;
        if (acpi.Available)
        {
            foreach (var table in acpi.ReadAll())
            {
                if (AcpiTable.TryParseHeader(table, out var header) && header.Signature == "CEDT")
                {
                    cedt = table;
                    break;
                }
            }
        }

        if (cedt is null)
        {
            return [new HardwareFact("cxl.cfmws.count", Category, "CXL 固定記憶體窗口", "", "",
                Source, FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "表列裡沒有 CEDT：此平台沒有（或韌體未提供）CXL 固定記憶體窗口——無此硬體不是錯誤")];
        }

        var windows = CedtDecoder.DecodeCfmws(cedt);
        var facts = new List<HardwareFact>
        {
            new("cxl.cfmws.count", Category, "CXL 固定記憶體窗口", windows.Count.ToString(), "個",
                Source, FactTrustLevel.Measured, false, at, windows.Count),
        };
        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            facts.Add(new HardwareFact($"cxl.cfmws.{i}", Category, $"CXL 窗口 {i}",
                $"基底 0x{w.BaseHpa:X}・大小 {FormatSize(w.WindowSize)}・交叉 {w.InterleaveWays} 路・HwSuppVer {w.HwSuppVer}",
                "", Source, FactTrustLevel.Measured, false, at, (double)w.WindowSize));
        }
        return facts;
    }

    private static string FormatSize(ulong bytes) => bytes switch
    {
        _ when bytes % (1UL << 30) == 0 => $"{bytes >> 30} GiB",
        _ when bytes % (1UL << 20) == 0 => $"{bytes >> 20} MiB",
        _ => $"{bytes} B",
    };
}
