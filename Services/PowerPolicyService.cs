using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 電源政策實況：逐核頻率上限與閒置狀態、目前電源計劃的關鍵設定、平台睡眠能力矩陣。
/// </summary>
/// <remarks>
/// <para>三個資料來源都是官方 API、零特權、唯讀：
/// <c>CallNtPowerInformation(ProcessorInformation)</c> 給逐邏輯處理器的頻率與 C-state 上限；
/// <c>PowerReadACValueIndex</c> 給目前電源計劃裡各子群組的設定索引（含核心停放、PCIe ASPM、
/// USB 選擇性暫停這些藏在「進階電源設定」深處、有些預設根本不顯示的項目）；
/// <c>CallNtPowerInformation(SystemPowerCapabilities)</c> 給平台自己宣告的睡眠支援矩陣。</para>
/// <para>誠實界線：</para>
/// <list type="bullet">
/// <item><b>不寫入任何電源設定。</b>改電源計劃不會像刷韌體那樣讓機器變磚，但它同時牽動效能、延遲、
/// 溫度與耗電四件事，正確答案取決於用途——本卡片攤開事實，要改請用 Windows 電源選項或「場景設定檔」。</item>
/// <item><c>CurrentMhz</c> 是 P-state 上限換算值，<b>不是核心實際時脈</b>。本機實測它恆為 2601（標稱 2.6 GHz），
/// 而「頻率真相」卡片實測的有效時脈是 4186 MHz。這裡明白標注差異，不讓人誤以為 CPU 只跑 2.6 GHz。</item>
/// <item>停放（parked）沒有官方布林值可讀，故只報 <c>CurrentIdleState</c>／<c>MaxIdleState</c>
/// 這些能觀察到的事實與停放設定的百分比，不宣稱「第 N 核已被停放」。</item>
/// <item>讀不到的設定顯示「—」，不用預設值頂替。</item>
/// </list>
/// </remarks>
public sealed class PowerPolicyService : ObservableObject
{
    private bool _loading;
    public bool IsLoading
    {
        get => _loading;
        private set { if (SetProperty(ref _loading, value)) OnPropertyChanged(nameof(CanRefresh)); }
    }
    public bool CanRefresh => !_loading;

    private string _status = "尚未讀取。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _planName = "—";
    /// <summary>目前作用中的電源計劃名稱。</summary>
    public string PlanName { get => _planName; private set => SetProperty(ref _planName, value); }

    private string _processorSummary = "—";
    /// <summary>逐核電源狀態的彙總（頻率一致性、限頻顆數、C-state 上限）。</summary>
    public string ProcessorSummary { get => _processorSummary; private set => SetProperty(ref _processorSummary, value); }

    private string _currentMhzNotice = "—";
    /// <summary>CurrentMhz 的誠實說明（為何它不等於實際時脈）。</summary>
    public string CurrentMhzNotice { get => _currentMhzNotice; private set => SetProperty(ref _currentMhzNotice, value); }

    /// <summary>電源計劃的關鍵設定（核心停放、ASPM、USB 選擇性暫停、Turbo 政策、處理器狀態範圍）。</summary>
    public ObservableCollection<PowerPolicyRow> Settings { get; } = [];

    /// <summary>平台睡眠能力矩陣（S1–S5、休眠檔、快速啟動、現代待命）。</summary>
    public ObservableCollection<PowerPolicyRow> SleepStates { get; } = [];

    /// <summary>本卡片的界線說明（固定文字）。</summary>
    public string ScopeNotice => PowerPolicyDecoder.ScopeNotice;

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_loading) return;
        IsLoading = true;
        Status = "讀取中…";
        try
        {
            var (plan, summary, mhzNotice, settings, sleep, note) = await Task.Run(ReadAll);
            PlanName = plan;
            ProcessorSummary = summary;
            CurrentMhzNotice = mhzNotice;
            Settings.Clear(); foreach (var r in settings) Settings.Add(r);
            SleepStates.Clear(); foreach (var r in sleep) SleepStates.Add(r);
            Status = note;
        }
        catch (Exception ex)
        {
            Status = "讀取失敗：" + ex.Message;
        }
        finally { IsLoading = false; }
    }

    private (string Plan, string Summary, string MhzNotice, List<PowerPolicyRow> Settings,
             List<PowerPolicyRow> Sleep, string Note) ReadAll()
    {
        var cores = ReadProcessorPower();
        string summary = PowerPolicyDecoder.SummarizeProcessors(cores);
        // 取回報值最高的那顆做說明：逐核回報值在本機實測為 1200–2601 分歧（各核 P-state 上限不同），
        // 拿第 0 顆當代表會讓人以為全機只跑 1.2 GHz。分歧的完整區間在上面的逐核彙總已如實列出。
        string mhzNotice = cores.Count > 0
            ? PowerPolicyDecoder.DescribeCurrentMhz(
                cores.Max(c => c.CurrentMhz), cores.Max(c => c.MaxMhz), cores.Max(c => c.MhzLimit))
            : "—（沒有逐核資料可說明）";

        nint scheme = 0;
        string plan = "—";
        var settings = new List<PowerPolicyRow>();
        try
        {
            if (PowerGetActiveScheme(0, out scheme) == 0 && scheme != 0)
            {
                plan = ReadFriendlyName(scheme) ?? "—（計劃名稱讀不到）";
                Guid s = Marshal.PtrToStructure<Guid>(scheme);

                settings.Add(PowerPolicyDecoder.DescribeProcessorStateRange(
                    ReadAc(s, ProcessorSubgroup, ThrottleMinimum),
                    ReadAc(s, ProcessorSubgroup, ThrottleMaximum)));
                settings.Add(PowerPolicyDecoder.DescribeBoostMode(ReadAc(s, ProcessorSubgroup, PerfBoostMode)));
                settings.Add(PowerPolicyDecoder.DescribeCoreParking("核心停放：最多可用核心",
                    ReadAc(s, ProcessorSubgroup, CoreParkingMaxCores)));
                settings.Add(PowerPolicyDecoder.DescribeCoreParking("核心停放：最少可用核心",
                    ReadAc(s, ProcessorSubgroup, CoreParkingMinCores)));
                settings.Add(PowerPolicyDecoder.DescribeAspm(ReadAc(s, PciExpressSubgroup, AspmPolicy)));
                settings.Add(PowerPolicyDecoder.DescribeUsbSuspend(ReadAc(s, UsbSubgroup, UsbSelectiveSuspend)));
            }
            else settings.Add(new PowerPolicyRow
            {
                Name = "電源計劃", Value = "—",
                Note = "PowerGetActiveScheme 失敗，無法讀取任何計劃設定。",
            });
        }
        finally { if (scheme != 0) LocalFree(scheme); }

        var sleep = ReadSleepStates();
        string note = cores.Count > 0
            ? $"已讀取 {cores.Count} 顆邏輯處理器的電源狀態、{settings.Count} 項計劃設定、{sleep.Count} 項平台能力。全部唯讀。"
            : "逐核電源資訊讀不到（CallNtPowerInformation 失敗），其餘欄位仍照實顯示。";
        return (plan, summary, mhzNotice, settings, sleep, note);
    }

    // ── CallNtPowerInformation ────────────────────────────────────────────────

    [DllImport("powrprof.dll")]
    private static extern int CallNtPowerInformation(int level, nint inBuf, uint inSize, nint outBuf, uint outSize);

    private const int ProcessorInformation = 11;
    private const int SystemPowerCapabilities = 4;

    /// <summary>PROCESSOR_POWER_INFORMATION 為六個 ULONG。</summary>
    private const int ProcessorPowerRecordSize = 24;

    private static List<ProcessorPowerSample> ReadProcessorPower()
    {
        int count = Environment.ProcessorCount;
        uint len = (uint)(ProcessorPowerRecordSize * count);
        nint buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (CallNtPowerInformation(ProcessorInformation, 0, 0, buf, len) != 0) return [];
            var list = new List<ProcessorPowerSample>(count);
            for (int i = 0; i < count; i++)
            {
                nint p = buf + i * ProcessorPowerRecordSize;
                list.Add(new ProcessorPowerSample(
                    unchecked((uint)Marshal.ReadInt32(p)),
                    unchecked((uint)Marshal.ReadInt32(p + 4)),
                    unchecked((uint)Marshal.ReadInt32(p + 8)),
                    unchecked((uint)Marshal.ReadInt32(p + 12)),
                    unchecked((uint)Marshal.ReadInt32(p + 16)),
                    unchecked((uint)Marshal.ReadInt32(p + 20))));
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// SYSTEM_POWER_CAPABILITIES 直接以位元組位移讀取。
    /// 這個結構前 24 個位元組全是 1 位元組欄位，用位移比宣告整個結構安全——
    /// 後半段有 BATTERY_REPORTING_SCALE 陣列與列舉，宣告錯一個欄位就會讀到垃圾。
    /// </summary>
    private static List<PowerPolicyRow> ReadSleepStates()
    {
        const int bufSize = 256;
        nint buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            if (CallNtPowerInformation(SystemPowerCapabilities, 0, 0, buf, bufSize) != 0)
                return [new PowerPolicyRow
                {
                    Name = "平台睡眠能力", Value = "—",
                    Note = "CallNtPowerInformation(SystemPowerCapabilities) 失敗。",
                }];

            bool B(int offset) => Marshal.ReadByte(buf + offset) != 0;
            return PowerPolicyDecoder.DescribeSleepStates(
                s1: B(3), s2: B(4), s3: B(5), s4: B(6), s5: B(7),
                hiberFile: B(8), fastS4: B(17), aoac: B(20));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ── PowerReadACValueIndex（目前電源計劃的設定索引）─────────────────────────

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(nint rootPowerKey, ref Guid scheme,
                                                     ref Guid subgroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerReadFriendlyName(nint rootPowerKey, ref Guid scheme,
                                                     nint subgroup, nint setting, byte[]? buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint mem);

    // 官方 GUID（winnt.h／powrprof.h）
    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ThrottleMinimum = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid ThrottleMaximum = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid CoreParkingMaxCores = new("ea062031-0e34-4ff1-9b6d-eb1059334028");
    private static readonly Guid CoreParkingMinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");
    private static readonly Guid PciExpressSubgroup = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    private static readonly Guid AspmPolicy = new("ee12f906-d277-404b-b6da-e5fa1a576df5");
    private static readonly Guid UsbSubgroup = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid UsbSelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    /// <summary>讀一項交流電（插電）設定索引；讀不到回 null，由解碼器照實說「—」。</summary>
    private static uint? ReadAc(Guid scheme, Guid subgroup, Guid setting)
        => PowerReadACValueIndex(0, ref scheme, ref subgroup, ref setting, out uint v) == 0 ? v : null;

    private static string? ReadFriendlyName(nint scheme)
    {
        Guid s = Marshal.PtrToStructure<Guid>(scheme);
        uint size = 0;
        PowerReadFriendlyName(0, ref s, 0, 0, null, ref size);
        if (size == 0) return null;
        var buf = new byte[size];
        if (PowerReadFriendlyName(0, ref s, 0, 0, buf, ref size) != 0) return null;
        return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }
}
