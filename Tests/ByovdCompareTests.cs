using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// BYOVD 逐驅動比對微軟建議封鎖清單的契約：清單 XML（微軟建議驅動封鎖規則格式）的純解碼
/// ＋載入中核心模組的逐檔雜湊／檔名比對。清單檔由使用者提供（無出網）——缺檔如實標
/// NotSupported、壞 XML 標 ReadError；命中逐條列模組與規則依據（檔名或 SHA-256）。
/// </summary>
public class ByovdCompareTests
{
    private const string SampleXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <SiPolicy xmlns="urn:schemas-microsoft-com:sipolicy">
          <FileRules>
            <FileRule FriendlyName="Driver Rule 1" FileName="gdrv.sys" />
            <FileRule FriendlyName="Driver Rule 2" MinimumSHA256Hash="AABB00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDD" />
            <FileRule FriendlyName="Driver Rule 3" FileName="EVIL.SYS" />
          </FileRules>
        </SiPolicy>
        """;

    private static readonly DateTimeOffset At = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 清單解碼_檔名與雜湊規則分開計數()
    {
        var rules = XinSpect.ByovdBlocklistDecoder.Parse(SampleXml);
        Assert.Equal(3, rules.Count);
        Assert.Equal(2, rules.Count(r => r.FileName is not null));
        Assert.Equal(1, rules.Count(r => r.Sha256 is not null));
        Assert.Equal("gdrv.sys", rules[0].FileName);
        Assert.Equal(64, rules[1].Sha256!.Length);
    }

    [Fact]
    public void 清單解碼_壞XML與空清單如實拒()
    {
        Assert.Empty(XinSpect.ByovdBlocklistDecoder.Parse("不是 XML"));
        Assert.Empty(XinSpect.ByovdBlocklistDecoder.Parse("<root><other/></root>"));
    }

    [Fact]
    public void 比對_檔名命中與雜湊命中與零命中()
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), $"byovd-{Guid.NewGuid():N}.xml");
        File.WriteAllText(xmlPath, SampleXml);
        try
        {
            // 檔名命中（不分大小寫）：C:\Windows\System32\drivers\GDRV.SYS
            var hitByName = XinSpect.ByovdCompareService.Collect(At, xmlPath,
                kernelModules: () => new List<XinSpect.KernelModuleEntry>
                {
                    new(@"C:\Windows\System32\drivers\GDRV.SYS", true, ""),
                    new(@"C:\Windows\System32\drivers\tcpip.sys", null, ""),
                });
            var hits = hitByName.Where(f => f.Key.StartsWith("byovd.hit.")).ToList();
            Assert.Single(hits);
            Assert.Contains("Driver Rule 1", hits[0].Value);
            Assert.Contains("gdrv", hits[0].Value.ToLowerInvariant());
            Assert.Contains("檔名", hits[0].Value);

            // 零命中：只有 tcpip.sys
            var clean = XinSpect.ByovdCompareService.Collect(At, xmlPath,
                kernelModules: () => new List<XinSpect.KernelModuleEntry>
                {
                    new(@"C:\Windows\System32\drivers\tcpip.sys", null, ""),
                });
            var zero = Assert.Single(clean, f => f.Key == "byovd.hits");
            Assert.Equal(FactAvailability.Present, zero.Availability);
            Assert.Contains("0 個", zero.Value);
        }
        finally { File.Delete(xmlPath); }
    }

    [Fact]
    public void 比對_雜湊規則對實檔生效()
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), $"byovd-{Guid.NewGuid():N}.xml");
        string drvPath = Path.Combine(Path.GetTempPath(), $"fake-{Guid.NewGuid():N}.sys");
        try
        {
            byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x01];
            File.WriteAllBytes(drvPath, payload);
            string sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
            string xml = $"""
                <SiPolicy><FileRules><FileRule FriendlyName="HashRule" MinimumSHA256Hash="{sha256}" /></FileRules></SiPolicy>
                """;
            File.WriteAllText(xmlPath, xml);

            var facts = XinSpect.ByovdCompareService.Collect(At, xmlPath,
                kernelModules: () => new List<XinSpect.KernelModuleEntry>
                {
                    new(drvPath, true, ""),
                });
            var hit = Assert.Single(facts, f => f.Key == "byovd.hit.0");
            Assert.Contains("SHA-256", hit.Value);
            Assert.Contains("HashRule", hit.Value);
        }
        finally { File.Delete(xmlPath); File.Delete(drvPath); }
    }

    [Fact]
    public void 缺清單與壓清單與缺模組清單_三態分得清楚()
    {
        var missing = XinSpect.ByovdCompareService.Collect(At, null,
            kernelModules: () => new List<XinSpect.KernelModuleEntry>());
        Assert.Equal(FactAvailability.NotSupported,
            Assert.Single(missing, f => f.Key == "byovd.rules").Availability);
        Assert.Contains("未提供", Assert.Single(missing, f => f.Key == "byovd.rules").UnavailableReason);

        string badPath = Path.Combine(Path.GetTempPath(), $"byovd-bad-{Guid.NewGuid():N}.xml");
        File.WriteAllText(badPath, "垃圾內容");
        try
        {
            var broken = XinSpect.ByovdCompareService.Collect(At, badPath,
                kernelModules: () => new List<XinSpect.KernelModuleEntry>());
            Assert.Equal(FactAvailability.ReadError,
                Assert.Single(broken, f => f.Key == "byovd.rules").Availability);
        }
        finally { File.Delete(badPath); }
    }
}
