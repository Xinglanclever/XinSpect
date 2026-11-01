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
        /// <summary>Readable=false 用來模擬「方法存在、Available 為真，但實際讀取一律失敗」——
        /// 2026-11-01 在實機上看到的 WinRing0 行為。</summary>
        public FakeMmio(bool available, string reason, bool readable = false)
        {
            Available = available; UnavailableReason = reason; Readable = readable;
        }

        public bool Available { get; }
        public string? UnavailableReason { get; }
        public bool Readable { get; }
        public int DisposeCount { get; private set; }
        public int ReadCount { get; private set; }
        public string? LastFailReason => Readable ? null : "讀取失敗（假件）";

        public byte[]? ReadBlock(ulong physicalAddress, int length)
        {
            ReadCount++;
            return Readable ? new byte[length] : null;
        }

        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void 主力可用時直接回主力且不打擾備援()
    {
        var primary = new FakeMmio(true, "", readable: true);
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
    public void 主力聲稱可讀但探測失敗時備援接手()
    {
        // 這就是 2026-11-01 實機上的 WinRing0：MemoryReadAvailable=true、MSR 也通，
        // 但實體記憶體讀取一律回 false。只看 Available 會讓這條路永遠選不到備援。
        var primary = new FakeMmio(true, "", readable: false);
        var fallback = new FakeMmio(true, "", readable: true);
        var chosen = MmioBackendSelector.Select(() => primary, () => fallback);
        try
        {
            Assert.Same(fallback, chosen);
            Assert.Equal(1, primary.DisposeCount);
            // 探測打兩個位址（0x0、0x100000）就夠了，不該把整條備援也拿來試讀。
            Assert.Equal(2, primary.ReadCount);
            Assert.Equal(0, fallback.ReadCount);
        }
        finally { fallback.Dispose(); }
    }

    [Fact]
    public void 探測失敗且備援也不可用時原因要說明宣稱可讀但讀失敗()
    {
        var primary = new FakeMmio(true, "", readable: false);
        var fallback = new FakeMmio(false, "驅動裝置不存在");
        var chosen = MmioBackendSelector.Select(() => primary, () => fallback);
        try
        {
            Assert.IsType<UnavailableMmioReader>(chosen);
            Assert.Contains("宣稱可讀", chosen.UnavailableReason);
            Assert.Contains("0x100000", chosen.UnavailableReason);
            Assert.Contains("驅動裝置不存在", chosen.UnavailableReason);
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
