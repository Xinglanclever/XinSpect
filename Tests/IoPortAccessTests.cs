using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// I/O 埠存取層（WP1）與 POST 代碼事實（A38）的降級契約：失敗橋接必須短路、
/// 0xFF／0x00 不得解碼成 POST 步驟（V7 §9：port 不存在要三態，非 0xFF 冒充）。
/// 特權層只測降級路徑，成功路徑由服務層假件覆蓋——慣例同 WinRing0BridgeTests。
/// </summary>
public class IoPortAccessTests
{
    [Fact]
    public void 失敗橋接不得聲稱支援IO埠存取()
    {
        using var access = new WinRing0IoPortAccess(WinRing0Bridge.CreateFailed("橋接掛了"));

        Assert.False(access.Available);
        Assert.Equal("橋接掛了", access.UnavailableReason);
        Assert.Null(access.InByte(0x80));
        Assert.False(access.OutByte(0x70, 0x00)); // 測試斷言的就是「短路」，不會真送 out 指令
    }

    [Fact]
    public void POST代碼_不可用與讀取失敗都三態()
    {
        var at = DateTimeOffset.UtcNow;

        var denied = IoPortFactsService.Collect(new UnavailableIoPortAccess("缺 I/O 埠存取"), at);
        var d = Assert.Single(denied);
        Assert.Equal(FactAvailability.InsufficientPrivilege, d.Availability);
        Assert.Equal("缺 I/O 埠存取", d.UnavailableReason);

        var fail = IoPortFactsService.Collect(new FakeIoPort(null), at);
        var f = Assert.Single(fail);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Null(f.NumericValue);
    }

    [Fact]
    public void POST代碼_0xFF與0x00不解碼_其餘如實成列()
    {
        var at = DateTimeOffset.UtcNow;

        var noDevice = Assert.Single(IoPortFactsService.Collect(new FakeIoPort(0xFF), at));
        Assert.Equal(FactAvailability.NotSupported, noDevice.Availability);
        Assert.Contains("無裝置回應", noDevice.UnavailableReason);
        Assert.Null(noDevice.NumericValue);

        var noLatch = Assert.Single(IoPortFactsService.Collect(new FakeIoPort(0x00), at));
        Assert.Equal(FactAvailability.NotSupported, noLatch.Availability);
        Assert.Contains("未鎖存", noLatch.UnavailableReason);

        var real = Assert.Single(IoPortFactsService.Collect(new FakeIoPort(0x9A), at));
        Assert.Equal(FactAvailability.Present, real.Availability);
        Assert.StartsWith("0x9A", real.Value);
        Assert.Equal(0x9A, real.NumericValue);
        Assert.Contains("殘留鎖存值", real.Value); // 語意警示：這是開機後的殘值，不是現在的狀態
    }

    private sealed class FakeIoPort(byte? value) : IIoPortAccess
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public byte? InByte(uint port) => value;
        public bool OutByte(uint port, byte value) => true;
    }
}
