using System.IO;
using System.Windows;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 軸三「AI 升級」的回歸測試：工具箱真的長到 34 項且名稱不重複、硬核工具在使用者尚未量測時
/// 必須說「尚未量測」而不是回一個 0、介面上寫的「34 項」要與工具箱實際數量一致，
/// 以及「保留對話」關閉時真的把檔案刪掉。
/// </summary>
/// <remarks>
/// 與 <see cref="UiSmokeTests"/> 同理，本類需要建構未呼叫 <c>Initialize()</c> 的 MainViewModel：
/// 工具箱是照著檢視模型的真實屬性形狀組出來的，用假物件驗不到「工具讀不到值時說了什麼」。
/// 不呼叫 Initialize() 表示 SensorService／LibreHardwareMonitor 不會建立，硬核單元也全未量測——
/// 這正好就是本測試要驗的那個狀態。
/// </remarks>
public class AiUpgradeTests
{
    /// <summary>硬核批次的 11 項工具名稱（AiToolboxBuilder.Hardcore.cs）。</summary>
    private static readonly string[] HardcoreTools =
    [
        "get_topdown_pipeline", "get_frequency_truth", "get_platform_trust", "get_firmware_versions",
        "get_core_time_breakdown", "get_power_policy", "get_memory_commit_truth", "get_machine_check",
        "get_cpu_security_bits", "get_rdt_monitoring", "get_performance_ceiling",
    ];

    /// <summary>介面文案宣稱的工具數；與 <see cref="ToolCount"/> 一起改，不容各說各話。</summary>
    private const int ToolCount = 34;

    /// <summary>在 STA 執行緒上跑一段需要 WPF 環境的工作，並把例外原樣帶回。</summary>
    private static void OnSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfEnv.Ensure();
                work();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "AI 工具箱測試逾時（3 分鐘未完成）。");
        if (error is not null) throw new Xunit.Sdk.XunitException("STA 執行緒中發生例外：" + error);
    }

    [Fact]
    public void 工具箱共34項且名稱不重複()
    {
        OnSta(() =>
        {
            var box = AiToolboxBuilder.Build(new MainViewModel());
            var names = box.Tools.Select(t => t.Name).ToList();
            Assert.Equal(ToolCount, names.Count);
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var t in box.Tools)
            {
                Assert.Matches("^[a-z0-9_]+$", t.Name);
                Assert.False(string.IsNullOrWhiteSpace(t.Description), $"{t.Name} 沒有給模型看的說明。");
            }
            foreach (string want in HardcoreTools)
                Assert.Contains(want, names);
        });
    }

    [Fact]
    public void 硬核工具在尚未量測時如實說沒量過而不回傳零()
    {
        OnSta(() =>
        {
            var box = AiToolboxBuilder.Build(new MainViewModel());
            foreach (string name in HardcoreTools)
            {
                string text = box.Invoke(name, "");
                Assert.False(string.IsNullOrWhiteSpace(text), $"{name} 回了空字串。");
                Assert.DoesNotContain("執行失敗", text);
                // 未呼叫 Initialize()，這些單元一定都沒量過：必須看到「尚未量測」這句實話，
                // 而不是把 0 或「—」當成量到的結果。
                Assert.Contains("尚未量測", text);
            }
        });
    }

    [Fact]
    public void 所有工具都能在未初始化狀態下安全執行()
    {
        OnSta(() =>
        {
            var box = AiToolboxBuilder.Build(new MainViewModel());
            foreach (var t in box.Tools)
            {
                string text = box.Invoke(t.Name, "");
                Assert.False(string.IsNullOrWhiteSpace(text), $"{t.Name} 回了空字串。");
                Assert.DoesNotContain("執行失敗", text);
            }
        });
    }

    [Fact]
    public void 介面文案宣稱的工具項數與工具箱實際數量一致()
    {
        string? root = RepoRoot();
        Assert.NotNull(root);       // 找不到原始碼樹就是測試環境有問題，不該靜靜跳過
        foreach (string rel in new[] { @"Views\AiView.xaml", @"Views\SettingsView.xaml" })
        {
            string text = File.ReadAllText(Path.Combine(root!, rel));
            Assert.Contains($"{ToolCount} 項", text);
            // 舊數字留在文案裡就是介面在說謊——例如工具加到 34 項了，文案還寫著 33 項。
            Assert.DoesNotContain("33 項本機唯讀", text);
            Assert.DoesNotContain("10 項是硬核", text);
        }
    }

    /// <summary>從測試輸出目錄往上找到含 Views\AiView.xaml 的原始碼根目錄。</summary>
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Views", "AiView.xaml"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void 關閉保留對話會立刻刪除對話檔()
    {
        var settings = new SettingsService();
        bool original = settings.AiKeepHistory;
        string folder = Path.Combine(Path.GetTempPath(), "XinSpectTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            settings.AiKeepHistory = true;
            var ai = new AiService(settings, folder);
            var store = new AiChatStore(folder);
            store.Save([new AiMessage { IsUser = true, Text = "測試訊息" }]);
            Assert.True(File.Exists(store.FilePath));

            // 設定頁寫著「取消勾選會立刻刪除該檔」，那就必須真的刪掉，不是只停止續寫。
            settings.AiKeepHistory = false;
            Assert.False(File.Exists(store.FilePath));
            Assert.NotNull(ai);
        }
        finally
        {
            settings.AiKeepHistory = original;      // 不留下對使用者設定檔的改動
            try { Directory.Delete(folder, true); } catch { /* 清不掉不影響結論 */ }
        }
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(404, true)]
    [InlineData(405, true)]
    [InlineData(422, true)]
    [InlineData(501, true)]
    [InlineData(401, false)]     // 金鑰錯誤：退回整段模式重試只是白等一輪
    [InlineData(403, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    public void 只有可能代表不支援串流的狀態碼才退回整段模式(int status, bool expected)
        => Assert.Equal(expected, AiService.MayRejectStreaming(status));

    [Fact]
    public void 同輪同名工具呼叫會拿到不同的合成識別碼()
    {
        // 端點沒給 id 時自己補的 id 必須帶序號，否則兩筆 tool 結果會對到同一次呼叫。
        string a = AiService.SyntheticId("", "get_temperatures", 0);
        string b = AiService.SyntheticId("", "get_temperatures", 1);
        Assert.NotEqual(a, b);
        // 端點有給 id 就用它的，不自作聰明覆寫。
        Assert.Equal("call_abc", AiService.SyntheticId("call_abc", "get_temperatures", 3));
    }

    [Fact]
    public void 歷史裁切會略過工具紀錄與失敗提示並回報裁掉幾則()
    {
        var all = new List<AiMessage>
        {
            new() { IsUser = true, Text = "第一問" },
            new() { IsUser = false, Text = "第一答" },
            new() { IsUser = false, IsTool = true, Text = "查詢 get_temperatures" },
            new() { IsUser = false, Text = "⚠ 呼叫失敗：連線逾時" },
            new() { IsUser = true, Text = "第二問" },
            new() { IsUser = false, Text = "第二答" },
            new() { IsUser = true, Text = "第三問" },
            new() { IsUser = false, Text = AiService.Placeholder },   // 最後一則是占位的回覆
        };

        var keep = AiService.SelectHistory(all, 0, out int dropped);
        Assert.Equal(0, dropped);
        Assert.Equal(["第一問", "第一答", "第二問", "第二答", "第三問"], keep.Select(m => m.Text));

        var trimmed = AiService.SelectHistory(all, 2, out int dropped2);
        Assert.Equal(3, dropped2);
        Assert.Equal(["第二答", "第三問"], trimmed.Select(m => m.Text));
    }
}
