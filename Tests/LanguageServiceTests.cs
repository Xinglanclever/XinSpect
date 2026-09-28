using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 繁簡逐字轉換的健壯性守則。claim 8 的 NUL 疑點經查證不成立（cchSrc 傳的是明確長度、非 -1，
/// 故映射 1:1、無結尾 NUL），但轉換已改用「實際寫入長度」建字串；這兩條把該行為釘住，
/// 日後若有人把 cchSrc 改成 -1 或改回 new string(buf) 會立刻紅。
/// </summary>
public class LanguageServiceTests
{
    [Fact]
    public void ToSimplified不夾帶結尾NUL字元()
    {
        string s = LanguageService.ToSimplified("記憶體與快取的效能");
        Assert.DoesNotContain('\0', s);
        Assert.Equal(s.Length, s.TrimEnd('\0').Length);
    }

    [Fact]
    public void ToTraditional不夾帶結尾NUL字元()
    {
        string s = LanguageService.ToTraditional("内存与缓存的性能");
        Assert.DoesNotContain('\0', s);
    }
}
