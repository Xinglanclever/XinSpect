namespace XinSpect;

/// <summary>一顆 AMD Radeon 顯示卡的唯讀遙測快照。</summary>
public sealed record AmdGpuSnapshot(
    string Name, int AdapterIndex, int BusNumber, int DeviceNumber, int FunctionNumber,
    double? TempC, uint? FanRpm, double? PowerW, uint? GfxClockMhz);

/// <summary>
/// ADL 後端的抽象——讓事實層在不掛真的 atiadlxx.dll 的前提下測行為形狀。
/// 回 null＝沒有 AMD 顯示卡或驅動 DLL 不存在，呼叫端如實 NotApplicable。
/// </summary>
public interface IAmdAdlBackend
{
    AmdGpuSnapshot? CollectSingleGpu();
}

/// <summary>
/// GPU 非 NVIDIA 事實（R8）：AMD Radeon 深度遙測經 ADL（atiadlxx.dll，usermode、<b>唯讀</b>）。
/// 溫度／風扇／功耗／時脈；名稱與驅動版本的標準層已由 <see cref="GpuAnalysisService"/> 的 WMI 路徑涵蓋。
/// <b>未在本機驗證</b>：本機是 NVIDIA——單位換算（溫度 ÷1000、OD6 功率 ÷255、PMLog 直讀）
/// 照 OpenHardwareMonitor 的長期實作；DLL 不存在或沒有 AMD 卡時整組 NotApplicable。
/// </summary>
public static class AmdAdlFactsService
{
    private const string Category = "顯示卡";
    private const string Source = "AMD ADL（atiadlxx.dll 唯讀遙測）";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, IAmdAdlBackend? backend = null)
    {
        var snapshot = (backend ?? new AmdAdlBackend()).CollectSingleGpu();
        if (snapshot is null)
            return
            [
                new("gpu.radeon.temp", Category, "Radeon 溫度", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "本機沒有 atiadlxx.dll（沒有 Radeon 顯示卡或驅動未裝）——整組不適用"),
                new("gpu.radeon.fan", Category, "Radeon 風扇轉速", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "本機沒有 atiadlxx.dll——整組不適用"),
                new("gpu.radeon.power", Category, "Radeon 功耗", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                    "本機沒有 atiadlxx.dll——整組不適用"),
            ];

        string bus = $"bus 0x{snapshot.BusNumber:X2}:0x{snapshot.DeviceNumber:X2}.0x{snapshot.FunctionNumber:X2}";
        var facts = new List<HardwareFact>
        {
            new("gpu.radeon.name", Category, "Radeon 顯示卡（ADL）",
                string.IsNullOrWhiteSpace(snapshot.Name) ? $"AMD 適配器 #{snapshot.AdapterIndex}（{bus}）" : $"{snapshot.Name.Trim()}（{bus}）", "",
                $"{Source}（未在本機驗證）", FactTrustLevel.Measured, false, at, null),
        };

        facts.Add(snapshot.TempC is { } t
            ? Fact("gpu.radeon.temp", "Radeon 溫度", $"{t:0.#} °C", t, at)
            : NA("gpu.radeon.temp", "Radeon 溫度", "驅動未回報（Overdrive/PMLog 皆不可用）"));
        facts.Add(snapshot.FanRpm is { } f
            ? Fact("gpu.radeon.fan", "Radeon 風扇轉速", $"{f} RPM", f, at)
            : NA("gpu.radeon.fan", "Radeon 風扇轉速", "驅動未回報"));
        facts.Add(snapshot.PowerW is { } p
            ? Fact("gpu.radeon.power", "Radeon 功耗", $"{p:0.#} W", p, at)
            : NA("gpu.radeon.power", "Radeon 功耗", "驅動未回報"));
        return facts;
    }

    private static HardwareFact Fact(string key, string name, string value, double numeric, DateTimeOffset at) =>
        new(key, Category, name, value, "", $"{Source}（未在本機驗證）", FactTrustLevel.Measured, false, at, numeric);

    private static HardwareFact NA(string key, string name, string reason) =>
        new(key, Category, name, "", "", Source, FactTrustLevel.Unknown, false, DateTimeOffset.UtcNow,
            null, FactAvailability.NotSupported, reason);
}

/// <summary>走真的 atiadlxx.dll 的後端。PMLog（Overdrive 8+）優先，Overdrive5/6/N 依代數後備；任何 loader 層例外收斂成 null。</summary>
public sealed class AmdAdlBackend : IAmdAdlBackend
{
    public AmdGpuSnapshot? CollectSingleGpu()
    {
        try
        {
            using var adl = AmdAdlInterop.TryLoad();
            if (adl is null) return null;
            if (!adl.MainControlCreate(out IntPtr context)) return null;
            try
            {
                int count = 0;
                if (!adl.AdapterNumberGet(context, ref count) || count <= 0) return null;

                var infos = adl.ReadAdapterInfos(context, count);
                if (infos is null) return null;

                foreach (var info in infos)
                {
                    if (info.VendorId != AmdAdlInterop.AmdVendorId) continue;
                    if (!adl.AdapterActiveGet(context, info.AdapterIndex, out int active) || active == 0) continue;

                    return ReadTelemetry(adl, context, info);
                }
                return null;
            }
            finally
            {
                adl.MainControlDestroy(context);
            }
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (BadImageFormatException) { return null; }
    }

    private static AmdGpuSnapshot ReadTelemetry(AmdAdlInterop adl, IntPtr context, AdapterInfoEntry info)
    {
        double? temp = null;
        uint? fan = null;
        double? power = null;
        uint? clock = null;

        // 第一層：PMLog（Overdrive 8+／RDNA 代；值域照 OHM：溫度 °C、風扇 RPM、功率 W、時脈 MHz）
        int[]? pmlog = adl.ReadPmlog(context, info.AdapterIndex);
        if (pmlog is not null)
        {
            int edge = pmlog[AmdAdlInterop.PmlogTemperatureEdge];
            int rpm = pmlog[AmdAdlInterop.PmlogFanRpm];
            int asic = pmlog[AmdAdlInterop.PmlogAsicPower];
            int gfx = pmlog[AmdAdlInterop.PmlogClkGfxclk];
            if (edge != AmdAdlInterop.PmlogUnsupported) temp = edge;
            if (rpm != AmdAdlInterop.PmlogUnsupported) fan = (uint)rpm;
            if (asic != AmdAdlInterop.PmlogUnsupported) power = asic;
            if (gfx != AmdAdlInterop.PmlogUnsupported) clock = (uint)gfx;
        }

        // 後備：Overdrive5（溫度 milli-°C、風扇 RPM）→ OverdriveN（溫度 milli-°C）→ Overdrive6（功率 ÷255）
        if (temp is null)
        {
            var t = new AmdAdlInterop.AdlTemperature { Size = System.Runtime.InteropServices.Marshal.SizeOf<AmdAdlInterop.AdlTemperature>() };
            if (adl.Overdrive5Temperature(context, info.AdapterIndex, ref t) && t.Temperature > 0)
                temp = t.Temperature / 1000.0;
            else if (adl.OverdriveNTemperature(context, info.AdapterIndex, AmdAdlInterop.OdnTempCore, out int odn) && odn > 0)
                temp = odn / 1000.0;
        }
        if (fan is null)
        {
            var fv = new AmdAdlInterop.AdlFanSpeedValue { Size = System.Runtime.InteropServices.Marshal.SizeOf<AmdAdlInterop.AdlFanSpeedValue>(), SpeedType = AmdAdlInterop.FanSpeedTypeRpm };
            if (adl.Overdrive5FanSpeed(context, info.AdapterIndex, ref fv) && fv.FanSpeed > 0)
                fan = (uint)fv.FanSpeed;
        }
        if (power is null && adl.Overdrive6Power(context, info.AdapterIndex, AmdAdlInterop.Od6PowerTotal, out int watts) && watts > 0)
            power = watts / 255.0;

        return new AmdGpuSnapshot(info.AdapterName, info.AdapterIndex, info.BusNumber,
            info.DeviceNumber, info.FunctionNumber, temp, fan, power, clock);
    }
}
