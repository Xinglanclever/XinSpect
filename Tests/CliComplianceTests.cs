using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>CLI 面的合規與 Merkle：形狀、退出碼、以及「輸出不得帶值」。</summary>
public class CliComplianceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 2, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, string value, double? numeric = null,
        FactAvailability availability = FactAvailability.Present, string reason = "")
        => new(key, "測試域", key, availability == FactAvailability.Present ? value : "", "",
            "測試來源", FactTrustLevel.Measured, false, At, numeric, availability,
            availability == FactAvailability.Present ? null : reason);

    private static HardwareFact[] ProtectedFacts() =>
    [
        Fact("chipset.bios_cntl", "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS"),
        Fact("chipset.smramc", "已鎖：D_LCK=1，SMRAM 設定鎖定"),
        Fact("spi.prr", "PR0 0x400000–0x40FFFF 寫+讀保護"),
        Fact("spi.write_surface", "最強保護：SMM_BWP=1，僅 SMM 可寫 BIOS"),
        Fact("bt.measured_boot", "有量測證據", 1),
        Fact("tpm.replay.summary", "一致 10／不一致 0", 10),
        Fact("uefi.secure_boot", "是", 1),
        Fact("uefi.db.count", "11 個", 11),
        Fact("uefi.dbx.count", "77 個", 77),
        Fact("byovd.hits", "0 個", 0),
        Fact("sec.vbs.state", "狀態 2", 2),
    ];

    [Fact]
    public void 合規模式_輸出控制項與ATTACK且未驗證項不冒充通過()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exit = CliService.Run(["--compliance"], ProtectedFacts, stdout, stderr);

        Assert.Equal(CliService.ExitPartial, exit);
        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        Assert.Equal("compliance", root.GetProperty("scope").GetString());
        Assert.Equal(11, root.GetProperty("controls").GetArrayLength());
        Assert.Equal(5, root.GetProperty("attack").GetArrayLength());
        Assert.True(root.GetProperty("summary").GetProperty("satisfied").GetInt32() >= 8);
        Assert.Equal(3, root.GetProperty("summary").GetProperty("unverifiable").GetInt32());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void 合規模式_什麼都讀不到時全部Unverifiable且退出碼2()
    {
        var stdout = new StringWriter();

        int exit = CliService.Run(["--compliance"], () => [], stdout, new StringWriter());

        Assert.Equal(CliService.ExitPartial, exit);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(0, doc.RootElement.GetProperty("summary").GetProperty("satisfied").GetInt32());
        Assert.Equal(11, doc.RootElement.GetProperty("summary").GetProperty("unverifiable").GetInt32());
    }

    [Fact]
    public void Merkle模式_root存在且輸出不含事實值()
    {
        var stdout = new StringWriter();
        var facts = new[]
        {
            Fact("uefi.pk", "SECRET-PK-VALUE"),
            Fact("chipset.bios_cntl", "最強保護"),
        };

        int exit = CliService.Run(["--merkle"], () => facts, stdout, new StringWriter());

        Assert.Equal(CliService.ExitOk, exit);
        string json = stdout.ToString();
        Assert.DoesNotContain("SECRET-PK-VALUE", json, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("leafCount").GetInt32());
        Assert.Matches("^[0-9a-f]{64}$", doc.RootElement.GetProperty("rootSha256").GetString());
    }

    [Fact]
    public void Merkle模式_沒有穩定事實時root為null且退出碼2()
    {
        var stdout = new StringWriter();

        int exit = CliService.Run(["--merkle"], () => [Fact("nvme.temp", "41")], stdout, new StringWriter());

        Assert.Equal(CliService.ExitPartial, exit);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(0, doc.RootElement.GetProperty("leafCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("rootSha256").ValueKind);
    }

    [Fact]
    public void 合規模式_支援out寫檔()
    {
        string path = Path.Combine(Path.GetTempPath(), "xinspect-compliance-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var stdout = new StringWriter();
            int exit = CliService.Run(["--compliance", "--out", path], ProtectedFacts, stdout, new StringWriter());

            Assert.Equal(CliService.ExitPartial, exit);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.True(File.Exists(path));
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("compliance", doc.RootElement.GetProperty("scope").GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}