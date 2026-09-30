
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 2.1.0「記憶體時脈解除 CPU-Z 依賴」：CPU-Z 不在場時，SPD 直讀與 WMI 設定速率要能補上，
/// 且來源必須誠實標示——時序只有 SPD 能給，WMI 那一檔不可以假裝 Loaded。
/// </summary>
public class NativeTimingsTests
{
    private static MainViewModel Vm(Action<MainViewModel> seed)
    {
        var vm = new MainViewModel();
        seed(vm);
        return vm;
    }

    [Fact]
    public void 有SPD直讀時以JEDEC時序填入並標明來源()
    {
        // 真實基準檔（SpdDecoderTests 同源）
        var raw = System.IO.File.ReadAllBytes(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "spd-ddr4-real-dimm1.bin"));
        var decoded = SpdDecoder.Decode(raw);
        var vm = Vm(v => v.DirectSpdReads =
            [new SpdDirectRead("PCH i801", 0x50, raw, decoded)]);

        var t = StartupSequence.BuildNativeTimings(vm);
        Assert.NotNull(t);
        Assert.True(t!.Loaded);
        Assert.Contains("SPD 直讀", t.SourceText);
        Assert.Contains("JEDEC", t.SourceText);
        Assert.NotEqual("—", t.CL);
        Assert.True(t.DramFrequencyMHz > 0);
        Assert.StartsWith("DDR4-", t.DataRateText);
        // 誠實原則：要說明 XMP 開啟時實際值可能不同
        Assert.Contains("XMP", t.Status);
    }

    [Fact]
    public void 只有WMI時給頻率不假裝有時序()
    {
        var vm = Vm(_ => { });
        vm.Modules.Add(new MemoryModuleInfo { Manufacturer = "KHX", PartNumber = "X", CapacityGB = 8, ConfiguredSpeedMHz = 3200, MemoryType = "DDR4" });
        vm.Modules.Add(new MemoryModuleInfo { Manufacturer = "KHX", PartNumber = "X", CapacityGB = 8, ConfiguredSpeedMHz = 3200, MemoryType = "DDR4" });

        var t = StartupSequence.BuildNativeTimings(vm);
        Assert.NotNull(t);
        Assert.False(t!.Loaded);                       // 時序沒有來源，不得 Loaded
        Assert.Contains("WMI", t.SourceText);
        Assert.Equal(1600, t.DramFrequencyMHz);        // DDR：3200 ÷ 2
        Assert.Equal("—", t.CL);
        Assert.Equal("16 GB（2 條）", t.MemorySizeText);
    }

    [Fact]
    public void 兩者皆無時回null讓UI如實顯示未讀到()
    {
        var vm = new MainViewModel();
        Assert.Null(StartupSequence.BuildNativeTimings(vm));
    }
}
