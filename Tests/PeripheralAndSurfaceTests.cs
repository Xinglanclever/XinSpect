using Xunit;

namespace XinSpect.Tests;

public class BluetoothBatteryServiceTests
{
    [Fact]
    public void 讀取不丟例外且回List()
    {
        var rows = BluetoothBatteryService.Read();
        Assert.NotNull(rows);
        // 無藍牙裝置是正常的（虛擬機/伺服器），只要不丟例外就行
    }
}

public class NpuComputeBenchServiceTests
{
    [Fact]
    public void 基準跑完回合理GFLOPS()
    {
        var r = NpuComputeBenchService.Run();
        Assert.True(r.Gflops > 0, "GFLOPS 不該是 0");
        Assert.True(r.ElapsedMs > 0);
        Assert.True(r.VectorWidth >= 4, "Vector width 應 >=4 (SSE) 或 8 (AVX2)");
    }
}

public class DiskSurfaceScanServiceTests
{
    [Fact]
    public void ListVolumes不丟例外且回List()
    {
        var vols = DiskSurfaceScanService.ListVolumes();
        Assert.NotNull(vols);
    }

    [Fact]
    public void 無效路徑回Null誠實()
    {
        var r = DiskSurfaceScanService.Scan("\\\\.\\NONEXISTENT_DRIVE_XYZ:");
        Assert.Null(r);
    }
}
