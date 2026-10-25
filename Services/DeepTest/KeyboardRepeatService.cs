using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// U10-TS-0146「鍵盤重複率/延遲實測（OS 設定 vs 實測）」的 OS 設定面：<c>ts.kbd.repeat</c>。
/// </summary>
/// <remarks>
/// <para>
/// 來源：Windows <c>SystemParametersInfo</c>（usermode 唯讀、零特權）——
/// <c>SPI_GETKEYBOARDSPEED</c>（重複速度索引 0–31；文件端點 0→2.5 字符/秒、31→30 字符/秒，
/// 中間值 OS 未承諾線性對照，故只報索引與兩個文件端點）、<c>SPI_GETKEYBOARDDELAY</c>
/// （重複延遲索引 0–3 → 250/500/750/1000 ms，文件明載線性）、
/// <c>SPI_GETFILTERKEYS</c>（篩選鍵開關與接受/重複毫秒——它會直接改變按鍵重複行為）。
/// </para>
/// <para>
/// 誠實界線：本層只收<b>OS 設定</b>；「實測」需要真實按鍵（互動層，不注入任何輸入）。
/// 兩者分列，不以設定值冒充實測值。
/// </para>
/// </remarks>
public static class KeyboardRepeatService
{
    private const string Category = "輸入";
    public const string Key = "ts.kbd.repeat";
    public const string Source = "Windows SystemParametersInfo（SPI_GETKEYBOARDSPEED／SPI_GETKEYBOARDDELAY／SPI_GETFILTERKEYS；唯讀）";

    /// <summary>OS 設定取樣。</summary>
    public sealed record KeyboardRepeatSample(int SpeedIndex, int DelayIndex,
        bool FilterKeysEnabled, int FilterKeysAcceptanceMs, int FilterKeysRepeatMs);

    /// <summary>收集鍵盤重複設定。測試以注入取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        KeyboardRepeatSample? probe = null)
    {
        KeyboardRepeatSample sample;
        try { sample = probe ?? RealRead(); }
        catch (Exception ex)
        {
            return [new HardwareFact(Key, Category, "鍵盤重複設定", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "SystemParametersInfo 讀取失敗：" + ex.Message)];
        }
        return [BuildFact(at, sample)];
    }

    /// <summary>純函式：設定樣本 → 事實。</summary>
    [SpecRef("Microsoft Learn SystemParametersInfoA/W：SPI_GETKEYBOARDSPEED 範圍 0–31（0→2.5 字符/秒、31→30 字符/秒）；"
        + "SPI_GETKEYBOARDDELAY 範圍 0–3（250/500/750/1000 ms）；winuser.h FILTERKEYS（FKF_FILTERKEYSON 0x1、iWaitMSec／iRepeatMSec）")]
    public static HardwareFact BuildFact(DateTimeOffset at, KeyboardRepeatSample sample)
    {
        if (sample.SpeedIndex is < 0 or > 31 || sample.DelayIndex is < 0 or > 3)
            return new HardwareFact(Key, Category, "鍵盤重複設定",
                $"速度索引 {sample.SpeedIndex}、延遲索引 {sample.DelayIndex}", "", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.Unknown,
                "索引越界（速度應 0–31、延遲應 0–3）——來源可疑，不得當已確認值使用");
        string delayText = sample.DelayIndex switch
        {
            0 => "250", 1 => "500", 2 => "750", _ => "1000",
        };
        string value = $"重複延遲 {delayText} ms（索引 {sample.DelayIndex}）；重複速度索引 {sample.SpeedIndex}"
            + $"（文件端點：0→2.5、31→30 字符/秒）"
            + (sample.FilterKeysEnabled
                ? $"；篩選鍵開啟（接受延遲 {sample.FilterKeysAcceptanceMs} ms、重複間隔 {sample.FilterKeysRepeatMs} ms——實際重複行為由篩選鍵接管）"
                : "；篩選鍵關閉")
            + "；設定值非實測值——實測需真實按鍵，屬互動層";
        return new HardwareFact(Key, Category, "鍵盤重複設定", value, "", Source,
            FactTrustLevel.Reported, false, at, null, FactAvailability.Present, null);
    }

    private static KeyboardRepeatSample RealRead()
    {
        if (SystemParametersInfo(SpiGetKeyboardSpeed, 0, out uint speed, 0) == 0)
            throw new InvalidOperationException("SPI_GETKEYBOARDSPEED 失敗");
        if (SystemParametersInfo(SpiGetKeyboardDelay, 0, out uint delay, 0) == 0)
            throw new InvalidOperationException("SPI_GETKEYBOARDDELAY 失敗");
        var fk = new FilterKeys();
        fk.cbSize = (uint)Marshal.SizeOf<FilterKeys>();
        if (SystemParametersInfo(SpiGetFilterKeys, (uint)Marshal.SizeOf<FilterKeys>(), ref fk, 0) == 0)
            throw new InvalidOperationException("SPI_GETFILTERKEYS 失敗");
        return new KeyboardRepeatSample((int)speed, (int)delay,
            (fk.Flags & 0x1 /*FKF_FILTERKEYSON*/) != 0, (int)fk.WaitMSec, (int)fk.RepeatMSec);
    }

    private const uint SpiGetKeyboardSpeed = 0x0B;
    private const uint SpiGetKeyboardDelay = 0x16;
    private const uint SpiGetFilterKeys = 0x26;

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterKeys
    {
        public uint cbSize;
        public uint Flags;
        public uint WaitMSec;
        public uint DelayMSec;
        public uint RepeatMSec;
        public uint BounceMSec;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern int SystemParametersInfo(uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = false, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern int SystemParametersInfo(uint uiAction, uint uiParam, ref FilterKeys pvParam, uint fWinIni);
}
