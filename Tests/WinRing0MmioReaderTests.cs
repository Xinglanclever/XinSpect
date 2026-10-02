using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WinRing0 實體記憶體後端與 MMIO 後端裁決鏈的<b>降級契約</b>。
/// </summary>
/// <remarks>
/// 慣例同 <see cref="WinRing0BridgeTests"/>：特權層只測降級路徑，成功路徑會真的開核心驅動，
/// 測試套件不做這種事（裁決鏈的預設工廠也因此絕不在測試中呼叫——那會觸發 Ring0.Open()）；
/// 成功路徑的正確性由服務層假件測試（SpiFlashTests／MchbarTests／EcamAerTests）覆蓋。
/// 釘住的重點：橋接失敗時不得聲稱可讀，裁決鏈落空時原因必須把每個後端為什麼不行說清楚——
/// 假的「可讀」會讓服務層把 0／0xFF 當資料解讀，正是驗機功能最不能犯的錯。
/// </remarks>
public class WinRing0MmioReaderTests
{
    [Fact]
    public void 失敗橋接不得聲稱支援實體記憶體讀取()
    {
        using var bridge = WinRing0Bridge.CreateFailed("測試用的失敗橋接");

        Assert.False(bridge.MemoryReadAvailable);
        Assert.Null(bridge.ReadMemoryBlock(0xFED10000, 0x88));
    }

    [Fact]
    public void 失敗橋接下的讀取者必須三態並帶原因()
    {
        using var reader = new WinRing0MmioReader(WinRing0Bridge.CreateFailed("橋接掛了"));

        Assert.False(reader.Available);
        Assert.Equal("橋接掛了", reader.UnavailableReason);

        var block = reader.ReadBlock(0xFED10000, 0x88);
        Assert.Null(block);
        Assert.Equal("橋接掛了", reader.LastFailReason);
    }

    private sealed class FakeMmio : IMmioReader, IDisposable
    {
        public FakeMmio(bool available, string reason) { Available = available; UnavailableReason = reason; }

        public bool Available { get; }
        public string? UnavailableReason { get; }
        public int DisposeCount { get; private set; }

        public byte[]? ReadBlock(ulong physicalAddress, int length) => null;
        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void 主力可用時直接回主力且不打擾備援()
    {
        var primary = new FakeMmio(true, "");
        var fallback = new FakeMmio(true, "");
        try
        {
            var chosen = MmioBackendSelector.Select(() => primary, () => fallback);

            Assert.Same(primary, chosen);
            Assert.Equal(0, primary.DisposeCount);
            Assert.Equal(0, fallback.DisposeCount); // 主力可用 → 備援工廠根本不該被呼叫
        }
        finally { primary.Dispose(); fallback.Dispose(); }
    }

    [Fact]
    public void 主力不可用時備援接手且主力被交還()
    {
        var primary = new FakeMmio(false, "主力掛了");
        var fallback = new FakeMmio(true, "");
        var chosen = MmioBackendSelector.Select(() => primary, () => fallback);
        try
        {
            Assert.Same(fallback, chosen);
            Assert.Equal(1, primary.DisposeCount);
            Assert.Equal(0, fallback.DisposeCount);
        }
        finally { fallback.Dispose(); }
    }

    [Fact]
    public void 兩者皆不可用時回帶完整原因的三態件()
    {
        var primary = new FakeMmio(false, "HVCI 封鎖");
        var fallback = new FakeMmio(false, "驅動裝置不存在");
        var chosen = MmioBackendSelector.Select(() => primary, () => fallback);
        try
        {
            Assert.IsType<UnavailableMmioReader>(chosen);
            Assert.False(chosen.Available);
            Assert.Equal("WinRing0 實體記憶體不可用（HVCI 封鎖）；XsRegProbe 不可用（驅動裝置不存在）", chosen.UnavailableReason);
            Assert.Null(chosen.ReadBlock(0xFED10000, 0x88));
            Assert.Equal(1, primary.DisposeCount);
            Assert.Equal(1, fallback.DisposeCount);
        }
        finally { primary.Dispose(); fallback.Dispose(); }
    }
}
