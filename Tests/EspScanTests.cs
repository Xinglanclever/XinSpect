using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// ESP 檔案層掃描的契約：.efi 清單與 SHA-256 如實、dbx 交叉引用的雙向結果（命中＝攻擊面事實不是
/// 中毒判決；未命中＝不代表安全）、三態（列舉失敗、無 ESP、dbx 讀不到不假裝比對過）、
/// EFI_SIGNATURE_LIST 的 SHA-256 條目解碼（金標按規格編碼）。全程唯讀。
/// </summary>
public class EspScanTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    // ── EFI_SIGNATURE_LIST 金標：SHA-256 型清單兩筆簽章 ──────────────────────

    private static byte[] BuildDbx(params byte[][] sha256Hashes)
    {
        // SignatureType(SHA-256 GUID 16) + SignatureListSize(4) + SignatureHeaderSize(4)=0 + SignatureSize(4)=48
        // + 每筆：SignatureOwner GUID(16) + SHA-256(32)
        int entrySize = 16 + 32;
        int listSize = 28 + sha256Hashes.Length * entrySize;
        var data = new byte[listSize];
        new Guid(EfiSigListDecoder.Sha256TypeGuid).ToByteArray().CopyTo(data, 0);
        BitConverter.GetBytes((uint)listSize).CopyTo(data, 16);
        BitConverter.GetBytes(0u).CopyTo(data, 20);
        BitConverter.GetBytes((uint)entrySize).CopyTo(data, 24);
        for (int i = 0; i < sha256Hashes.Length; i++)
        {
            int p = 28 + i * entrySize;
            new Guid("11111111-2222-3333-4444-555555555555").ToByteArray().CopyTo(data, p);
            Array.Copy(sha256Hashes[i], 0, data, p + 16, 32);
        }
        return data;
    }

    [Fact]
    public void dbx金標_逐筆解出SHA256_小寫十六進位()
    {
        var h1 = new byte[32]; h1[0] = 0xAB;
        var h2 = new byte[32]; h2[31] = 0xCD;
        var hashes = EfiSigListDecoder.DecodeSha256Hashes(BuildDbx(h1, h2));
        Assert.Equal(2, hashes.Count);
        Assert.Equal(Convert.ToHexString(h1).ToLowerInvariant(), hashes[0]);
        Assert.Equal(Convert.ToHexString(h2).ToLowerInvariant(), hashes[1]);
    }

    [Fact]
    public void dbx非SHA256清單_不進雜湊清單()
    {
        // 憑證型（SignatureType ≠ SHA-256 GUID）——「雜湊對雜湊」比不了的形狀，如實不列
        int entrySize = 16 + 32;
        var data = new byte[28 + entrySize];
        new Guid("99999999-8888-7777-6666-555555555555").ToByteArray().CopyTo(data, 0);
        BitConverter.GetBytes((uint)data.Length).CopyTo(data, 16);
        BitConverter.GetBytes(0u).CopyTo(data, 20);
        BitConverter.GetBytes((uint)entrySize).CopyTo(data, 24);
        Assert.Empty(EfiSigListDecoder.DecodeSha256Hashes(data));
    }

    // ── 服務層 ──────────────────────────────────────────────────────────────

    private (string Vol, Func<IReadOnlyList<string>?> List) MakeEsp(params (string Name, byte[] Content)[] files)
    {
        var dir = Directory.CreateTempSubdirectory("espscan-").FullName;
        foreach (var (name, content) in files)
        {
            var full = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }
        return (dir + Path.DirectorySeparatorChar, () => [dir + Path.DirectorySeparatorChar]);
    }

    [Fact]
    public void ESP有efi檔_清單與SHA256如實_未命中dbx的界線在文字裡()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var (vol, list) = MakeEsp(("EFI\\Boot\\bootx64.efi", content), ("EFI\\Extra\\tool.efi", new byte[] { 9 }));
        try
        {
            var facts = EspScanService.Collect(At, listVolumes: list, readDbx: () => null);

            var count = facts.Single(x => x.Key == EspScanService.CountKey);
            Assert.Equal(FactAvailability.Present, count.Availability);
            Assert.Equal(1, count.NumericValue);
            Assert.Contains("2 個", count.Value);
            Assert.Contains("不是對任何檔案的好壞判決", count.Value);

            var files = Assert.Single(facts, x => x.Key == "esp.files");
            Assert.Contains("bootx64.efi", files.Value);
            Assert.Contains(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()[..16], files.Value);

            // dbx 讀不到（變數不存在＝null）→ 比對未進行，如實標注
            var dbx = Assert.Single(facts, x => x.Key == "esp.dbx");
            Assert.Contains("比對未進行", dbx.Value);
            Assert.Contains("不假裝比對過", dbx.Value);
        }
        finally { Directory.Delete(vol, recursive: true); }
    }

    [Fact]
    public void dbx命中_是攻擊面事實不是中毒判決()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var sha = SHA256.HashData(content);
        var (vol, list) = MakeEsp(("bootx64.efi", content));
        try
        {
            var facts = EspScanService.Collect(At, listVolumes: list, readDbx: () => BuildDbx(sha));
            var dbx = Assert.Single(facts, x => x.Key == "esp.dbx");
            Assert.Equal(FactAvailability.Present, dbx.Availability);
            Assert.Contains("命中 1 個檔案", dbx.Value);
            Assert.Contains("不是中毒判決", dbx.Value);

            var files = Assert.Single(facts, x => x.Key == "esp.files");
            Assert.Contains("dbx 命中", files.Value);
        }
        finally { Directory.Delete(vol, recursive: true); }
    }

    [Fact]
    public void dbx未命中_明說不代表安全()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var (vol, list) = MakeEsp(("bootx64.efi", content));
        try
        {
            var other = new byte[32]; other[0] = 0xFF;
            var facts = EspScanService.Collect(At, listVolumes: list, readDbx: () => BuildDbx(other));
            var dbx = Assert.Single(facts, x => x.Key == "esp.dbx");
            Assert.Contains("未命中", dbx.Value);
            Assert.Contains("不代表安全", dbx.Value);
        }
        finally { Directory.Delete(vol, recursive: true); }
    }

    [Fact]
    public void 沒有ESP_如實標不適用_不是錯誤()
    {
        var fact = Assert.Single(EspScanService.Collect(At, listVolumes: () => []));
        Assert.Equal(FactAvailability.NotApplicable, fact.Availability);
        Assert.Contains("不是錯誤", fact.UnavailableReason);
    }

    [Fact]
    public void 列舉失敗_如實標讀取失敗不猜()
    {
        var fact = Assert.Single(EspScanService.Collect(At, listVolumes: () => null));
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Contains("讀不到不猜", fact.UnavailableReason);
    }

    [Fact]
    public void 檔案讀不到_逐項標注_不冒充掃完()
    {
        var (vol, list) = MakeEsp(("bootx64.efi", new byte[] { 1 }));
        try
        {
            var facts = EspScanService.Collect(At, listVolumes: list, readFile: _ => null);
            var files = Assert.Single(facts, x => x.Key == "esp.files");
            Assert.Contains("讀不到", files.Value);
            var count = facts.Single(x => x.Key == EspScanService.CountKey);
            Assert.Contains("有讀取失敗的項目", count.Value);
        }
        finally { Directory.Delete(vol, recursive: true); }
    }
}
