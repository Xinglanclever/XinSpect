using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

public sealed class AgentBridgeCliTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"xin-agent-bridge-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static HardwareFact Fact(string key, string value, bool sensitive = false) =>
        new(key, "測試", key, value, "", "測試來源", FactTrustLevel.Measured, sensitive, At);

    [Fact]
    public void Capabilities標示版本唯讀範圍與完整快照缺口且不啟動收集()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = CliService.Run([CliService.AgentBridgeArg, "capabilities"],
            () => throw new InvalidOperationException("capabilities 不應收集硬體"), stdout, stderr);

        using var json = JsonDocument.Parse(stdout.ToString());
        var root = json.RootElement;
        Assert.Equal(CliService.ExitOk, code);
        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(HardwareSnapshotSchema.CurrentVersion, root.GetProperty("snapshotSchemaVersion").GetInt32());
        Assert.True(root.GetProperty("readOnly").GetBoolean());
        Assert.False(root.GetProperty("mutationsExposed").GetBoolean());
        Assert.False(root.GetProperty("persistentWritesExposed").GetBoolean());
        Assert.True(root.GetProperty("transientHardwareWritesPossible").GetBoolean());
        Assert.False(root.GetProperty("completeDesktopSnapshot").GetBoolean());
        Assert.Contains("cli-evidence", root.GetProperty("supportedScopes").EnumerateArray().Select(x => x.GetString()));
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void Snapshot沿用既有schema並遮蔽敏感值()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = CliService.Run([CliService.AgentBridgeArg, "collect_snapshot"],
            () => [Fact("board.model", "Board X"), Fact("board.serial", "SECRET-123", sensitive: true)], stdout, stderr);

        using var json = JsonDocument.Parse(stdout.ToString());
        var root = json.RootElement;
        Assert.Equal(CliService.ExitOk, code);
        Assert.Equal(HardwareSnapshotSchema.CurrentVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(root.GetProperty("sensitiveValuesPreserved").GetBoolean());
        Assert.DoesNotContain("SECRET-123", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal("測試來源", root.GetProperty("facts")[0].GetProperty("source").GetString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void GetFact只輸出要求的精確鍵()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = CliService.Run([CliService.AgentBridgeArg, "get_fact", "platform.hvci"], () =>
        [
            Fact("platform.hvci", "已啟用"),
            Fact("platform.testsigning", "已停用"),
        ], stdout, stderr);

        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(CliService.ExitOk, code);
        Assert.Equal("platform.hvci", json.RootElement.GetProperty("facts")[0].GetProperty("key").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("facts").GetArrayLength());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void GetFact查無鍵時明確回報而不假裝沒有硬體()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = CliService.Run([CliService.AgentBridgeArg, "get_fact", "gpu.model"], () => [], stdout, stderr);

        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(CliService.ExitPartial, code);
        Assert.Equal("NotSupported", json.RootElement.GetProperty("availability").GetString());
        Assert.Contains("沒有此 key", json.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void VerifySnapshot使用標準schema與完整性驗證()
    {
        var snapshot = HardwareSnapshotService.Create(
            [new HardwareSnapshotFact
            {
                Key = "board.model", Category = "主機板", Name = "型號", Value = "Board X",
                Source = "測試", Trust = FactTrustLevel.Reported, Sensitive = false, MeasuredAtUtc = At,
            }], ["test-machine"], At, AppInfo.Version);
        HardwareSnapshotService.Save(_path, snapshot);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = CliService.Run([CliService.AgentBridgeArg, "verify_snapshot", _path], () => [], stdout, stderr);

        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(CliService.ExitOk, code);
        Assert.True(json.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(HardwareSnapshotSchema.CurrentVersion, json.RootElement.GetProperty("snapshotSchemaVersion").GetInt32());
        Assert.Empty(stderr.ToString());
    }
}
