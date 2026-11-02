using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>--fleet 只讀本機快照：有同型號第二台才給基線；沒有就回 2，不把「沒有可比」說成「沒有差異」。</summary>
public class FleetCliTests
{
    private static string Snapshot(string model, DateTimeOffset at, double sensor)
        => JsonSerializer.Serialize(new
        {
            anonymousMachineId = "machine-" + at.ToUnixTimeSeconds(),
            capturedAtUtc = at,
            facts = new object[]
            {
                new { key = "asset.identify", value = model },
                new { key = "sensor.cpu", value = "溫度", numericValue = sensor },
            },
        });

    [Fact]
    public void 兩份同型號快照_輸出族群基線且退出碼0()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinspect-fleet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), Snapshot("ACME X1", DateTimeOffset.Parse("2026-11-01T00:00:00Z"), 41));
            File.WriteAllText(Path.Combine(dir, "b.json"), Snapshot("ACME X1", DateTimeOffset.Parse("2026-11-02T00:00:00Z"), 43));

            var stdout = new StringWriter();
            int exit = CliService.Run(["--fleet", "--snapshots", dir], () => [], stdout, new StringWriter());

            Assert.Equal(CliService.ExitOk, exit);
            using var doc = JsonDocument.Parse(stdout.ToString());
            var root = doc.RootElement;
            Assert.Equal("fleet", root.GetProperty("scope").GetString());
            Assert.Equal(2, root.GetProperty("snapshotCount").GetInt32());
            Assert.True(root.GetProperty("comparable").GetBoolean());

            var keys = root.GetProperty("facts").EnumerateArray()
                .Select(f => f.GetProperty("key").GetString()).ToList();
            Assert.Contains("fleet.summary", keys);
            Assert.Contains("fleet.boundary", keys);
            Assert.Contains(keys, k => k!.StartsWith("fleet.peer.", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 沒有快照_退出碼2且如實說沒有可比的第二台()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xinspect-fleet-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stdout = new StringWriter();
            int exit = CliService.Run(["--fleet", "--snapshots", dir], () => [], stdout, new StringWriter());

            Assert.Equal(CliService.ExitPartial, exit);
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.False(doc.RootElement.GetProperty("comparable").GetBoolean());
            var summary = Assert.Single(doc.RootElement.GetProperty("facts").EnumerateArray(),
                f => f.GetProperty("key").GetString() == "fleet.summary");
            Assert.Equal("NotApplicable", summary.GetProperty("availability").GetString());
            Assert.Contains("沒有", summary.GetProperty("unavailableReason").GetString()!, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}