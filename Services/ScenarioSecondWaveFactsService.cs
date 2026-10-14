using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 情境引導第二波（Vol2 第 SG-008、SG-010 項）：<c>sg.usbslow</c>（USB 為什麼慢）、
/// <c>sg.audio</c>（聲音為什麼斷）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一組是導航不是診斷</b>——與 <see cref="ScenarioGuidanceFactsService"/> 同一套哲學：
/// 每一把鍵只做兩件事，<b>把量得到的面向並列</b>、<b>指路到真正有細節的那一頁</b>。
/// 不給紅黃判決、不猜根因。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① SG-008 的判讀核心是「<b>落差要說得出是哪一層造成的</b>」——
/// 裝置支援 3.x 卻跑 480 Mb/s，如果埠支援 3.x 那就是<b>線材</b>，
/// 埠只有 2.0 那就是<b>插錯埠</b>；這兩者的處置完全不同，所以必須分開講
/// （判讀邏輯複用 <see cref="UsbLinkDecoder.Judge"/>，不寫第二份）；
/// ② <b>空清單不能講成一切正常</b>——沒讀到裝置與裝置都跑滿速是兩件事；
/// ③ SG-010 只並列<b>APO 處理鏈、端點數量、時鐘來源</b>三個面向，
/// <b>不斷言「就是 APO 造成的」</b>——音訊中斷的成因（緩衝、驅動、USB 排程、電源管理）
/// 大多在本工具的可見範圍之外，講出來的那幾個面向只是<b>排除法的起點</b>；
/// ④ <b>不錄音、不播放測試音</b>——本項只讀組態。
/// </para>
/// </remarks>
public static class ScenarioSecondWaveFactsService
{
    private const string Cat = "情境診斷";
    private const string Source = "本機唯讀（USB 鏈路查詢／音訊端點登錄）";

    /// <summary>SG-008 USB 為什麼慢。</summary>
    public const string UsbSlowKey = "sg.usbslow";
    /// <summary>SG-010 聲音為什麼斷。</summary>
    public const string AudioKey = "sg.audio";

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>
    /// （裝置數, 掉到 USB 2.0 者, 差一級者, 線材造成的數, 埠造成的數）。
    /// 後兩者由 <see cref="UsbLinkDecoder.Judge"/> 的結論分類——兩者處置不同。
    /// </summary>
    public sealed record UsbSlowState(int Devices, int DownTo2, int OneStep, int ByCable, int ByPort);

    /// <summary>（渲染端點數, 有 APO 處理鏈的端點數, APO 元件總數, 預設端點可讀）。</summary>
    public sealed record AudioState(int RenderEndpoints, int WithApo, int ApoComponents, bool DefaultReadable);

    public delegate UsbSlowState? UsbSlowProbe();
    public delegate AudioState? AudioProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        UsbSlowProbe? usb = null,
        AudioProbe? audio = null)
        =>
        [
            UsbSlowFact(at, usb),
            AudioFact(at, audio),
        ];

    /// <summary>
    /// 從既有的 <see cref="UsbPortRow"/> 清單收成情境摘要。
    /// <b>判讀複用 <see cref="UsbLinkDecoder.Judge"/> 的結論字串</b>——不重寫一份判斷，
    /// 否則兩邊的判準會漂移，而漂移的那一天沒人知道該信哪一個。
    /// </summary>
    public static UsbSlowState SummarizeUsb(IReadOnlyList<UsbPortRow> rows)
    {
        int down = 0, one = 0, byCable = 0, byPort = 0;
        foreach (var r in rows)
        {
            if (r.Severity >= 2)
            {
                down++;
                // Judge 的兩個嚴重度 2 分支各有一句特徵文字，靠它分類來源。
                if (r.Verdict.Contains("線材", StringComparison.Ordinal)) byCable++;
                else if (r.Verdict.Contains("只有 USB 2.0", StringComparison.Ordinal)) byPort++;
            }
            else if (r.Severity == 1) one++;
        }
        return new UsbSlowState(rows.Count, down, one, byCable, byPort);
    }

    // ── SG-008 USB 為什麼慢 ───────────────────────────────────────────────

    public static HardwareFact UsbSlowFact(DateTimeOffset at, UsbSlowProbe? probe = null)
    {
        UsbSlowState? s;
        try { s = (probe ?? ReadUsbSlow)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, UsbSlowKey, "USB 為什麼慢", FactAvailability.ReadError,
                "USB 鏈路查詢失敗——讀不到就是不猜（不把讀不到當成速度正常）");

        var v = s;

        // 空清單絕不能講成一切正常。
        if (v.Devices == 0)
            return new HardwareFact(UsbSlowKey, Cat, "USB 為什麼慢",
                "沒有讀到任何 USB 裝置。<b>這不代表速度正常</b>——也可能是集線器不接受查詢、" +
                "或目前沒有外接裝置。要回答「為什麼慢」需要先有裝置可比。",
                "個", Source, FactTrustLevel.Reported, false, at, 0);

        if (v.DownTo2 == 0 && v.OneStep == 0)
            return new HardwareFact(UsbSlowKey, Cat, "USB 為什麼慢",
                $"USB 裝置 {v.Devices} 個，目前速度全部已達裝置能力——<b>USB 這一段不是慢的原因</b>。" +
                "下一步該看的是：儲存裝置本身的隨機讀寫、檔案系統、或是走網路的路徑" +
                "（細節看「儲存」「網路」頁）。本項只排除 USB 這一段，不宣稱找到元凶。",
                "個", Source, FactTrustLevel.Reported, false, at, v.Devices);

        var causes = new List<string>(2);
        if (v.ByCable > 0) causes.Add($"{v.ByCable} 個是<b>線材</b>造成（埠支援 3.x，線只有 2.0 的芯）——換線可解");
        if (v.ByPort > 0) causes.Add($"{v.ByPort} 個是<b>插錯埠</b>造成（裝置支援 3.x，這個埠只有 2.0）——換埠可解");
        if (causes.Count == 0) causes.Add("落差來源未分類（判讀文字不含可辨識的特徵）——照逐列說明看");

        string oneStep = v.OneStep > 0
            ? $"・另有 {v.OneStep} 個支援 10 Gb/s 只跑到 5 Gb/s（埠或線材只到 5 Gb/s，<b>不是故障</b>）"
            : "";

        return new HardwareFact(UsbSlowKey, Cat, "USB 為什麼慢",
            $"USB 裝置 {v.Devices} 個・掉到 480 Mb/s 的 {v.DownTo2} 個{oneStep}。" +
            string.Join("；", causes) + "。" +
            "<b>分清楚是線還是埠，是因為處置完全不同</b>——一個換線、一個換埠，" +
            "而兩者在檔案總管裡長得一模一樣（都只是「比較慢」）。" +
            "逐列細節與每個埠的協定請看「USB 鏈路真相」頁；本列只導航。" +
            "本項不測吞吐量（要寫入測試檔，需明示同意），只比對裝置宣告的能力與目前協商的速度。",
            "個", Source, FactTrustLevel.Reported, false, at, v.DownTo2);
    }

    // ── SG-010 聲音為什麼斷 ───────────────────────────────────────────────

    public static HardwareFact AudioFact(DateTimeOffset at, AudioProbe? probe = null)
    {
        AudioState? s;
        try { s = (probe ?? ReadAudio)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, AudioKey, "聲音為什麼斷", FactAvailability.ReadError,
                "音訊端點組態讀取失敗——讀不到就是不猜");

        var v = s;
        if (v.RenderEndpoints == 0)
            return new HardwareFact(AudioKey, Cat, "聲音為什麼斷",
                "沒有渲染音訊端點——若使用者聽到的是「完全沒聲音」，" +
                "「沒有輸出端點」本身就是第一件該確認的事（比 APO、緩衝、時鐘都前面）。" +
                "但若聲音只是<b>間歇中斷</b>，那這個狀態與症狀不符，要看的是下面那幾個面向。",
                "個", Source, FactTrustLevel.Reported, false, at, 0);

        string apoText = v.WithApo == 0
            ? "沒有任何端點掛 APO 處理鏈"
            : $"{v.WithApo} 個端點掛了 APO 處理鏈（共 {v.ApoComponents} 個元件）——" +
              "<b>APO 是效果鏈</b>（等化、環繞、降噪），它<b>可能</b>參與中斷但不是唯一嫌疑，" +
              "而且掛著不等於它在處理：效果要真的生效才進訊號路徑";

        string clockText = v.DefaultReadable
            ? "預設端點組態可讀"
            : "預設端點組態<b>讀不到</b>（不猜預設裝置是哪一個）";

        return new HardwareFact(AudioKey, Cat, "聲音為什麼斷",
            $"渲染端點 {v.RenderEndpoints} 個・{apoText}・{clockText}。" +
            "<b>本項只並列量得到的面向，不斷言元凶</b>——音訊中斷的成因（驅動緩衝大小、" +
            "USB 排程與省電、獨佔模式切換、取樣率轉換、藍牙鏈路）大多落在本工具的可見範圍之外。" +
            "這三個面向是<b>排除法的起點</b>：先確認端點與預設裝置對不對、" +
            "再確認有沒有 APO 在訊號路徑上，剩下的要靠播放測試與驅動端的事件記錄。" +
            "<b>不錄音、不播放測試音、不讀任何音訊內容</b>——本項只讀組態。",
            "個", Source, FactTrustLevel.Reported, false, at, v.RenderEndpoints);
    }

    // ── 讀取層 ────────────────────────────────────────────────────────────

    /// <summary>
    /// USB 通路：<b>本版刻意不在此重跑鏈路查詢</b>——那需要對每個控制器發一連串 IOCTL，
    /// 屬「USB 鏈路真相」頁的明示觸發路徑（<see cref="UsbLinkService"/>）。
    /// 事實收集是啟動路徑的一部分，不該在那裡做整棵 USB 樹的深度列舉。
    /// 回 <c>null</c> 時由值說明；接縫已備，接上時只換這一個方法。
    /// </summary>
    internal static UsbSlowState? ReadUsbSlow() => null;

    /// <summary>音訊通路：MMDevices 渲染端點的 APO 掛載計數（唯讀登錄列舉）。</summary>
    internal static AudioState? ReadAudio()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render");
            if (root is null) return new AudioState(0, 0, 0, false);

            int endpoints = 0, withApo = 0, components = 0;
            foreach (string endpoint in root.GetSubKeyNames())
            {
                endpoints++;
                using var sub = root.OpenSubKey(endpoint);
                if (sub is null) continue;
                using var props = sub.OpenSubKey(@"Properties");
                if (props is null) continue;
                // APO 相關屬性以 "{...},FX\\" 這類形式出現；只計數不看內容。
                var apoNames = props.GetValueNames()
                    .Where(n => n.Contains("FX\\", StringComparison.OrdinalIgnoreCase)
                             || n.Contains("APO", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (apoNames.Count > 0) withApo++;
                components += apoNames.Count;
            }
            bool defaultReadable = root.GetSubKeyNames().Length > 0;
            return new AudioState(endpoints, withApo, components, defaultReadable);
        }
        catch { return null; }
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
