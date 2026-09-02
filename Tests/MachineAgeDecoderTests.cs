using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 「這台機器多老了」的推估。
///
/// 沒有任何暫存器記著出廠日或購買日，所以這一份守的是<b>不許假裝知道</b>：
/// 三個線索（Windows 安裝日期、韌體建置日期、磁碟通電時數）各有偏誤方向，文字必須把偏誤講出來，
/// 一個都讀不到時就說推不出來，不給任何數字。
/// </summary>
public class MachineAgeDecoderTests
{
    private static readonly DateTime Now = new(2026, 9, 2);

    [Fact]
    public void 時間長度用年月表示_不足一個月說天數()
    {
        Assert.Equal("3 年 5 個月", MachineAgeDecoder.Duration(TimeSpan.FromDays(365.25 * 3 + 30.44 * 5 + 15)));
        Assert.Equal("2 年", MachineAgeDecoder.Duration(TimeSpan.FromDays(365.25 * 2 + 5)));
        Assert.Equal("7 個月", MachineAgeDecoder.Duration(TimeSpan.FromDays(30.44 * 7 + 10)));
        Assert.Equal("12 天", MachineAgeDecoder.Duration(TimeSpan.FromDays(12.5)));
        // 不得出現「0 年 0 個月」這種寫法
        Assert.DoesNotContain("0 年", MachineAgeDecoder.Duration(TimeSpan.FromDays(3)));
    }

    [Fact]
    public void 通電時數換算成全天候運轉的年數讓人有感()
    {
        string s = MachineAgeDecoder.HoursText(26_280);   // 三年 × 365 × 24
        Assert.Contains("26,280", s);
        Assert.Contains("3", s);
        Assert.Equal("讀不到", MachineAgeDecoder.HoursText(0));
    }

    [Fact]
    public void 三個線索都讀不到時不給任何數字()
    {
        var v = MachineAgeDecoder.Judge(new MachineAgeFacts { Now = Now });
        Assert.Contains("推不出來", v.Headline);
        Assert.Equal(Severity.Neutral, v.Severity);
        // headline 裡不得出現年月數字
        Assert.DoesNotContain("年", v.Headline);
    }

    [Fact]
    public void 取兩個日期裡較舊的那個當下界()
    {
        var v = MachineAgeDecoder.Judge(new MachineAgeFacts
        {
            Now = Now,
            WindowsInstall = new DateTime(2025, 1, 1),   // 1 年多
            BiosDate = new DateTime(2017, 6, 1),          // 9 年多 ← 應以這個為下界
        });
        Assert.Contains("9 年", v.Headline);
        Assert.Contains("至少", v.Headline);
    }

    [Fact]
    public void 安裝日期的偏誤要講出來()
    {
        var v = MachineAgeDecoder.Judge(new MachineAgeFacts { Now = Now, WindowsInstall = new DateTime(2024, 3, 1) });
        Assert.Contains("重裝", v.Detail);
        Assert.Contains("下界", v.Detail);
    }

    [Fact]
    public void 韌體日期不得被說成出廠日或購買日()
    {
        var v = MachineAgeDecoder.Judge(new MachineAgeFacts { Now = Now, BiosDate = new DateTime(2017, 6, 1) });
        Assert.Contains("不是出廠日", v.Detail);
        Assert.Contains("略高於", v.Detail);   // 偏誤方向要寫出來
    }

    [Fact]
    public void 磁碟通電時數要標明換過碟就不代表整機()
    {
        var v = MachineAgeDecoder.Judge(new MachineAgeFacts
        {
            Now = Now,
            WindowsInstall = new DateTime(2024, 1, 1),
            Disks = [new DiskAge("Samsung SSD 980 PRO", 12_000)],
        });
        Assert.Contains("980 PRO", v.Detail);
        Assert.Contains("換過碟", v.Detail);
    }

    [Fact]
    public void 通電時數很高時提醒備份()
    {
        var v = MachineAgeDecoder.Judge(new MachineAgeFacts
        {
            Now = Now,
            WindowsInstall = new DateTime(2018, 1, 1),
            Disks = [new DiskAge("老碟", 45_000)],
        });
        Assert.Equal(Severity.Warning, v.Severity);
        Assert.Contains("備份", v.Detail);
    }

    [Fact]
    public void 通電時數還沒讀時如實說還沒讀()
    {
        var v = MachineAgeDecoder.Judge(new MachineAgeFacts { Now = Now, WindowsInstall = new DateTime(2024, 1, 1) });
        Assert.Contains("還沒讀", v.Detail);
        Assert.Equal(Severity.Neutral, v.Severity);
    }

    [Fact]
    public void 結論必須說這是推估而不是查到的事實()
    {
        var v = MachineAgeDecoder.Judge(new MachineAgeFacts { Now = Now, BiosDate = new DateTime(2017, 6, 1) });
        Assert.Contains("推估", v.Detail);
        Assert.Contains("不是查到的事實", v.Detail);
    }

    [Fact]
    public void 未來的日期不採用_不得算出負的年齡()
    {
        var v = MachineAgeDecoder.Judge(new MachineAgeFacts
        {
            Now = Now,
            WindowsInstall = new DateTime(2030, 1, 1),   // 韌體時鐘壞掉會出現這種值
            BiosDate = new DateTime(2020, 1, 1),
        });
        Assert.Contains("6 年", v.Headline);             // 只採用 BIOS 那個
        Assert.Contains("讀不到 Windows 安裝日期", v.Detail);
    }
}
