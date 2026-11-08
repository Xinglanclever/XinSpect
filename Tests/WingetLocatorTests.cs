using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// patch-36：winget 絕對路徑解析的守門——契約只有一句：
/// 回 null 或「存在的絕對路徑」，絕不回裸檔名（裸檔名交給 CreateProcess＝CWD 優先搜尋面）。
/// </summary>
public class WingetLocatorTests
{
    [Fact]
    public void Resolve_回null或存在的絕對路徑_絕不回裸檔名()
    {
        WingetLocator.Reset();
        var path = WingetLocator.Resolve();
        if (path is null) return;   // 沒裝 winget 的機器：如實 null，不是「winget」

        Assert.True(System.IO.Path.IsPathRooted(path));
        Assert.False(string.Equals(path, "winget.exe", StringComparison.OrdinalIgnoreCase));
        Assert.True(System.IO.File.Exists(path));   // 釘扎的檔案必須真的存在（含 0-byte 執行別名）
    }

    [Fact]
    public void Reset_後重新解析_釘扎不殘留()
    {
        var first = WingetLocator.Resolve();
        WingetLocator.Reset();
        var second = WingetLocator.Resolve();
        Assert.Equal(first, second);   // 同一環境兩次掃描結果一致（釘扎只是快取，不是狀態源）
    }
}
