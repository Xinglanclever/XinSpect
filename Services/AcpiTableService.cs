using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>ACPI 表來源的可注入接縫：真實以 EnumSystemFirmwareTables + GetSystemFirmwareTable 取；測試注入合成表，不碰韌體。</summary>
public interface IAcpiTableSource
{
    bool Available { get; }
    string? UnavailableReason { get; }
    IReadOnlyList<byte[]> ReadAll();
}

/// <summary>把 ACPI 表列舉成三態事實：列不到就標不可用，列得到則逐表給簽章/版本/OEM/校驗和，另附一條清單摘要。</summary>
public static class AcpiService
{
    public static IReadOnlyList<HardwareFact> Collect(IAcpiTableSource source, DateTimeOffset at)
    {
        var facts = new List<HardwareFact>();
        if (!source.Available)
        {
            facts.Add(new HardwareFact("acpi.tables", "ACPI", "ACPI 表清單", "", "", "EnumSystemFirmwareTables",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.InsufficientPrivilege,
                source.UnavailableReason ?? "無法列舉 ACPI 表"));
            return facts;
        }

        var parsed = new List<(byte[] Bytes, AcpiTableHeader Header)>();
        foreach (var bytes in source.ReadAll())
            if (AcpiTable.TryParseHeader(bytes, out var h)) parsed.Add((bytes, h));

        var sigCount = parsed.GroupBy(p => Slug(p.Header.Signature)).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var occ = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (bytes, h) in parsed)
        {
            string slug = Slug(h.Signature);
            int n = occ.TryGetValue(slug, out int c) ? c + 1 : 1;
            occ[slug] = n;
            string key = sigCount[slug] > 1 ? $"acpi.table.{slug}.{n}" : $"acpi.table.{slug}";
            facts.Add(new HardwareFact(key, "ACPI", h.Signature,
                $"rev {h.Revision}，OEM {h.OemId}，校驗和 {(h.ChecksumValid ? "正確" : "錯誤")}", "",
                "GetSystemFirmwareTable", FactTrustLevel.Measured, false, at));

            // 單一實例表的安全加值（只解已明確定義的單一欄位，不碰可變長度 body）。
            if (slug == "hest" && n == 1 && AcpiTable.HestErrorSourceCount(bytes) is { } sources)
                facts.Add(new HardwareFact("acpi.hest.sources", "ACPI", "硬體錯誤來源數",
                    sources.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "ACPI HEST",
                    FactTrustLevel.Measured, false, at, sources));
            if (slug == "bert" && n == 1 && AcpiTable.BertBootErrorRegionLength(bytes) is { } regionLen)
            {
                facts.Add(new HardwareFact("acpi.bert.region_length", "ACPI", "開機錯誤區長度",
                    regionLen.ToString(System.Globalization.CultureInfo.InvariantCulture), "bytes", "ACPI BERT",
                    FactTrustLevel.Measured, false, at, regionLen));
                // 實際錯誤記錄在實體位址，usermode 讀不到——誠實標三態，不假裝沒有錯誤。
                facts.Add(new HardwareFact("acpi.bert.record", "ACPI", "上次開機錯誤記錄", "", "", "ACPI BERT + 實體記憶體",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.InsufficientPrivilege,
                    "錯誤記錄在實體位址，需 ring0 讀取（Phase 3 MMIO）"));
            }
        }

        facts.Add(new HardwareFact("acpi.tables", "ACPI", "ACPI 表清單",
            string.Join(" ", parsed.Select(p => p.Header.Signature).OrderBy(x => x, StringComparer.Ordinal)), "",
            "EnumSystemFirmwareTables", FactTrustLevel.Measured, false, at));
        return facts;
    }

    private static string Slug(string signature)
    {
        string s = new(signature.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return s.Length == 0 ? "unknown" : s;
    }
}

/// <summary>以 Win32 firmware-table API 列舉並讀取所有 ACPI 表（usermode，不需驅動）。失敗即 Available=false 帶原因。</summary>
public sealed class Win32AcpiTableSource : IAcpiTableSource
{
    private const uint AcpiProvider = 0x41435049; // 'ACPI'（與 SecurityPostureService 一致）

    public bool Available { get; private set; } = true;
    public string? UnavailableReason { get; private set; }

    public IReadOnlyList<byte[]> ReadAll()
    {
        try
        {
            uint size = EnumSystemFirmwareTables(AcpiProvider, null, 0);
            if (size == 0)
            {
                Available = false;
                UnavailableReason = "EnumSystemFirmwareTables 回報 0 張 ACPI 表";
                return [];
            }
            var ids = new byte[size];
            EnumSystemFirmwareTables(AcpiProvider, ids, size);

            var tables = new List<byte[]>();
            for (int i = 0; i + 4 <= ids.Length; i += 4)
            {
                uint id = BitConverter.ToUInt32(ids, i);
                uint tsize = GetSystemFirmwareTable(AcpiProvider, id, null, 0);
                if (tsize == 0) continue;
                var buf = new byte[tsize];
                if (GetSystemFirmwareTable(AcpiProvider, id, buf, tsize) == tsize) tables.Add(buf);
            }
            return tables;
        }
        catch (Exception ex)
        {
            Available = false;
            UnavailableReason = ex.Message;
            return [];
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint EnumSystemFirmwareTables(uint provider, byte[]? buffer, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);
}
