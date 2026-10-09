using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// HeatScale 系算繪快照守門（v2.53，docs/PROGRAM-ULTIMATE-2026-10-10.md §5.7）。
///
/// <para>
/// 熱區圖、逐核液柱、空板誠實路徑——這三個畫面是「全站同一份色階」的實地展示。
/// 既有測試驗的是資料契約與繫結不炸；但「顏色悄悄換了」「圖例長歪了」「格子偏移」
/// 這類<b>像素級</b>回歸只有把東西真的畫出來比對才抓得到。本檔把三個場景算繪成
/// 固定尺寸點陣圖（MILSW 軟體光柵化，不經 D3D），對 PNG 位元組取 SHA-256，
/// 與倉庫內 <c>Tests/RenderSnapshots.baseline.json</c> 逐案比對。
/// </para>
/// <para>
/// <b>輸入全凍結：</b>固定假資料（含一格無讀值走「—」）、固定佈景（深色＋藍色強調，
/// 不隨先前測試殘留）、<see cref="Motion.Suspend"/> 停動畫、固定版面尺寸。
/// 快照基線與繪製機器綁定（字型版本、WPF 光柵器版本改變都會反映成像素差）——
/// 換機或升級後紅燈時，用 <c>XINSPECT_RENDER_SNAPSHOT_UPDATE=1</c> 重生成基線，
/// 親眼確認新畫面無異議後把基線檔一起 commit，這是「故意更新」而非放寬守門。
/// </para>
/// </summary>
[Collection(WpfCollection.Name)]
public class HeatScaleRenderSnapshotTests
{
    public const string BaselineRelativePath = "Tests/RenderSnapshots.baseline.json";
    public const string UpdateEnvVar = "XINSPECT_RENDER_SNAPSHOT_UPDATE";

    /// <summary>固定假核心：溫度覆蓋色階各段（41→綠、83→紅），#6 無讀值走誠實空格，物理 8 核補兩格。</summary>
    private static CoreRow[] FixedRows() =>
    [
        new("核心 #1") { TempC = 41, LoadPercent = 55 },
        new("核心 #2") { TempC = 57, LoadPercent = 88 },
        new("核心 #3") { TempC = 68.5, LoadPercent = 30 },
        new("核心 #4") { TempC = 74, LoadPercent = 12 },
        new("核心 #5") { TempC = 83, LoadPercent = 95 },
        new("核心 #6") { TempC = null, LoadPercent = 0 },
    ];

    [Fact]
    public void 熱區圖快照與基線一致() => RunCase("core-heatmap", () =>
    {
        var map = new CoreHeatmap { Cores = FixedRows(), PhysicalCores = 8 };
        return RenderSha(map, 640, 430);
    });

    [Fact]
    public void 逐核液柱快照與基線一致() => RunCase("core-columns", () =>
    {
        var columns = new CoreColumns { Cores = FixedRows(), Width = 640, Height = 200 };
        return RenderSha(columns, 640, 200);
    });

    [Fact]
    public void 空板誠實路徑快照與基線一致() => RunCase("core-empty", () =>
    {
        var map = new CoreHeatmap { Cores = null, PhysicalCores = 0 };
        return RenderSha(map, 640, 430);
    });

    [Fact]
    public void 基線檔必須收錄全部案例且為十六進位摘要()
    {
        var doc = LoadBaseline();
        Assert.NotNull(doc);
        var cases = doc!.RootElement.GetProperty("cases");
        foreach (string key in new[] { "core-heatmap", "core-columns", "core-empty" })
        {
            Assert.True(cases.TryGetProperty(key, out var el), $"基線缺少案例 {key}——用 {UpdateEnvVar}=1 重生成。");
            string sha = el.GetString() ?? "";
            Assert.True(sha.Length == 64 && sha.All(Uri.IsHexDigit), $"{key} 的基線不是 64 字元十六進位摘要");
        }
    }

    // ── 執行機制 ──────────────────────────────────────────────────────────────

    private static void RunCase(string key, Func<string> compute)
    {
        var problems = new List<string>();
        string actual = "";
        var thread = new Thread(() =>
        {
            try
            {
                WpfEnv.Ensure();
                ThemeService.Initialize();
                ThemeService.Theme = AppTheme.Dark;          // 不隨其他 WPF 測試殘留的外觀
                ThemeService.Accent = ThemeService.FindAccent("blue");
                using var _ = Motion.Suspend();              // 停動畫：快照沒有毫秒與氣泡的位置
                actual = compute();
            }
            catch (Exception ex) { problems.Add(ex.ToString()); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "算繪逾時");
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        if (Environment.GetEnvironmentVariable(UpdateEnvVar) == "1")
        {
            UpdateBaseline(key, actual);
            return;   // 故意更新：重生成即如實寫回，不假裝比對通過
        }

        var doc = LoadBaseline();
        Assert.True(doc is not null,
            $"基線檔 {BaselineRelativePath} 不存在——先設 {UpdateEnvVar}=1 跑一次重生成，確認畫面無異議後 commit。");
        Assert.True(doc!.RootElement.GetProperty("cases").TryGetProperty(key, out var el),
            $"基線缺少案例 {key}——設 {UpdateEnvVar}=1 重生成。");
        string expected = el.GetString() ?? "";
        Assert.True(expected == actual,
            $"{key} 的算繪與基線不同了（actual {actual} ≠ baseline {expected}）。\n"
            + "若是故意改視覺：設 " + UpdateEnvVar + "=1 重跑、親眼看過新畫面，再 commit 更新後的基線檔。\n"
            + "若不是故意的：這正是守門要抓的靜默像素回歸，查 HeatScale／熱區圖／液柱／佈景這一路。");
    }

    private static string RenderSha(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
    }

    private static string RepoFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(dir.FullName + "/XinSpect.csproj"))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Tests", "RenderSnapshots.baseline.json");
    }

    private static JsonDocument? LoadBaseline()
    {
        string path = RepoFile();
        if (!File.Exists(path)) return null;
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static void UpdateBaseline(string key, string sha)
    {
        string path = RepoFile();
        var root = File.Exists(path)
            ? JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(path))
            : new SnapshotFile();
        root.Cases[key] = sha;
        root.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        File.WriteAllText(path, JsonSerializer.Serialize(root,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class SnapshotFile
    {
        [JsonPropertyName("note")] public string Note { get; set; } = "HeatScale 系算繪快照基線；更新機制見 HeatScaleRenderSnapshotTests。";
        [JsonPropertyName("updatedUtc")] public string UpdatedUtc { get; set; } = "";
        [JsonPropertyName("cases")] public Dictionary<string, string> Cases { get; set; } = [];
    }
}
