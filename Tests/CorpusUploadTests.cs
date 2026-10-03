using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP48 corpus 上傳格式骨架：貢獻包由快照派生的契約。
/// **只收遮蔽版**（SensitiveValuesPreserved=true 的快照拒收）、
/// 敏感事實與身份鍵（serial／uuid／mac）逐鍵排除——機器釘住；
/// 上傳通路刻意不實作（骨架＝格式與淨化規則，收集等社群）。
/// </summary>
public class CorpusUploadTests
{
    private static HardwareSnapshot Snapshot(params HardwareSnapshotFact[] facts) => SnapshotWith(preserved: false, facts);

    private static HardwareSnapshot PreservedSnapshot(params HardwareSnapshotFact[] facts) => SnapshotWith(preserved: true, facts);

    private static HardwareSnapshot SnapshotWith(bool preserved, HardwareSnapshotFact[] facts)
    {
        var at = DateTimeOffset.Parse("2026-10-03T00:00:00+00:00");
        return new HardwareSnapshot
        {
            SchemaVersion = 1,
            AppVersion = "2.5.0",
            AnonymousMachineId = "machinehash",
            CapturedAtUtc = at,
            SensitiveValuesPreserved = preserved,
            Facts = facts,
            Integrity = new HardwareSnapshotIntegrity { Algorithm = HardwareSnapshotIntegrity.Sha256Algorithm, Hash = new string('a', 64) },
        };
    }

    private static HardwareSnapshotFact F(string key, string value, bool sensitive = false) => new()
    {
        Key = key, Category = "c", Name = key, Value = value,
        Source = "s", Trust = FactTrustLevel.Reported, Sensitive = sensitive,
        MeasuredAtUtc = DateTimeOffset.Parse("2026-10-03T00:00:00+00:00"),
    };

    [Fact]
    public void 貢獻包_身份鍵與敏感事實逐鍵排除()
    {
        var snapshot = Snapshot(
            F("cpu.generation", "Skylake-X"),
            F("board.serial", "SN123456"),
            F("system.uuid", "ABCD-1234"),
            F("net.mac", "AA:BB:CC:DD:EE:FF"),
            F("user.note", "我的筆記", sensitive: true),
            F("mem.spd.size", "16 GiB"));

        var contribution = XinSpect.CorpusUploadService.Build(snapshot);
        Assert.NotNull(contribution);
        Assert.Equal(new[] { "cpu.generation", "mem.spd.size" },
            contribution!.Facts.Select(f => f.Key).ToArray());
        Assert.Equal("machinehash", contribution.AnonymousMachineId);
        Assert.Equal(HardwareSnapshotIntegrity.Sha256Algorithm, contribution.Integrity.Algorithm);
    }

    [Fact]
    public void 貢獻包_敏感保留版拒收_遮蔽版才收()
    {
        Assert.Null(XinSpect.CorpusUploadService.Build(PreservedSnapshot(F("cpu.generation", "x"))));
        Assert.NotNull(XinSpect.CorpusUploadService.Build(Snapshot(F("cpu.generation", "x"))));
    }

    [Fact]
    public void 貢獻包_JSON往返_鍵序穩定()
    {
        var contribution = XinSpect.CorpusUploadService.Build(Snapshot(F("cpu.generation", "Skylake-X")));
        Assert.NotNull(contribution);
        string json = XinSpect.CorpusUploadService.ToJson(contribution!);
        string again = XinSpect.CorpusUploadService.ToJson(
            XinSpect.CorpusUploadService.FromJson(json)!);
        Assert.Equal(json, again);
        Assert.Contains("\"anonymousMachineId\"", json);
    }
}
