using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// managed 契約鏡像（Services/XsRegProbeContract.cs）與驅動端唯一真源（XsRegProbe/Driver/XrpContract.h）
/// 逐項鎖死：允許清單、範圍、IOCTL 號、魔法數、能力位元，任何一邊單獨改動這裡就紅——
/// 兩邊漂移時 usermode 會對帳失敗誤標驅動不符，等於把誠實資料弄丟。
/// </summary>
public sealed class XsRegProbeContractTests
{
    private static string Header() => ReadRepoFile(Path.Combine("XsRegProbe", "Driver", "XrpContract.h"));

    [Fact]
    public void 驅動標頭與managed契約鏡像一致()
    {
        string h = Header();

        Assert.Equal(XsRegProbeContract.MsrAllow, ParseHexList(h, "g_MsrAllow"));
        var (msrLo, msrHi) = ParseHexPairList(h, "g_MsrRangeAllow").Single();
        Assert.Equal(XsRegProbeContract.MsrRangeAllow, (Convert.ToUInt32(msrLo, 16), Convert.ToUInt32(msrHi, 16)));
        var mmio = ParseHexPairList(h, "g_MmioAllow")
            .Select(p => (Convert.ToUInt64(p.Lo, 16), Convert.ToUInt64(p.Hi, 16))).ToArray();
        Assert.Equal(XsRegProbeContract.MmioAllow, mmio);
    }

    [Fact]
    public void IOCTL號與能力位元兩邊一致()
    {
        string h = Header();
        uint deviceType = Convert.ToUInt32(Match(h, @"#define\s+XRP_DEVICE_TYPE\s+0x([0-9A-Fa-f]+)"), 16);

        // CTL_CODE(type, fn, METHOD_BUFFERED(0), FILE_READ_DATA(1)) = (type<<16)|(access<<14)|(fn<<2)
        uint Ctl(uint fn) => (deviceType << 16) | (1u << 14) | (fn << 2);

        Assert.Equal(XsRegProbeContract.IoctlQueryInfo, Ctl(0x800));
        Assert.Equal(XsRegProbeContract.IoctlReadMsrList, Ctl(0x801));
        Assert.Equal(XsRegProbeContract.IoctlReadMmio, Ctl(0x802));

        Assert.Equal(0x31505258u, Convert.ToUInt32(Match(h, @"#define\s+XRP_MAGIC\s+0x([0-9A-Fa-f]+)"), 16));
        Assert.Equal(1u, uint.Parse(Match(h, @"#define\s+XRP_IOCTL_VERSION\s+(\d+)"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0x1u, Convert.ToUInt32(Match(h, @"#define\s+XRP_FEATURE_MSR_READ\s+0x([0-9A-Fa-f]+)"), 16));
        Assert.Equal(0x2u, Convert.ToUInt32(Match(h, @"#define\s+XRP_FEATURE_MMIO_READ\s+0x([0-9A-Fa-f]+)"), 16));
        Assert.Equal(64, int.Parse(Match(h, @"#define\s+XRP_MSR_MAX_BATCH\s+(\d+)"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(4096, int.Parse(Match(h, @"#define\s+XRP_MMIO_MAX\s+(\d+)"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void 允許清單查驗_界內准界外拒()
    {
        Assert.True(XsRegProbeContract.MsrAllowed(0xCE));
        Assert.True(XsRegProbeContract.MsrAllowed(0x401)); // MCA 銀行範圍
        Assert.False(XsRegProbeContract.MsrAllowed(0x3B)); // 清單外

        Assert.True(XsRegProbeContract.MmioRangeAllowed(0xFED10004, 4)); // SPIBAR 內
        Assert.False(XsRegProbeContract.MmioRangeAllowed(0xFED10080, 16)); // 跨出 SPIBAR 允許端點
        Assert.False(XsRegProbeContract.MmioRangeAllowed(0xDEADBEE0, 4));
        Assert.False(XsRegProbeContract.MmioRangeAllowed(0xFED10000, 0));
    }

    private static string Match(string source, string pattern) =>
        Regex.Match(source, pattern).Groups[1].Value is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"標頭裡找不到 {pattern}");

    private static uint[] ParseHexList(string source, string tableName)
    {
        string body = TableBody(source, tableName);
        return Regex.Matches(body, @"0x([0-9A-Fa-f]+)[uU]?L?L?")
            .Select(m => Convert.ToUInt32(m.Groups[1].Value, 16)).ToArray();
    }

    private static (string Lo, string Hi)[] ParseHexPairList(string source, string tableName)
    {
        string body = TableBody(source, tableName);
        return Regex.Matches(body, @"\{\s*0x([0-9A-Fa-f]+)U?L?L?\s*,\s*0x([0-9A-Fa-f]+)U?L?L?\s*\}")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToArray();
    }

    private static string TableBody(string source, string tableName)
    {
        int start = source.IndexOf($"{tableName}[] = {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"標頭裡找不到 {tableName}");
        int end = source.IndexOf("};", start, StringComparison.Ordinal);
        return source[start..end];
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
                return File.ReadAllText(Path.Combine(dir.FullName, relativePath));
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到原始碼樹（往上找不到 XinSpect.csproj）");
    }
}
