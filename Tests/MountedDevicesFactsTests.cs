using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// MountedDevices 事實服務（FS-009）三態契約：機碼缺席、讀取失敗、空值、正常列舉——
/// 「掛載歷史不是在線清單」界線要寫進值裡。
/// </summary>
public class MountedDevicesFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 機碼缺席_如實說不存在不冒充沒有掛載()
    {
        var facts = MountedDevicesFactsService.Collect(At, () => null);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不存在", f.UnavailableReason);
    }

    [Fact]
    public void 讀取失敗_標讀取錯誤()
    {
        var facts = MountedDevicesFactsService.Collect(At, () => throw new System.Security.SecurityException("denied"));
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("讀不到不等於沒有", f.UnavailableReason);
    }

    [Fact]
    public void 空值清單_如實列零筆()
    {
        var facts = MountedDevicesFactsService.Collect(At, () => []);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("0 筆值", f.Value);
    }

    [Fact]
    public void 碟號與磁碟區_逐項解碼與歷史界線()
    {
        var dosC = new byte[12];
        BitConverter.GetBytes(0x1234ABCDu).CopyTo(dosC, 0);
        BitConverter.GetBytes(0x100000L).CopyTo(dosC, 4);
        var vol1 = System.Text.Encoding.Unicode.GetBytes("\\Device\\HarddiskVolume3\0");
        var vol2 = System.Text.Encoding.Unicode.GetBytes("\\Device\\HarddiskVolume5");
        var other = new byte[4]; // 未知形式值

        var facts = MountedDevicesFactsService.Collect(At, () =>
        [
            new(@"\DosDevices\C:", dosC),
            new(@"\DosDevices\D:", dosC),
            new(@"\??\Volume{11111111-2222-3333-4444-555555555555}", vol1),
            new(@"\??\Volume{66666666-7777-8888-9999-aaaaaaaaaaaa}", vol2),
            new(@"SomethingElse", other),
        ]);

        var count = facts.Single(f => f.Key == "fs.mount.count");
        Assert.Equal(5, count.NumericValue);
        Assert.Contains("碟號 2 筆＋磁碟區 GUID 2 筆＝5 筆", count.Value);
        Assert.Contains("其他形式值", count.Value);
        Assert.Contains("歷史紀錄", count.Value);
        Assert.Contains("不是現在在線清單", count.Value);

        var letters = facts.Single(f => f.Key == "fs.mount.letters");
        Assert.Contains("C:", letters.Value);
        Assert.Contains("D:", letters.Value);

        var volumes = facts.Single(f => f.Key == "fs.mount.volumes");
        Assert.Contains("→\\Device\\HarddiskVolume3", volumes.Value);
        Assert.Contains("HarddiskVolume5", volumes.Value);
    }

    [Fact]
    public void 碟號二進位_簽章與位移金標()
    {
        var dos = new byte[12];
        BitConverter.GetBytes(0xAABBCCDDu).CopyTo(dos, 0);
        BitConverter.GetBytes(0x7FF00L).CopyTo(dos, 4);
        var decoded = MountedDevicesFactsService.TryDecodeDosDevice(dos);
        Assert.Equal((0xAABBCCDDu, 0x7FF00L), decoded);
        Assert.Null(MountedDevicesFactsService.TryDecodeDosDevice(new byte[8]));
    }

    [Fact]
    public void 磁碟區目標_奇數長度與壞內容如實回null()
    {
        Assert.Equal("\\Device\\HarddiskVolume3",
            MountedDevicesFactsService.TryDecodeVolumeTarget(System.Text.Encoding.Unicode.GetBytes("\\Device\\HarddiskVolume3")));
        Assert.Null(MountedDevicesFactsService.TryDecodeVolumeTarget(new byte[3]));
        Assert.Null(MountedDevicesFactsService.TryDecodeVolumeTarget(System.Text.Encoding.Unicode.GetBytes("\\Device\\HarddiskVolume1")[..3]));
    }
}
