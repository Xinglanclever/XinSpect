using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>媒體保護與空間音訊深化（MU-003／MU-006）單元測試。</summary>
public class MediaProtectionFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key)
        => facts.Single(f => f.Key == key);

    private static readonly IReadOnlyList<MediaProtectionFactsService.DrmComponent> TwoDrm =
    [
        new("PlayReady", "4.5.0.0"),
        new("Widevine", null),
    ];

    // ── MU-003 DRM 元件 ──────────────────────────────────────────────────

    [Fact]
    public void DRM_列出元件與版本()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At, drm: () => TwoDrm), "mu.drm.components");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("2 個元件", f.Value);
        Assert.Contains("PlayReady（4.5.0.0）", f.Value);
        Assert.Contains("Widevine", f.Value);
        Assert.Equal(2d, f.NumericValue);
    }

    [Fact]
    public void DRM_版本為零版號時只列名稱()
    {
        var rows = new[] { new MediaProtectionFactsService.DrmComponent("PlayReady", "0.0.0.0") };
        var f = Fact(MediaProtectionFactsService.Collect(At, drm: () => rows), "mu.drm.components");
        Assert.DoesNotContain("0.0.0.0", f.Value);
    }

    [Fact]
    public void DRM_元件存在不等於可播()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At, drm: () => TwoDrm), "mu.drm.components");
        Assert.Contains("元件存在不等於可以播", f.Value);
        Assert.Contains("授權伺服器可達", f.Value);
    }

    [Fact]
    public void DRM_不讀授權金鑰與內容識別碼()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At, drm: () => TwoDrm), "mu.drm.components");
        Assert.Contains("不讀授權、不讀金鑰、不讀內容識別碼", f.Value);
        Assert.Contains("權利資訊", f.Value);
    }

    [Fact]
    public void DRM_元件名稱不翻譯()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At, drm: () => TwoDrm), "mu.drm.components");
        Assert.Contains("刻意保留原文不翻譯", f.Value);
    }

    [Fact]
    public void DRM_零元件是不適用且說明可能仍能播()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At, drm: () => []), "mu.drm.components");
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有元件不等於不能播", f.UnavailableReason);
    }

    [Fact]
    public void DRM_讀取失敗是ReadError不當成沒有元件()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At, drm: () => null), "mu.drm.components");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不把讀不到讀成沒有元件", f.UnavailableReason);
    }

    [Fact]
    public void DRM_探針擲例外也是ReadError()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At,
            drm: () => throw new InvalidOperationException("x")), "mu.drm.components");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── MU-006 空間音訊格式 ───────────────────────────────────────────────

    [Fact]
    public void 空間音訊_列出格式與端點可用數()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At,
            spatial: () => new MediaProtectionFactsService.SpatialFormatsState(
                ["Windows Sonic", "Dolby Atmos"], 3, 2)), "mu.spatial.formats");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("已註冊格式 2 種", f.Value);
        Assert.Contains("Windows Sonic、Dolby Atmos", f.Value);
        Assert.Contains("端點 3 個", f.Value);
        Assert.Contains("其中 2 個至少有一種格式可用", f.Value);
        Assert.Equal(2d, f.NumericValue);
    }

    [Fact]
    public void 空間音訊_支援與使用中是兩件事()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At,
            spatial: () => new MediaProtectionFactsService.SpatialFormatsState(["A"], 1, 1)),
            "mu.spatial.formats");
        Assert.Contains("「支援格式」與「目前使用中」是兩件事", f.Value);
    }

    [Fact]
    public void 空間音訊_格式名稱不在地化()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At,
            spatial: () => new MediaProtectionFactsService.SpatialFormatsState(["Dolby Atmos"], 1, 1)),
            "mu.spatial.formats");
        Assert.Contains("格式名稱不在地化", f.Value);
    }

    [Fact]
    public void 空間音訊_零格式如實說不代表不支援()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At,
            spatial: () => new MediaProtectionFactsService.SpatialFormatsState([], 2, 0)),
            "mu.spatial.formats");
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("沒有回報任何空間音訊格式", f.Value);
        Assert.Contains("不代表不支援", f.Value);
    }

    [Fact]
    public void 空間音訊_沒有端點是不適用()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At,
            spatial: () => new MediaProtectionFactsService.SpatialFormatsState([], 0, 0)),
            "mu.spatial.formats");
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有端點不等於格式不支援", f.UnavailableReason);
    }

    [Fact]
    public void 空間音訊_不開裝置不測效果()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At,
            spatial: () => new MediaProtectionFactsService.SpatialFormatsState(["A"], 1, 1)),
            "mu.spatial.formats");
        Assert.Contains("不開音訊裝置、不測試空間效果、不判斷音質", f.Value);
    }

    [Fact]
    public void 空間音訊_讀取失敗是ReadError()
    {
        var f = Fact(MediaProtectionFactsService.Collect(At, spatial: () => null), "mu.spatial.formats");
        Assert.Equal(FactAvailability.ReadError, f.Availability);
    }

    // ── 契約 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 兩把鍵齊且互不影響()
    {
        var facts = MediaProtectionFactsService.Collect(At,
            drm: () => null,
            spatial: () => new MediaProtectionFactsService.SpatialFormatsState(["A"], 1, 1));
        Assert.Equal(new[] { "mu.drm.components", "mu.spatial.formats" },
            facts.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(FactAvailability.ReadError, Fact(facts, "mu.drm.components").Availability);
        Assert.Equal(FactAvailability.Present, Fact(facts, "mu.spatial.formats").Availability);
    }

    [Fact]
    public void 不適用與失敗都帶原因且值為空()
    {
        var facts = MediaProtectionFactsService.Collect(At, drm: () => [], spatial: () => null);
        foreach (var f in facts)
        {
            Assert.NotEqual(FactAvailability.Present, f.Availability);
            Assert.False(string.IsNullOrWhiteSpace(f.UnavailableReason), $"{f.Key} 缺原因");
            Assert.Equal("", f.Value);
        }
    }
}
