using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// FS-013 隱私禁項的<b>機器釘住</b>： jump lists／MRU／縮圖快取這類「使用者做了什麼」的來源，
/// 本專案明文不讀——而「明文不讀」不能只是一句宣稱，要掃得到。
/// 這條測試掃 Services/ 全部原始碼，任何一支服務出現禁項路徑就是紅燈。
/// </summary>
public class PrivacyDenylistTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>禁項宣告檔本身當字典用，不算违规（它只是把不讀的東西寫成清單）。</summary>
    private static readonly string DeclaringFile = "EnvironmentRuntimeFactsService.cs";

    [Fact]
    public void 禁項清單不為空且每條都有理由()
    {
        Assert.True(EnvironmentRuntimeFactsService.ForbiddenSourcePatterns.Count >= 5,
            "禁項清單縮到 5 條以下——要嘛是來源寫法改了要更新，要嘛是禁項被放寬（兩者都該被看見）");
        Assert.All(EnvironmentRuntimeFactsService.ForbiddenSourcePatterns,
            p => Assert.False(string.IsNullOrWhiteSpace(p)));
    }

    [Fact]
    public void Services_原始碼裡沒有任何禁項路徑()
    {
        var services = Path.Combine(RepoRoot(), "Services");
        var hits = new List<string>();
        foreach (string file in Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (Path.GetFileName(file).Equals(DeclaringFile, StringComparison.Ordinal)) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }

            foreach (var pattern in EnvironmentRuntimeFactsService.ForbiddenSourcePatterns)
            {
                if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    hits.Add($"{Path.GetRelativePath(RepoRoot(), file)}：{pattern}");
            }
        }

        Assert.True(hits.Count == 0,
            "以下服務讀了明文禁止的使用者行為來源（jump lists／MRU／縮圖／URL 快取／UserAssist）——" +
            "這些與「這台機器的硬體與系統事實」無關，讀了就變監視工具：" + string.Join("、", hits));
    }

    [Fact]
    public void 禁項事實會把清單攤在畫面上而不是只藏在程式碼()
    {
        var at = DateTimeOffset.UtcNow;
        var facts = EnvironmentRuntimeFactsService.Collect(at,
            registry: _ => null,
            wakeRunner: _ => "",
            pool: () => (1, 1, 1, 0, 0),
            gc: () => (false, "Interactive", 0, 0, 0, 1));
        var boundary = facts.Single(f => f.Key == EnvironmentRuntimeFactsService.PrivacyKey);

        Assert.Equal(FactAvailability.Present, boundary.Availability);
        foreach (var pattern in EnvironmentRuntimeFactsService.ForbiddenSourcePatterns)
            Assert.Contains(pattern, boundary.Value, StringComparison.Ordinal);
    }
}