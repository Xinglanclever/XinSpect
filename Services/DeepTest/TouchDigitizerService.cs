using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// U10-TS-0144「觸控螢幕多點上限測試」的資料面：<c>ts.touch.maxpoints</c>。
/// </summary>
/// <remarks>
/// <para>
/// 來源：Windows API（usermode 唯讀、零特權）——<c>GetSystemMetrics(SM_DIGITIZER)</c> 的能力旗標
/// 與 <c>GetSystemMetrics(SM_MAXIMUMTOUCHES)</c> 的最大觸控點數。
/// </para>
/// <para>
/// 誠實界線：本層回報的是<b>系統宣告的多點能力</b>；「真的同時按幾點都收得到」屬互動測試
/// （UI 層）。旗標 0＝系統沒有回報任何數位板能力，標 NotSupported——「沒有觸控」是環境事實；
/// 旗標就緒但最大點數為 0 來源可疑，標 Unknown 而非拿 0 冒充。
/// </para>
/// </remarks>
public static class TouchDigitizerService
{
    private const string Category = "輸入";
    public const string Key = "ts.touch.maxpoints";
    public const string Source = "Windows GetSystemMetrics（SM_DIGITIZER／SM_MAXIMUMTOUCHES；唯讀）";

    /// <summary>收集觸控數位板能力。測試以注入取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        (int DigitizerFlags, int MaxTouches)? probe = null)
    {
        int flags, maxTouches;
        try { (flags, maxTouches) = probe ?? RealRead(); }
        catch (Exception ex)
        {
            return [new HardwareFact(Key, Category, "觸控多點能力", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "GetSystemMetrics 讀取失敗：" + ex.Message)];
        }
        return [BuildFact(at, flags, maxTouches)];
    }

    /// <summary>純函式：旗標與點數 → 事實。</summary>
    [SpecRef("Windows SDK um/winuser.h：GetSystemMetrics 索引 SM_DIGITIZER(94)／SM_MAXIMUMTOUCHES(95)；"
        + "SM_DIGITIZER 回傳值旗標 NID_INTEGRATED_TOUCH 0x1、NID_EXTERNAL_TOUCH 0x2、NID_INTEGRATED_PEN 0x4、"
        + "NID_EXTERNAL_PEN 0x8、NID_MULTI_INPUT 0x40、NID_READY 0x80")]
    public static HardwareFact BuildFact(DateTimeOffset at, int digitizerFlags, int maxTouches)
    {
        if (digitizerFlags == 0)
            return new HardwareFact(Key, Category, "觸控多點能力", "", "", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.NotSupported,
                "SM_DIGITIZER 回報 0——系統未宣告任何觸控/數位板能力（無觸控是環境事實，不是錯誤）");
        var parts = new List<string>();
        if ((digitizerFlags & 0x1) != 0) parts.Add("內建觸控");
        if ((digitizerFlags & 0x2) != 0) parts.Add("外接觸控");
        if ((digitizerFlags & 0x4) != 0) parts.Add("內建筆");
        if ((digitizerFlags & 0x8) != 0) parts.Add("外接筆");
        if ((digitizerFlags & 0x40) != 0) parts.Add("多點");
        bool ready = (digitizerFlags & 0x80) != 0;
        if (!ready)
            return new HardwareFact(Key, Category, "觸控多點能力",
                $"旗標 {string.Join("・", parts)}，但 NID_READY 未就緒（數位板未準備好）", "", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.Unknown,
                "能力旗標有值但未就緒——最大觸控點數不可信，不以 0 冒充");
        if (maxTouches <= 0)
            return new HardwareFact(Key, Category, "觸控多點能力",
                $"旗標 {string.Join("・", parts)} 就緒，但 SM_MAXIMUMTOUCHES 回 {maxTouches}", "", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.Unknown,
                "就緒旗標與零點數矛盾——來源可疑，不得當已確認值使用");
        return new HardwareFact(Key, Category, "觸控多點能力",
            $"最大觸控點數 {maxTouches}；旗標 {string.Join("・", parts)}", "點", Source,
            FactTrustLevel.Reported, false, at, maxTouches, FactAvailability.Present, null);
    }

    private static (int, int) RealRead() =>
        (GetSystemMetrics(SmDigitizer), GetSystemMetrics(SmMaximumTouches));

    private const int SmDigitizer = 94;
    private const int SmMaximumTouches = 95;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
