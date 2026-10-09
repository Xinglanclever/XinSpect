using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

/// <summary>
/// UEFI 變數完整審計（V7 WP14 延伸）：db/dbx/KEK/PK 簽章清單＋Boot#### 條目名稱。
/// 全部透過 GetFirmwareEnvironmentVariableEx（唯讀），需系統管理員。
/// </summary>
public static class UefiSignatureFactsService
{
    private const string Category = "信賴根";

    // UEFI 變數命名空間 GUID
    private static readonly byte[] GuidImageSecurity = Guid.Parse("a7719a85-d6cd-4be1-a1bd-4b0e8bee64c5").ToByteArray();
    private static readonly byte[] GuidGlobalVar = Guid.Parse("8be4df61-93ca-11d2-aa0d-00e098032b8c").ToByteArray();

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableExW(
        string lpName, string lpGuid, byte[]? pBuffer, uint dwSize, ref uint pdwAttrib);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<string, byte[]?>? readVariable = null)
    {
        var reader = readVariable ?? ReadFirmwareVar;
        var facts = new List<HardwareFact>();

        // ── db（允許的簽章資料庫）──
        CollectSigList(facts, "uefi.db", "db（允許簽章資料庫）", "db", GuidImageSecurity, reader, at);
        // ── dbx（撤銷簽章資料庫）──
        CollectSigList(facts, "uefi.dbx", "dbx（撤銷簽章資料庫）", "dbx", GuidImageSecurity, reader, at);
        // ── KEK（金鑰交換金鑰）──
        CollectSigList(facts, "uefi.kek", "KEK（金鑰交換金鑰）", "KEK", GuidGlobalVar, reader, at);
        // ── PK（平台金鑰）──
        var pk = reader("PK");
        facts.Add(pk is { Length: > 16 }
            ? new HardwareFact("uefi.pk", Category, "PK（平台金鑰）", $"存在（{pk.Length} bytes）", "",
                "GetFirmwareEnvironmentVariableEx（EFI Global Variable）", FactTrustLevel.Reported, false, at, pk.Length)
            : new HardwareFact("uefi.pk", Category, "PK（平台金鑰）", "", "",
                "GetFirmwareEnvironmentVariableEx（EFI Global Variable）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported, pk is null ? "PK 讀取失敗（可能未進入 Setup Mode）" : "PK 為空"));

        // ── Boot#### 條目 ──
        CollectBootEntries(facts, reader, at);
        return facts;
    }

    private static void CollectSigList(List<HardwareFact> facts, string keyPrefix, string name,
        string varName, byte[] guid, Func<string, byte[]?> reader, DateTimeOffset at)
    {
        byte[]? data = reader(varName);
        if (data is null || data.Length < 28)
        {
            facts.Add(new HardwareFact(keyPrefix, Category, name, "", "",
                "GetFirmwareEnvironmentVariableEx", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported, $"{varName} 讀取失敗或為空（可能未進入 Setup Mode 或不支援）"));
            return;
        }
        var lists = EfiSigListDecoder.Decode(data);
        int totalSigs = lists.Sum(l => l.SignatureCount);
        facts.Add(new HardwareFact(keyPrefix + ".count", Category, name,
            $"{totalSigs} 條簽章（{lists.Count} 條清單）", "條",
            $"GetFirmwareEnvironmentVariableEx + EfiSigListDecoder", FactTrustLevel.Derived, false, at, totalSigs));
        for (int i = 0; i < lists.Count && i < 5; i++)
        {
            var l = lists[i];
            facts.Add(new HardwareFact($"{keyPrefix}.list.{i}", Category, $"{name} 清單 {i}",
                $"類型 {l.TypeGuid}・{l.SignatureCount} 條簽章・{l.TotalBytes} bytes", "",
                "EfiSigListDecoder（UEFI Spec §32.4.1）", FactTrustLevel.Measured, false, at, l.SignatureCount));
        }
    }

    private static void CollectBootEntries(List<HardwareFact> facts, Func<string, byte[]?> reader, DateTimeOffset at)
    {
        var bootEntries = new List<(ushort Index, string Description)>();
        for (ushort idx = 0; idx < 0xFFFF; idx++)
        {
            string varName = $"Boot{idx:X4}";
            byte[]? data = reader(varName);
            if (data is null || data.Length < 6) continue;
            // EFI_LOAD_OPTION：Attributes u32 + FilePathListLength u16 + Description（UTF-16LE null-terminated）
            int descOff = 6;
            var sb = new StringBuilder();
            while (descOff + 1 < data.Length)
            {
                ushort ch = BitConverter.ToUInt16(data, descOff);
                if (ch == 0) break;
                sb.Append((char)ch);
                descOff += 2;
            }
            bootEntries.Add((idx, sb.ToString()));
            if (bootEntries.Count >= 20) break;
        }

        facts.Add(new HardwareFact("uefi.boot_entries.count", Category, "UEFI 開機條目",
            bootEntries.Count == 0 ? "沒有 Boot#### 條目（可能未提權）" : $"{bootEntries.Count} 個開機條目", "個",
            "GetFirmwareEnvironmentVariableEx（Boot####）", FactTrustLevel.Reported, false, at, bootEntries.Count));
        for (int i = 0; i < bootEntries.Count && i < 10; i++)
        {
            var (idx, desc) = bootEntries[i];
            facts.Add(new HardwareFact($"uefi.boot.{idx}", Category, $"Boot{idx:X4}",
                string.IsNullOrEmpty(desc) ? $"Boot{idx:X4}（無描述）" : desc, "",
                "GetFirmwareEnvironmentVariableEx（EFI_LOAD_OPTION Description）", FactTrustLevel.Reported, false, at, null));
        }
    }

    /// <summary>讀韌體環境變數（薄通路）。回 null 表示讀取失敗或變數不存在。</summary>
    internal static byte[]? ReadFirmwareVar(string name)
    {
        try
        {
            // 先以 0 長度查大小
            uint size = 0;
            uint attrib = 0;
            var guidStr = name.StartsWith("db") || name == "PK" || name == "KEK"
                ? name.StartsWith("db") ? FormatGuidString(GuidImageSecurity) : FormatGuidString(GuidGlobalVar)
                : FormatGuidString(GuidGlobalVar);
            GetFirmwareEnvironmentVariableExW(name, guidStr, null, 0, ref size);
            if (size == 0 || size > 1024 * 1024) return null; // 上限 1 MB
            var buf = new byte[size];
            uint actual = (uint)buf.Length;
            uint rc = GetFirmwareEnvironmentVariableExW(name, guidStr, buf, actual, ref actual);
            if (rc == 0) return null;
            return buf;
        }
        catch { return null; }
    }

    private static string FormatGuidString(byte[] guidBytes)
    {
        // UEFI GUID 在 Windows API 需要字串格式 "{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}"
        var g = new Guid(guidBytes);
        return g.ToString("B").ToUpperInvariant();
    }
}
