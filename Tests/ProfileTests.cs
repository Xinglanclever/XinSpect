using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 場景設定檔的純邏輯測試：場景清單形狀、歸算（Resolve）後的實際動作、自訂欄位的夾限與
/// scenes.json 的落地與還原。不呼叫 ApplyAsync——那會真的切換 Windows 電源計劃並寫入硬體。
/// </summary>
public sealed class ProfileTests : IDisposable
{
    private readonly string _dir;

    public ProfileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "XinSpectTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 暫存目錄清不掉不影響測試結果 */ }
    }

    private ProfileService New() => new(_dir);

    private static Scene Get(ProfileService svc, string key) => svc.Scenes.First(s => s.Key == key);

    // 手寫一份落地檔，模擬「上次關機前存下的設定」。
    private static void WriteSceneFile(string dir, string active, object? custom = null)
    {
        var payload = custom ?? new
        {
            Active = active,
            CustomFan = true, CustomFanPreset = 2,
            CustomPlan = true, CustomPlanIndex = 3,
            CustomGpu = true, CustomGpuPower = 115.0, CustomGpuTemp = 90.0,
        };
        File.WriteAllText(Path.Combine(dir, "scenes.json"), JsonSerializer.Serialize(payload));
    }

    // ── 場景清單 ────────────────────────────────────────────────────────────

    [Fact]
    public void Scenes_AreFourInFixedOrder()
    {
        var svc = New();
        Assert.Equal(["quiet", "balanced", "performance", "custom"], svc.Scenes.Select(s => s.Key));
        Assert.Equal(["靜音", "均衡", "效能", "自訂"], svc.Scenes.Select(s => s.Name));
    }

    [Fact]
    public void Scenes_OnlyLastIsCustom()
    {
        var svc = New();
        Assert.Single(svc.Scenes, s => s.IsCustom);
        Assert.True(svc.Scenes[^1].IsCustom);
    }

    [Fact]
    public void Scenes_BuiltInsCarryIconSummaryAndDetails()
    {
        foreach (var s in New().Scenes)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.IconData));
            Assert.False(string.IsNullOrWhiteSpace(s.Summary));
            if (!s.IsCustom) Assert.Equal(3, s.Details.Count);   // 風扇／電源計劃／顯示卡各一行
        }
    }

    [Fact]
    public void ActionText_FollowsIsActive()
    {
        var s = Get(New(), "quiet");
        Assert.Equal("套用此場景", s.ActionText);
        Assert.False(s.IsActive);
    }

    [Fact]
    public void FreshService_ReportsNothingApplied()
    {
        var svc = New();
        Assert.Equal("", svc.ActiveKey);
        Assert.Equal("未套用", svc.ActiveName);
        Assert.True(svc.NotBusy);
        Assert.Contains("尚未套用", svc.StatusText);
    }

    // ── 電源計劃對照表 ──────────────────────────────────────────────────────

    [Fact]
    public void PowerPlanTable_IsAlignedAndValid()
    {
        Assert.Equal(ProfileService.PowerPlanNames.Length, ProfileService.PowerPlanGuids.Length);
        foreach (string g in ProfileService.PowerPlanGuids) Assert.True(Guid.TryParse(g, out _));
        Assert.Equal(["節能", "平衡", "高效能", "最佳效能"], ProfileService.PowerPlanNames);
    }

    // ── 歸算（內建場景）──────────────────────────────────────────────────────

    [Fact]
    public void Resolve_QuietTurnsEverythingDown()
    {
        var svc = New();
        var a = svc.Resolve(Get(svc, "quiet"));
        Assert.Equal(0, a.FanPreset);                       // 靜音樣板
        Assert.True(a.EnableFanCurves);
        Assert.Equal("節能", a.PowerPlanName);
        Assert.Equal(85, a.GpuPowerPercent);
        Assert.Equal(75, a.GpuTempLimitC);
    }

    [Fact]
    public void Resolve_PerformanceRaisesLimits()
    {
        var svc = New();
        var a = svc.Resolve(Get(svc, "performance"));
        Assert.Equal(2, a.FanPreset);
        Assert.Equal("高效能", a.PowerPlanName);
        Assert.Equal(110, a.GpuPowerPercent);
        Assert.Equal(88, a.GpuTempLimitC);
    }

    [Fact]
    public void Resolve_BuiltInsEscalateMonotonically()
    {
        var svc = New();
        var q = svc.Resolve(Get(svc, "quiet"));
        var b = svc.Resolve(Get(svc, "balanced"));
        var p = svc.Resolve(Get(svc, "performance"));

        Assert.True(q.FanPreset < b.FanPreset && b.FanPreset < p.FanPreset);
        Assert.True(q.GpuPowerPercent < b.GpuPowerPercent && b.GpuPowerPercent < p.GpuPowerPercent);
        Assert.True(q.GpuTempLimitC < b.GpuTempLimitC && b.GpuTempLimitC < p.GpuTempLimitC);
    }

    [Fact]
    public void Resolve_BuiltInsCarryDistinctPlanGuids()
    {
        var svc = New();
        var guids = new[] { "quiet", "balanced", "performance" }
            .Select(k => svc.Resolve(Get(svc, k)).PowerPlanGuid)
            .ToList();
        Assert.All(guids, g => Assert.False(string.IsNullOrEmpty(g)));
        Assert.Equal(3, guids.Distinct().Count());
    }

    // ── 歸算（自訂場景）──────────────────────────────────────────────────────

    [Fact]
    public void Resolve_CustomFollowsPickedValues()
    {
        var svc = New();
        svc.Custom.ApplyFan = true; svc.Custom.FanPreset = 2;
        svc.Custom.ApplyPowerPlan = true; svc.Custom.PowerPlanIndex = 0;
        svc.Custom.ApplyGpu = true; svc.Custom.GpuPowerPercent = 120; svc.Custom.GpuTempLimitC = 90;

        var a = svc.Resolve(Get(svc, "custom"));
        Assert.Equal(2, a.FanPreset);
        Assert.True(a.EnableFanCurves);
        Assert.Equal(ProfileService.PowerPlanGuids[0], a.PowerPlanGuid);
        Assert.Equal("節能", a.PowerPlanName);
        Assert.Equal(120, a.GpuPowerPercent);
        Assert.Equal(90, a.GpuTempLimitC);
    }

    [Fact]
    public void Resolve_CustomLeavesUncheckedPartsAlone()
    {
        var svc = New();
        svc.Custom.ApplyFan = false;
        svc.Custom.ApplyPowerPlan = false;
        svc.Custom.ApplyGpu = false;

        var a = svc.Resolve(Get(svc, "custom"));
        Assert.Null(a.FanPreset);            // null 表示這部分完全不動
        Assert.False(a.EnableFanCurves);
        Assert.Null(a.PowerPlanGuid);
        Assert.Null(a.GpuPowerPercent);
        Assert.Null(a.GpuTempLimitC);
    }

    [Fact]
    public void Resolve_CustomKeepsPlanNameEvenWhenUnchecked()
    {
        var svc = New();
        svc.Custom.PowerPlanIndex = 2;
        svc.Custom.ApplyPowerPlan = false;

        var a = svc.Resolve(Get(svc, "custom"));
        Assert.Null(a.PowerPlanGuid);        // 不切換
        Assert.Equal("高效能", a.PowerPlanName);   // 但名稱仍可用於說明文字
    }

    // ── 自訂欄位的夾限 ──────────────────────────────────────────────────────

    [Fact]
    public void Custom_ClampsPresetAndPlanIndex()
    {
        var c = new CustomScene { FanPreset = 9 };
        Assert.Equal(2, c.FanPreset);
        c.FanPreset = -4;
        Assert.Equal(0, c.FanPreset);

        c.PowerPlanIndex = 99;
        Assert.Equal(ProfileService.PowerPlanNames.Length - 1, c.PowerPlanIndex);
        c.PowerPlanIndex = -1;
        Assert.Equal(0, c.PowerPlanIndex);
    }

    [Fact]
    public void Custom_ClampsAndRoundsGpuLimits()
    {
        var c = new CustomScene { GpuPowerPercent = 240 };
        Assert.Equal(130, c.GpuPowerPercent);
        c.GpuPowerPercent = 10;
        Assert.Equal(50, c.GpuPowerPercent);
        c.GpuPowerPercent = 97.6;
        Assert.Equal(98, c.GpuPowerPercent);          // 四捨五入為整數

        c.GpuTempLimitC = 120;
        Assert.Equal(93, c.GpuTempLimitC);
        c.GpuTempLimitC = 30;
        Assert.Equal(65, c.GpuTempLimitC);
    }

    [Fact]
    public void Custom_DefaultsAreConservative()
    {
        var c = new CustomScene();
        Assert.True(c.ApplyFan);
        Assert.Equal(1, c.FanPreset);                 // 均衡
        Assert.True(c.ApplyPowerPlan);
        Assert.Equal(1, c.PowerPlanIndex);            // 平衡
        Assert.False(c.ApplyGpu);                     // 顯示卡預設不碰
        Assert.Equal(100, c.GpuPowerPercent);
        Assert.Equal(83, c.GpuTempLimitC);
    }

    [Fact]
    public void Custom_ChangedFiresOnlyOnRealChange()
    {
        var c = new CustomScene();
        int hits = 0;
        c.Changed += () => hits++;

        c.GpuPowerPercent = 110;
        Assert.Equal(1, hits);
        c.GpuPowerPercent = 110;                      // 同值不得再觸發（拖曳時避免存檔風暴）
        Assert.Equal(1, hits);
        c.GpuPowerPercent = 110.4;                    // 四捨五入後同值亦不觸發
        Assert.Equal(1, hits);
        c.ApplyGpu = true;
        Assert.Equal(2, hits);
    }

    // ── 落地 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Persist_CustomFieldsSurviveNewInstance()
    {
        var a = New();
        a.Custom.ApplyFan = false;
        a.Custom.FanPreset = 2;
        a.Custom.PowerPlanIndex = 3;
        a.Custom.ApplyGpu = true;
        a.Custom.GpuPowerPercent = 118;
        a.Custom.GpuTempLimitC = 91;

        var b = New();
        Assert.False(b.Custom.ApplyFan);
        Assert.Equal(2, b.Custom.FanPreset);
        Assert.Equal(3, b.Custom.PowerPlanIndex);
        Assert.True(b.Custom.ApplyGpu);
        Assert.Equal(118, b.Custom.GpuPowerPercent);
        Assert.Equal(91, b.Custom.GpuTempLimitC);
    }

    [Fact]
    public void Persist_ActiveSceneIsRestoredAndMarked()
    {
        WriteSceneFile(_dir, "performance");
        var svc = New();

        Assert.Equal("performance", svc.ActiveKey);
        Assert.Equal("效能", svc.ActiveName);
        Assert.True(Get(svc, "performance").IsActive);
        Assert.Equal("使用中", Get(svc, "performance").ActionText);
        Assert.False(Get(svc, "quiet").IsActive);
        Assert.Contains("上次使用的場景", svc.StatusText);
    }

    [Fact]
    public void Persist_UnknownActiveKeyIsIgnored()
    {
        WriteSceneFile(_dir, "no-such-scene");
        var svc = New();

        Assert.Equal("", svc.ActiveKey);
        Assert.Equal("未套用", svc.ActiveName);
        Assert.DoesNotContain(svc.Scenes, s => s.IsActive);
        Assert.Equal(2, svc.Custom.FanPreset);        // 其餘欄位仍照檔案還原
    }

    [Fact]
    public void Persist_LoadDoesNotRewriteFile()
    {
        WriteSceneFile(_dir, "quiet");
        string path = Path.Combine(_dir, "scenes.json");
        string before = File.ReadAllText(path);

        _ = New();                                    // 載入期間的 _loading 旗標須抑制存檔
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Persist_IgnoresCorruptFile()
    {
        File.WriteAllText(Path.Combine(_dir, "scenes.json"), "{ 這不是 JSON");
        var svc = New();                              // 壞檔視為沒有設定，不得拋出

        Assert.Equal(4, svc.Scenes.Count);
        Assert.Equal("", svc.ActiveKey);
        Assert.Equal(1, svc.Custom.FanPreset);        // 回到預設
    }

    [Fact]
    public void Persist_OutOfRangeValuesInFileAreClamped()
    {
        WriteSceneFile(_dir, "quiet", new
        {
            Active = "quiet",
            CustomFan = true, CustomFanPreset = 7,
            CustomPlan = true, CustomPlanIndex = 42,
            CustomGpu = true, CustomGpuPower = 900.0, CustomGpuTemp = 5.0,
        });
        var svc = New();

        Assert.Equal(2, svc.Custom.FanPreset);
        Assert.Equal(ProfileService.PowerPlanNames.Length - 1, svc.Custom.PowerPlanIndex);
        Assert.Equal(130, svc.Custom.GpuPowerPercent);
        Assert.Equal(65, svc.Custom.GpuTempLimitC);
    }

    [Fact]
    public void Persist_WorksWithoutFanOrGpuServices()
    {
        var svc = New();                              // Fans／Gpu 皆為 null：歸算與落地仍須可用
        svc.Custom.ApplyGpu = true;
        var a = svc.Resolve(Get(svc, "custom"));
        Assert.NotNull(a.GpuPowerPercent);
        Assert.Null(svc.Fans);
        Assert.Null(svc.Gpu);
    }
}
