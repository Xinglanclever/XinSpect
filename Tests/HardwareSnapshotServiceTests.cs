using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

public sealed class HardwareSnapshotServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "XinSpect-snapshot-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _measured = new(2026, 9, 8, 1, 2, 3, TimeSpan.Zero);

    public HardwareSnapshotServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void 保存載入_RoundTrip且同內容序列化確定()
    {
        var snapshot = Snapshot([
            Fact("storage.disk0.hours", "儲存", "通電時間", "120 小時", 120, "h"),
            Fact("cpu.model", "處理器", "型號", "Example CPU"),
        ]);
        string first = FilePath("first.json");
        string second = FilePath("second.json");

        HardwareSnapshotService.Save(first, snapshot);
        var loaded = HardwareSnapshotService.Load(first);
        HardwareSnapshotService.Save(second, loaded);

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        Assert.Equal(HardwareSnapshotSchema.CurrentVersion, loaded.SchemaVersion);
        Assert.Equal("2.0.0-test", loaded.AppVersion);
        Assert.Equal(snapshot.AnonymousMachineId, loaded.AnonymousMachineId);
        Assert.Equal(["cpu.model", "storage.disk0.hours"], loaded.Facts.Select(x => x.Key));
        Assert.Matches("^[0-9a-f]{64}$", loaded.Integrity.Hash);
        Assert.Equal("SHA-256", loaded.Integrity.Algorithm);
    }

    [Fact]
    public void 內容遭竄改_載入拒絕且明說不是簽章()
    {
        string path = FilePath("tampered.json");
        HardwareSnapshotService.Save(path, Snapshot([Fact("cpu.model", "處理器", "型號", "Old CPU")]));
        string json = File.ReadAllText(path).Replace("Old CPU", "New CPU", StringComparison.Ordinal);
        File.WriteAllText(path, json);

        var ex = Assert.Throws<InvalidDataException>(() => HardwareSnapshotService.Load(path));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Contains("不是簽章", ex.Message);
    }

    [Fact]
    public void 敏感事實_預設遮蔽且JSON完全不含原始值()
    {
        const string secret = "SERIAL-原始-123456";
        var snapshot = Snapshot([
            Fact("board.serial", "主機板", "序號", secret, sensitive: true),
            Fact("board.model", "主機板", "型號", "Public Model"),
        ]);
        string path = FilePath("redacted.json");

        HardwareSnapshotService.Save(path, snapshot);
        string json = File.ReadAllText(path);
        var loaded = HardwareSnapshotService.Load(path);

        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", json, StringComparison.Ordinal);
        var sensitive = Assert.Single(loaded.Facts.Where(x => x.Sensitive));
        Assert.True(HardwareSnapshotService.IsRedacted(sensitive.Value));
        Assert.Null(sensitive.NumericValue);
    }

    [Fact]
    public void 敏感事實_只有明確OptIn才會保留()
    {
        const string secret = "SERIAL-OPT-IN-987";
        var original = HardwareSnapshotService.Create(
            [Fact("disk.serial", "儲存", "序號", secret, sensitive: true)],
            ["raw-machine-id"], _measured, "2.0.0-test", SensitiveValuePolicy.Preserve);
        string defaultPath = FilePath("default.json");
        string optInPath = FilePath("opt-in.json");

        HardwareSnapshotService.Save(defaultPath, original);
        HardwareSnapshotService.Save(optInPath, original,
            new HardwareSnapshotSaveOptions { SensitiveValues = SensitiveValuePolicy.Preserve });

        Assert.DoesNotContain(secret, File.ReadAllText(defaultPath), StringComparison.Ordinal);
        Assert.Contains(secret, File.ReadAllText(optInPath), StringComparison.Ordinal);
        Assert.Equal(secret, HardwareSnapshotService.Load(optInPath).Facts.Single().Value);
    }

    [Fact]
    public void 遮蔽敏感值仍可偵測改變但不洩漏原文()
    {
        var before = HardwareSnapshotService.Create(
            [Fact("disk.serial", "儲存", "序號", "SECRET-A", sensitive: true)],
            ["machine"], _measured, "test", SensitiveValuePolicy.Redact);
        var same = HardwareSnapshotService.Create(
            [Fact("disk.serial", "儲存", "序號", "SECRET-A", sensitive: true)],
            ["machine"], _measured.AddMinutes(1), "test", SensitiveValuePolicy.Redact);
        var changed = HardwareSnapshotService.Create(
            [Fact("disk.serial", "儲存", "序號", "SECRET-B", sensitive: true)],
            ["machine"], _measured.AddMinutes(2), "test", SensitiveValuePolicy.Redact);

        Assert.Equal(SnapshotChangeKind.Unchanged, Assert.Single(HardwareSnapshotService.Diff(before, same).Changes).Kind);
        var delta = Assert.Single(HardwareSnapshotService.Diff(before, changed).Changes);
        Assert.Equal(SnapshotChangeKind.Changed, delta.Kind);
        Assert.DoesNotContain("SECRET", delta.Previous!.Value);
        Assert.DoesNotContain("SECRET", delta.Current!.Value);
    }

    [Fact]
    public void 差異比較_辨識新增移除變更不變並計算數值Delta()
    {
        var before = Snapshot([
            Fact("cpu.model", "處理器", "型號", "Same CPU"),
            Fact("disk.hours", "儲存", "通電時間", "100 小時", 100, "h"),
            Fact("driver.old", "驅動", "舊驅動", "1.0"),
        ], _measured);
        var after = Snapshot([
            Fact("cpu.model", "處理器", "型號", "Same CPU"),
            Fact("disk.hours", "儲存", "通電時間", "135 小時", 135, "h"),
            Fact("driver.new", "驅動", "新驅動", "2.0"),
        ], _measured.AddDays(1));

        var diff = HardwareSnapshotService.Diff(before, after);

        Assert.True(diff.IsSameMachine);
        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(1, diff.RemovedCount);
        Assert.Equal(1, diff.ChangedCount);
        Assert.Equal(1, diff.UnchangedCount);
        Assert.Equal(35, diff.Entries.Single(x => x.Key == "disk.hours").NumericDelta);
        Assert.Equal(SnapshotChangeKind.Added, diff.Entries.Single(x => x.Key == "driver.new").Kind);
        Assert.Equal(SnapshotChangeKind.Removed, diff.Entries.Single(x => x.Key == "driver.old").Kind);
        Assert.Null(diff.Entries.Single(x => x.Key == "cpu.model").NumericDelta);
    }

    [Fact]
    public void Schema版本錯誤_即使JSON合法也拒絕()
    {
        string path = FilePath("future-schema.json");
        HardwareSnapshotService.Save(path, Snapshot([Fact("cpu.model", "處理器", "型號", "CPU")]));
        string json = File.ReadAllText(path).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 999", StringComparison.Ordinal);
        File.WriteAllText(path, json);

        var ex = Assert.Throws<NotSupportedException>(() => HardwareSnapshotService.Load(path));
        Assert.Contains("schemaVersion 999", ex.Message);
    }

    [Fact]
    public void 機器匿名識別_不可洩漏原始序號且與輸入順序無關()
    {
        const string uuid = "RAW-UUID-ABC-123";
        const string serial = "BOARD-SERIAL-XYZ-987";

        string first = HardwareSnapshotService.CreateAnonymousMachineId([uuid, serial]);
        string second = HardwareSnapshotService.CreateAnonymousMachineId([serial, uuid]);
        string path = FilePath("machine-id.json");
        HardwareSnapshotService.Save(path, Snapshot([Fact("cpu.model", "處理器", "型號", "CPU")], identity: [uuid, serial]));
        string json = File.ReadAllText(path);

        Assert.Equal(first, second);
        Assert.Matches("^sha256:[0-9a-f]{64}$", first);
        Assert.DoesNotContain(uuid, first, StringComparison.Ordinal);
        Assert.DoesNotContain(serial, first, StringComparison.Ordinal);
        Assert.DoesNotContain(uuid, json, StringComparison.Ordinal);
        Assert.DoesNotContain(serial, json, StringComparison.Ordinal);
    }

    [Fact]
    public void JSON多餘欄位與重複FactKey都拒絕()
    {
        string unknown = FilePath("unknown.json");
        HardwareSnapshotService.Save(unknown, Snapshot([Fact("cpu.model", "處理器", "型號", "CPU")]));
        string json = File.ReadAllText(unknown).Replace("\"appVersion\":", "\"unexpected\": true,\n  \"appVersion\":", StringComparison.Ordinal);
        File.WriteAllText(unknown, json);
        Assert.Throws<InvalidDataException>(() => HardwareSnapshotService.Load(unknown));

        Assert.Throws<ArgumentException>(() => Snapshot([
            Fact("same.key", "A", "A", "1"),
            Fact("same.key", "B", "B", "2"),
        ]));
    }

    [Fact]
    public void 數值單位不同時不產生誤導Delta()
    {
        var before = Snapshot([Fact("temperature.cpu", "溫度", "CPU", "40 °C", 40, "°C")]);
        var after = Snapshot([Fact("temperature.cpu", "溫度", "CPU", "313.15 K", 313.15, "K")]);

        var entry = Assert.Single(HardwareSnapshotService.Diff(before, after).Entries);
        Assert.Equal(SnapshotChangeKind.Changed, entry.Kind);
        Assert.Null(entry.NumericDelta);
    }

    private HardwareSnapshot Snapshot(
        IEnumerable<HardwareSnapshotFact> facts,
        DateTimeOffset? captured = null,
        IEnumerable<string?>? identity = null)
        => HardwareSnapshotService.Create(facts, identity ?? ["machine-raw-material"],
            captured ?? _measured, "2.0.0-test", SensitiveValuePolicy.Preserve);

    private HardwareSnapshotFact Fact(
        string key,
        string category,
        string name,
        string value,
        double? number = null,
        string? unit = null,
        bool sensitive = false)
        => new()
        {
            Key = key,
            Category = category,
            Name = name,
            Value = value,
            NumericValue = number,
            Unit = unit,
            Source = "單元測試合成來源",
            Trust = FactTrustLevel.Measured,
            Sensitive = sensitive,
            MeasuredAtUtc = _measured,
        };

    private string FilePath(string name) => Path.Combine(_directory, name);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
