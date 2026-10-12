using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Authenticode 指紋快取的契約（效能修復：本機實測模組面 1651→16 ms、監聽埠面 1736→29 ms，
/// 真實 usermode 更新路徑中位 12,667→7,756 ms）。
/// <para>
/// 快取<b>以檔案指紋失效</b>（完整路徑＋大小＋最後寫入時間＋動作 GUID），不是以時間失效；
/// 命中時回傳值必須與不快取時逐字相同——快取只省重複的驗證，不改任何判定。
/// </para>
/// <para>
/// 斷言一律以<b>各測試自己的唯一暫存檔</b>查快取（<see cref="KernelModuleService.IsVerdictCached"/>），
/// 不看全域筆數，所以與其他測試類別平行跑也穩定。
/// </para>
/// </summary>
[Collection("SignatureVerdictCache")]
public class SignatureVerdictCacheTests
{
    private static string TempFile()
        => Path.Combine(Path.GetTempPath(), $"xinspect-sig-{Guid.NewGuid():N}.bin");

    [Fact]
    public void 同檔同動作第二次命中快取_指紋改變即失效並重驗()
    {
        KernelModuleService.ClearVerdictCache();
        string probe = TempFile();
        File.WriteAllText(probe, "not a signed binary");
        try
        {
            Assert.False(KernelModuleService.IsVerdictCached(probe));   // 還沒驗過

            var first = KernelModuleService.VerifyGenericAuthenticode(probe);
            Assert.True(KernelModuleService.IsVerdictCached(probe));    // 送驗後進快取
            Assert.False(first.Ok);                                     // 未簽章檔如實回「未通過」而非猜

            var second = KernelModuleService.VerifyGenericAuthenticode(probe);
            Assert.Equal(first, second);                                // 命中：逐字相同

            File.SetLastWriteTimeUtc(probe, DateTime.UtcNow.AddSeconds(37)); // 指紋改變
            Assert.False(KernelModuleService.IsVerdictCached(probe));   // 舊判定失效，必須重驗

            KernelModuleService.VerifyGenericAuthenticode(probe);
            Assert.True(KernelModuleService.IsVerdictCached(probe));
        }
        finally
        {
            File.Delete(probe);
            KernelModuleService.ClearVerdictCache();
        }
    }

    [Fact]
    public void 指紋改變後的重驗結果_與清空快取後的基準逐字相同()
    {
        KernelModuleService.ClearVerdictCache();
        string probe = TempFile();
        File.WriteAllText(probe, "v1");
        try
        {
            KernelModuleService.VerifyGenericAuthenticode(probe);
            File.WriteAllText(probe, "v2--不同的內容與大小");
            File.SetLastWriteTimeUtc(probe, DateTime.UtcNow.AddSeconds(11));
            var afterChange = KernelModuleService.VerifyGenericAuthenticode(probe);

            KernelModuleService.ClearVerdictCache();                     // 基準：完全不使用快取
            var fresh = KernelModuleService.VerifyGenericAuthenticode(probe);

            Assert.Equal(fresh, afterChange);                            // 快取絕不改變判定
        }
        finally
        {
            File.Delete(probe);
            KernelModuleService.ClearVerdictCache();
        }
    }

    [Fact]
    public void 診斷閘關閉時_完全不經快取且判定不變()
    {
        string probe = TempFile();
        File.WriteAllText(probe, "unsigned probe");
        try
        {
            Environment.SetEnvironmentVariable("XINSPECT_SIGCACHE", null);
            KernelModuleService.ClearVerdictCache();
            var cached = KernelModuleService.VerifyGenericAuthenticode(probe);
            Assert.True(KernelModuleService.IsVerdictCached(probe));

            Environment.SetEnvironmentVariable("XINSPECT_SIGCACHE", "off");
            KernelModuleService.ClearVerdictCache();
            var a = KernelModuleService.VerifyGenericAuthenticode(probe);
            var b = KernelModuleService.VerifyGenericAuthenticode(probe);
            Assert.False(KernelModuleService.IsVerdictCached(probe));    // 不讀也不寫

            Assert.Equal(cached, a);                                     // 關閉快取不改判定
            Assert.Equal(a, b);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XINSPECT_SIGCACHE", null);
            File.Delete(probe);
            KernelModuleService.ClearVerdictCache();
        }
    }

    [Fact]
    public void 不存在的檔案如實回讀不到_且不進快取()
    {
        KernelModuleService.ClearVerdictCache();
        string missing = TempFile();

        var verdict = KernelModuleService.VerifyGenericAuthenticode(missing);

        Assert.Null(verdict.Ok);
        Assert.Contains("不存在", verdict.Note);
        Assert.False(KernelModuleService.IsVerdictCached(missing)); // 沒有檔案就沒有指紋，不寫快取
    }

    [Fact]
    public void 已簽章的系統檔_命中快取且判定一致()
    {
        KernelModuleService.ClearVerdictCache();
        string signed = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        Assert.True(File.Exists(signed), "找不到系統檔可測——環境異常");

        var first = KernelModuleService.VerifyGenericAuthenticode(signed);
        Assert.True(first.Ok, $"系統檔應通過 Authenticode（實得 {first.Note}）");
        Assert.True(KernelModuleService.IsVerdictCached(signed));

        var second = KernelModuleService.VerifyGenericAuthenticode(signed);
        Assert.Equal(first, second);

        KernelModuleService.ClearVerdictCache();
    }
}
