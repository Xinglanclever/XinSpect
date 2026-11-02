using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Merkle 指紋的守門：同樣組態必須同 root；敏感／讀不到／易變的鍵不得進樹；輸出不得帶值。
/// </summary>
public class FactMerkleTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 1, 30, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, string value = "v", bool sensitive = false,
        FactAvailability availability = FactAvailability.Present, string source = "測試來源",
        FactTrustLevel trust = FactTrustLevel.Measured)
        => new(key, "測試域", key, availability == FactAvailability.Present ? value : "", "", source,
            trust, sensitive, At, availability == FactAvailability.Present ? 1 : null, availability,
            availability == FactAvailability.Present ? null : "讀不到");

    [Fact]
    public void 同組態不同順序與不同量測時間_root必須相同()
    {
        var a = FactMerkleService.Build(
        [
            Fact("chipset.bios_cntl", "最強保護"),
            Fact("uefi.secure_boot", "是"),
            Fact("spi.prr", "PR0 寫保護"),
        ]);
        var b = FactMerkleService.Build(
        [
            Fact("spi.prr", "PR0 寫保護"),
            Fact("chipset.bios_cntl", "最強保護"),
            Fact("uefi.secure_boot", "是"),
        ]);

        Assert.Equal(3, a.LeafCount);
        Assert.Equal(a.RootSha256, b.RootSha256);
        Assert.Equal(a.Leaves.Select(x => x.Key), b.Leaves.Select(x => x.Key));
    }

    [Fact]
    public void 值變了_root就要變()
    {
        var a = FactMerkleService.Build([Fact("uefi.pk", "PK-A")]);
        var b = FactMerkleService.Build([Fact("uefi.pk", "PK-B")]);

        Assert.NotEqual(a.RootSha256, b.RootSha256);
    }

    [Fact]
    public void 敏感_非Present_易變鍵都被排除且分類計數()
    {
        var tree = FactMerkleService.Build(
        [
            Fact("spi.bios_hash", "SECRET", sensitive: true),
            Fact("spi.hsfsts", availability: FactAvailability.ReadError),
            Fact("spi.frap.temp", "41"),
            Fact("ac.dmar.summary", "4 個 DRHD"),
            Fact("not.in.allowlist", "x"),
        ]);

        Assert.Equal(1, tree.LeafCount);
        Assert.Equal("ac.dmar.summary", tree.Leaves[0].Key);
        Assert.Equal(1, tree.Excluded.Sensitive);
        Assert.Equal(1, tree.Excluded.Unavailable);
        Assert.Equal(1, tree.Excluded.Volatile);
        Assert.Equal(1, tree.Excluded.NotAllowlisted);
    }

    [Fact]
    public void 沒有任何穩定事實_root是null而不是空字串()
    {
        var tree = FactMerkleService.Build([Fact("nvme.temp", "40")]);

        Assert.Equal(0, tree.LeafCount);
        Assert.Null(tree.RootSha256);
        Assert.Empty(tree.Leaves);
    }

    [Fact]
    public void 三片葉子_落單者原樣提升到下一層()
    {
        var facts = new[]
        {
            Fact("ac.cedt.summary", "A"),
            Fact("ac.dmar.summary", "B"),
            Fact("ac.madt.summary", "C"),
        };
        var tree = FactMerkleService.Build(facts);

        byte[] l1 = Convert.FromHexString(tree.Leaves[0].LeafSha256);
        byte[] l2 = Convert.FromHexString(tree.Leaves[1].LeafSha256);
        byte[] l3 = Convert.FromHexString(tree.Leaves[2].LeafSha256);
        byte[] expected = SHA256.HashData([.. SHA256.HashData([.. l1, .. l2]), .. l3]);

        Assert.Equal(Convert.ToHexStringLower(expected), tree.RootSha256);
    }

    [Fact]
    public void 葉子依鍵排序且每片都有64位小寫十六進位()
    {
        var tree = FactMerkleService.Build(
        [
            Fact("uefi.secure_boot", "是"),
            Fact("chipset.bios_cntl", "最強保護"),
            Fact("ac.dmar.summary", "4 個 DRHD"),
        ]);

        var keys = tree.Leaves.Select(l => l.Key).ToList();
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), keys);
        Assert.All(tree.Leaves, l =>
        {
            Assert.Matches("^[0-9a-f]{64}$", l.LeafSha256);
            Assert.Equal(l.LeafSha256, FactMerkleService.LeafHash(
                Fact(l.Key, l.Key == "uefi.secure_boot" ? "是"
                    : l.Key == "chipset.bios_cntl" ? "最強保護" : "4 個 DRHD")));
        });
    }

    [Fact]
    public void canonical_payload不含量測時間()
    {
        var f = Fact("uefi.pk", "PK-A");
        string payload = FactMerkleService.CanonicalPayload(f);

        Assert.DoesNotContain("2026", payload, StringComparison.Ordinal);
        Assert.Equal(4, payload.Split(FactMerkleService.Separator).Length);
    }
}