using System.IO;
using Xunit;

namespace XinSpect.Tests;

public sealed class RawRegisterSnapshotStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private static RawRegisterSnapshot Sample() => new()
    {
        AppVersion = "2.1.0.5",
        TakenAtUtc = At,
        Regions =
        [
            new RawRegisterRegion { Source = "pcicfg:00:1f.0+dc", Bytes = [0x22, 0x00, 0x00, 0x00] },
            new RawRegisterRegion { Source = "msr:0x3a", Bytes = [5, 0, 0, 0, 0, 0, 0, 0], VolatilityMask = [0, 0, 0, 0, 0, 0, 0, 1] },
            new RawRegisterRegion { Source = "mmio:spi:0xfed10000+88", Availability = FactAvailability.InsufficientPrivilege, UnavailableReason = "缺自家核心驅動（未載入）" },
        ],
    };

    [Fact]
    public void 存取往返_位元組與三態逐區原樣()
    {
        string path = Path.Combine(Path.GetTempPath(), "xinraw-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            RawRegisterSnapshotStore.Save(path, Sample());
            var loaded = RawRegisterSnapshotStore.Load(path);

            Assert.Equal("2.1.0.5", loaded.AppVersion);
            var biosCntl = loaded.Regions.Single(r => r.Source == "pcicfg:00:1f.0+dc");
            Assert.Equal([0x22, 0, 0, 0], biosCntl.Bytes);
            var spi = loaded.Regions.Single(r => r.Source == "mmio:spi:0xfed10000+88");
            Assert.Equal(FactAvailability.InsufficientPrivilege, spi.Availability);
            Assert.Contains("缺自家核心驅動", spi.UnavailableReason);
            Assert.Equal([0, 0, 0, 0, 0, 0, 0, 1], loaded.Regions.Single(r => r.Source == "msr:0x3a").VolatilityMask);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void 竄改一個位元組_載入拒收()
    {
        string path = Path.Combine(Path.GetTempPath(), "xinraw-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            RawRegisterSnapshotStore.Save(path, Sample());
            var loaded = RawRegisterSnapshotStore.Load(path); // 先驗合法檔可載
            Assert.NotNull(loaded);

            // 原樣保留舊信封、只改一個位元組 → 雜湊必不相符
            var tampered = loaded with
            {
                Regions = loaded.Regions.Select(r => r.Source == "pcicfg:00:1f.0+dc"
                    ? r with { Bytes = [0x99, 0, 0, 0] } : r).ToArray(),
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(tampered, RawRegisterSnapshotStore.JsonOptions));

            Assert.Throws<InvalidOperationException>(() => RawRegisterSnapshotStore.Load(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void 缺完整性欄位_拒載不猜()
    {
        string path = Path.Combine(Path.GetTempPath(), "xinraw-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var unsigned = Sample();
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(unsigned, RawRegisterSnapshotStore.JsonOptions));
            Assert.Throws<InvalidOperationException>(() => RawRegisterSnapshotStore.Load(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void 雜湊不含信封自身_同內容同雜湊()
    {
        var a = RawRegisterSnapshotStore.WithIntegrity(Sample());
        var b = RawRegisterSnapshotStore.WithIntegrity(Sample());
        Assert.Equal(a.Integrity!.Hash, b.Integrity!.Hash);
        Assert.Equal("sha256", a.Integrity.Algorithm);
    }
}
