using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 合規基準比較：只讀兩份 JSON、只比判定；Unverifiable 的出現要算退步，不能當成沒事。
/// </summary>
public class ComplianceDiffTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 6, 30, 0, TimeSpan.Zero);

    private static string Report(
        (string Id, string Status)[] controls,
        (string Id, string Exposure)[]? attack = null)
        => JsonSerializer.Serialize(new
        {
            scope = "compliance",
            schemaVersion = 1,
            controls = controls.Select(c => new { id = c.Id, status = c.Status }),
            attack = (attack ?? []).Select(a => new { id = a.Id, exposure = a.Exposure }),
        });

    [Fact]
    public void 控制項判定變化_改善退步與證據消失都如實分類()
    {
        var before = Report(
        [
            ("A", "Satisfied"),
            ("B", "NotSatisfied"),
            ("C", "Unverifiable"),
            ("D", "Satisfied"),
            ("E", "Satisfied"),
        ]);
        var after = Report(
        [
            ("A", "NotSatisfied"),
            ("B", "Satisfied"),
            ("C", "Satisfied"),
            ("D", "Unverifiable"),
            ("F", "Satisfied"),
        ]);

        var diff = ComplianceDiffService.Compare(before, after);

        Assert.Equal(ComplianceChangeKind.Regressed, Assert.Single(diff.Controls, x => x.Id == "A").Kind);
        Assert.Equal(ComplianceChangeKind.Improved, Assert.Single(diff.Controls, x => x.Id == "B").Kind);
        Assert.Equal(ComplianceChangeKind.Improved, Assert.Single(diff.Controls, x => x.Id == "C").Kind);
        Assert.Equal(ComplianceChangeKind.Regressed, Assert.Single(diff.Controls, x => x.Id == "D").Kind);
        Assert.Equal(ComplianceChangeKind.Removed, Assert.Single(diff.Controls, x => x.Id == "E").Kind);
        Assert.Equal(ComplianceChangeKind.Added, Assert.Single(diff.Controls, x => x.Id == "F").Kind);
        Assert.Equal(5, diff.ControlsBefore);
        Assert.Equal(5, diff.ControlsAfter);
    }

    [Fact]
    public void ATTACK的Mitigated失去叫退步_不是普通變動()
    {
        var before = Report([], [("T1", "Mitigated"), ("T2", "Unknown"), ("T3", "Exposed")]);
        var after = Report([], [("T1", "Unknown"), ("T2", "Mitigated"), ("T3", "Mitigated")]);

        var diff = ComplianceDiffService.Compare(before, after);

        Assert.Equal(ComplianceChangeKind.Regressed, Assert.Single(diff.Techniques, x => x.Id == "T1").Kind);
        Assert.Equal(ComplianceChangeKind.Improved, Assert.Single(diff.Techniques, x => x.Id == "T2").Kind);
        Assert.Equal(ComplianceChangeKind.Improved, Assert.Single(diff.Techniques, x => x.Id == "T3").Kind);
    }

    [Fact]
    public void CLI比較兩份JSON_有差異回2且輸出只帶判定()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinspect-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string beforePath = Path.Combine(dir, "before.json");
        string afterPath = Path.Combine(dir, "after.json");
        File.WriteAllText(beforePath, Report([("NIST-800-147-WP", "Satisfied")]));
        File.WriteAllText(afterPath, Report([("NIST-800-147-WP", "Unverifiable")]));

        try
        {
            var stdout = new StringWriter();
            int exit = CliService.Run(["--compliance-diff", beforePath, afterPath], () => [],
                stdout, new StringWriter());

            Assert.Equal(CliService.ExitPartial, exit);
            using var doc = JsonDocument.Parse(stdout.ToString());
            var root = doc.RootElement;
            Assert.Equal("compliance-diff", root.GetProperty("scope").GetString());
            Assert.Equal(1, root.GetProperty("summary").GetProperty("changed").GetInt32());
            Assert.Equal(1, root.GetProperty("summary").GetProperty("controls").GetProperty("regressed").GetInt32());
            Assert.Equal("Unverifiable", root.GetProperty("controls")[0].GetProperty("after").GetString());
            Assert.DoesNotContain("SECRET", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CLI相同判定回0()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinspect-diff-same-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "same.json");
        File.WriteAllText(path, Report([("NIST-800-147-WP", "Satisfied")]));

        try
        {
            var stdout = new StringWriter();
            int exit = CliService.Run(["--compliance-diff", path, path], () => [],
                stdout, new StringWriter());

            Assert.Equal(CliService.ExitOk, exit);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(0, doc.RootElement.GetProperty("summary").GetProperty("changed").GetInt32());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 格式不認得回1且不輸出半份結果()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinspect-diff-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string good = Path.Combine(dir, "good.json");
        string bad = Path.Combine(dir, "bad.json");
        File.WriteAllText(good, Report([("A", "Satisfied")]));
        File.WriteAllText(bad, "{\"scope\":\"something-else\"}");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exit = CliService.Run(["--compliance-diff", good, bad], () => [],
                stdout, stderr);

            Assert.Equal(CliService.ExitError, exit);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("scope", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}