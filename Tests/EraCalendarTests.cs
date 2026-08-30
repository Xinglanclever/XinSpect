using Xunit;

namespace XinSpect.Tests;

/// <summary>紀年格式化：五種紀年的換算基準與自然排列格式。</summary>
public class EraCalendarTests
{
    private static readonly DateTime Sample = new(2026, 8, 28, 13, 5, 9);

    [Fact]
    public void Names_MatchEnumOrder()
    {
        // 下拉選單索引直接當作 EraMode 使用（MainViewModel.EraIndex），數量與順序必須對齊
        Assert.Equal(Enum.GetValues<EraMode>().Length, EraCalendar.Names.Length);
        // 使用者指定的順序：西元第一、民國第二、黃帝第三
        Assert.Equal(0, (int)EraMode.Gregorian);
        Assert.Equal(1, (int)EraMode.Minguo);
        Assert.Equal(2, (int)EraMode.Huangdi);
        Assert.Equal("西元紀年", EraCalendar.Names[(int)EraMode.Gregorian]);
        Assert.Equal("民國紀年", EraCalendar.Names[(int)EraMode.Minguo]);
        Assert.Equal("中華黃帝紀元", EraCalendar.Names[(int)EraMode.Huangdi]);
    }

    [Fact]
    public void Xuantong_名稱不冠大清而以括號標明朝代()
    {
        string name = EraCalendar.Names[(int)EraMode.Xuantong];
        Assert.Equal("宣統紀年〔大清〕", name);
        Assert.DoesNotContain("惡搞", name);
        Assert.False(name.StartsWith("大清", StringComparison.Ordinal));
    }

    [Fact]
    public void Gregorian_UsesNaturalOrder()
        => Assert.Equal("西元 2026 年 8 月 28 日  13:05:09", EraCalendar.Format(Sample, EraMode.Gregorian));

    [Theory]
    [InlineData(EraMode.Huangdi, "中華黃帝紀元 4724 年")]    // 2026 + 2698
    [InlineData(EraMode.Xuantong, "宣統 118 年")]            // 2026 − 1908
    [InlineData(EraMode.Minguo, "民國 115 年")]              // 2026 − 1911
    public void Eras_UseCorrectEpoch(EraMode mode, string expectedPrefix)
        => Assert.StartsWith(expectedPrefix, EraCalendar.Format(Sample, mode));

    [Fact]
    public void Doraemon_BeforeBirthYear_CountsDown()
        => Assert.StartsWith("哆啦A夢前 86 年", EraCalendar.Format(Sample, EraMode.Doraemon));

    [Fact]
    public void Doraemon_BirthYear_IsFirstYear()
        => Assert.StartsWith("哆啦A夢元年", EraCalendar.Format(new DateTime(2112, 1, 1), EraMode.Doraemon));

    [Fact]
    public void Doraemon_AfterBirthYear_CountsUp()
        => Assert.StartsWith("哆啦A夢 2 年", EraCalendar.Format(new DateTime(2113, 1, 1), EraMode.Doraemon));

    [Fact]
    public void AllModes_ContainTimeOfDay()
    {
        foreach (var mode in Enum.GetValues<EraMode>())
            Assert.Contains("13:05:09", EraCalendar.Format(Sample, mode));
    }

    // ── 舊設定檔遷移 ────────────────────────────────────────────────────────
    //
    // EraMode 的數值在 1.5.1 重排過，而 settings.json 存的是裸整數。不遷移的話，
    // 舊使用者選的「民國」(舊 3) 會靜默變成「宣統」(新 3)——設定沒壞但意思變了，
    // 比當掉更難察覺，所以這組測試把每一格舊編號都釘住。

    [Theory]
    [InlineData(0, EraMode.Gregorian)]
    [InlineData(1, EraMode.Huangdi)]
    [InlineData(2, EraMode.Xuantong)]
    [InlineData(3, EraMode.Minguo)]     // 關鍵那一格：舊 3 是民國，不是新 3 的宣統
    [InlineData(5, EraMode.Doraemon)]
    public void MigrateLegacyValue_PreservesTheUsersOriginalChoice(int legacy, EraMode expected)
        => Assert.Equal(expected, EraCalendar.MigrateLegacyValue(legacy));

    [Fact]
    public void MigrateLegacyValue_RemovedDahan_FallsBackToTheNearestAncientEra()
    {
        // 「大漢紀年」(舊 4) 已移除，無法照原意還原；退回同屬上古中國連續紀元的黃帝紀元，
        // 保住「使用者想要某種古代紀年」的意圖，而不是悄悄丟回西元。
        Assert.Equal(EraMode.Huangdi, EraCalendar.MigrateLegacyValue(4));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(99)]
    [InlineData(-1)]
    public void MigrateLegacyValue_OutOfRange_FallsBackToGregorianWithoutGuessing(int legacy)
        => Assert.Equal(EraMode.Gregorian, EraCalendar.MigrateLegacyValue(legacy));

    [Fact]
    public void MigrateLegacyValue_CoversEveryLegacyNumberInUse()
    {
        // 舊編號 0–5 全部要對到合法的現行紀年，不能有哪一格落到範圍外
        for (int legacy = 0; legacy <= 5; legacy++)
            Assert.Contains(EraCalendar.MigrateLegacyValue(legacy), Enum.GetValues<EraMode>());
    }

    // ── 讀入時的夾取 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, EraMode.Gregorian)]
    [InlineData(1, EraMode.Minguo)]
    [InlineData(4, EraMode.Doraemon)]
    public void Coerce_PassesValidValuesThrough(int value, EraMode expected)
        => Assert.Equal(expected, EraCalendar.Coerce(value));

    [Theory]
    [InlineData(5)]
    [InlineData(1000)]
    [InlineData(-3)]
    public void Coerce_OutOfRange_ReturnsGregorianInsteadOfThrowing(int value)
        => Assert.Equal(EraMode.Gregorian, EraCalendar.Coerce(value));

    [Fact]
    public void Coerce_AcceptsEverythingNamesCanDisplay()
    {
        // Coerce 的合法範圍就是 Names 的索引範圍；否則下拉選單會撞到範圍外
        for (int i = 0; i < EraCalendar.Names.Length; i++)
            Assert.Equal(i, (int)EraCalendar.Coerce(i));
        Assert.Equal(EraMode.Gregorian, EraCalendar.Coerce(EraCalendar.Names.Length));
    }

    [Fact]
    public void Migration_IsIdempotentOnceTheSchemaIsStamped()
    {
        // 遷移後必須回寫 SchemaVersion；否則下次啟動會把「已是新編號」的值再遷移一次，
        // 民國 → 宣統，每次啟動往後跳一格。這裡驗的是「第二次走的是 Coerce 而非 Migrate」。
        var once = EraCalendar.MigrateLegacyValue(3);            // 舊 3（民國）→ Minguo(1)
        var twice = EraCalendar.Coerce((int)once);               // 已標記結構版本 → 原值通過
        Assert.Equal(EraMode.Minguo, once);
        Assert.Equal(EraMode.Minguo, twice);

        // 反面對照：若忘記回寫版本、第二次仍走遷移，就會變成黃帝——這正是要避免的漂移
        Assert.NotEqual(once, EraCalendar.MigrateLegacyValue((int)once));
    }

    [Fact]
    public void SettingsSchema_IsStampedAtTheVersionThatRenumberedEras()
        => Assert.Equal(1, SettingsService.CurrentSchema);
}
