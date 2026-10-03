using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP23＋WP24 的契約：攝影機/UVC 列舉（WMI PnP，usermode）與企業儲存的三態分離
/// （iSCSI/MPIO 看服務狀態、FC 看根\wmi HBA 類別、NVMe-oF 刻意標「偵測路徑未實作」——
/// 無此硬體是 NotApplicable 不是錯誤，與 OobFactsService 同一套分離哲學）。
/// </summary>
public class CameraAndEnterpriseStorageTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 攝影機_逐台名稱與狀態_讀不到三態()
    {
        var cameras = new List<XinSpect.PnpCameraEntry>
        {
            new("整合式攝影機", "OK"),
            new("外接 UVC 裝置", "Error"),
        };
        var facts = XinSpect.CameraFactsService.Collect(At, probe: () => cameras);

        Assert.All(facts, f => Assert.Equal("週邊匯流排", f.Category));
        var count = Assert.Single(facts, f => f.Key == "cam.count");
        Assert.Equal(2u, count.NumericValue);
        var cam1 = Assert.Single(facts, f => f.Key == "cam.1");
        Assert.Contains("外接 UVC 裝置", cam1.Value);
        Assert.Contains("Error", cam1.Value);   // 裝置狀態照抄系統口徑

        var fail = XinSpect.CameraFactsService.Collect(At, probe: () => null);
        Assert.All(fail, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }

    [Fact]
    public void 攝影機_零台是沒有不是錯誤()
    {
        var facts = XinSpect.CameraFactsService.Collect(At, probe: () => []);
        var count = Assert.Single(facts, f => f.Key == "cam.count");
        Assert.Equal(FactAvailability.Present, count.Availability);
        Assert.Equal(0u, count.NumericValue);
    }

    [Fact]
    public void 企業儲存_有無分離與NVMeoF誠實標()
    {
        var facts = XinSpect.EnterpriseStorageFactsService.Collect(At,
            iscsiRunning: true, mpioServiceInstalled: false, fcAdapters: 0);

        var iscsi = Assert.Single(facts, f => f.Key == "ent.iscsi");
        Assert.Equal(FactAvailability.Present, iscsi.Availability);
        Assert.Contains("執行中", iscsi.Value);

        var mpio = Assert.Single(facts, f => f.Key == "ent.mpio");
        Assert.Equal(FactAvailability.NotSupported, mpio.Availability);
        Assert.Contains("未安裝", mpio.UnavailableReason);

        var fc = Assert.Single(facts, f => f.Key == "ent.fc");
        Assert.Equal(FactAvailability.NotApplicable, fc.Availability);
        Assert.Contains("沒有 FC", fc.UnavailableReason);

        var nvmeof = Assert.Single(facts, f => f.Key == "ent.nvmeof");
        Assert.Equal(FactAvailability.NotSupported, nvmeof.Availability);
        Assert.Contains("偵測路徑未實作", nvmeof.UnavailableReason);
    }
}
