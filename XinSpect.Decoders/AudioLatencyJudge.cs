using System;
using System.Collections.Generic;
using System.Globalization;

namespace XinSpect;

/// <summary>一個音訊輸出端點的緩衝區實況。讀不到為 0／空。</summary>
/// <param name="Name">端點名稱。</param>
/// <param name="IsDefault">是否為目前的預設輸出。</param>
/// <param name="MixSampleRate">引擎混合格式的取樣率（Hz）；0＝讀不到。</param>
/// <param name="MixBits">引擎混合格式的位元深度；0＝讀不到。</param>
/// <param name="MixChannels">引擎混合格式的聲道數；0＝讀不到。</param>
/// <param name="ExclusiveBufferMs">獨占模式實際配到的緩衝區（毫秒）；0＝配不出來。</param>
/// <param name="ExclusiveFrames">獨占模式緩衝區的框架數；0＝配不出來。</param>
/// <param name="RequestedMs">要求的最小週期（毫秒）；0＝未指定（用裝置預設）。</param>
/// <param name="SharedBufferMs">共用模式實際配到的緩衝區（毫秒）；0＝配不出來。</param>
/// <param name="ExclusiveError">獨占模式配置失敗的錯誤碼（十六進位字串）；成功為空。</param>
/// <param name="NameSuggestsSoftware">名稱本身透露是軟體端點（含 Virtual／Cable／Streaming 等字樣）。</param>
public readonly record struct AudioEndpointSample(
    string Name,
    bool IsDefault,
    int MixSampleRate,
    int MixBits,
    int MixChannels,
    double ExclusiveBufferMs,
    int ExclusiveFrames,
    double RequestedMs,
    double SharedBufferMs,
    string ExclusiveError,
    bool NameSuggestsSoftware);

/// <summary>
/// 音訊端點緩衝區的判讀：把「這個端點能跑多小的緩衝區」與「這件事對專業用途的意義」講開。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼緩衝區大小是專業音訊的第一個數字：</b>緩衝區決定來回延遲（round-trip latency）的下限。
/// 錄音時要一邊聽自己的聲音、一邊演奏，監聽延遲超過十幾毫秒就會明顯干擾演奏；
/// 一般消費用途（看影片、聽音樂）幾十毫秒完全無感。<b>同一個數字在兩種用途下的意義完全不同</b>，
/// 所以判讀必須先問用途再給結論。
/// </para>
/// <para>
/// <b>獨占模式與共用模式的差別：</b>共用模式下音訊會經過 Windows 的引擎混音，緩衝區由引擎決定，
/// 應用程式無法縮小；獨占模式讓應用程式直接對裝置說話，可以要求更小的週期。
/// 因此<b>獨占模式配到的緩衝區才是「這個端點在專業用途下的下限」</b>，
/// 共用模式那個數字是所有應用程式共享的、不能拿來當延遲指標。
/// </para>
/// <para>
/// <b>Windows 會把過小的要求往上夾：</b>要求 1 ms 與要求 20 ms 常常得到同一個值——
/// 那是裝置（或驅動）實際能提供的最小週期。判讀會把「要求值」與「實配值」並列，
/// 並在兩者差距大時說明那是裝置夾住的結果，不是應用程式的選擇。
/// </para>
/// <para><b>本判讀只描述緩衝區配置，不量音質、不量 DAC、也不判斷端點的好壞。</b></para>
/// </remarks>
public static class AudioLatencyJudge
{
    /// <summary>延遲等級。</summary>
    public enum LatencyTier
    {
        /// <summary>獨占模式可配到 10 ms 以下——適合即時監聽與演奏。</summary>
        Realtime,
        /// <summary>10–20 ms——單向錄音可行，邊聽邊彈會有一點感覺。</summary>
        LowLatency,
        /// <summary>20–40 ms——一般錄音可接受，即時監聽不適合。</summary>
        Standard,
        /// <summary>40 ms 以上——只適合播放，不適合任何即時用途。</summary>
        PlaybackOnly,
        /// <summary>配不出來（裝置被獨占、格式不支援、或驅動拒絕）。</summary>
        Unavailable,
        /// <summary>讀不到足夠欄位，如實不判。</summary>
        Unknown,
    }

    /// <summary>判讀結果。</summary>
    /// <param name="Tier">延遲等級。</param>
    /// <param name="Headline">一行結論。</param>
    /// <param name="Evidence">依據：實際配到的值與要求值的關係。</param>
    /// <param name="SoftwareEndpoint">這是軟體端點（由名稱推得，非匯流排查證）。</param>
    public readonly record struct Verdict(
        LatencyTier Tier, string Headline, string Evidence, bool SoftwareEndpoint);

    /// <summary>即時監聽的門檻（毫秒）。業界常用的分界，非規格值。</summary>
    public const double RealtimeThresholdMs = 10.0;
    /// <summary>低延遲的門檻（毫秒）。</summary>
    public const double LowLatencyThresholdMs = 20.0;
    /// <summary>播放可接受的門檻（毫秒）。</summary>
    public const double StandardThresholdMs = 40.0;

    /// <summary>判讀一個端點。</summary>
    [SpecRef("Windows Core Audio 的 IAudioClient::Initialize（AUDCLNT_SHAREMODE_EXCLUSIVE／SHARED）與 GetBufferSize／GetDevicePeriod：獨占模式下應用程式可直接指定週期，共用模式下由音訊引擎決定且所有應用程式共享。要求的週期小於裝置可提供值時由裝置端夾住，不報錯。判讀僅讀取配置結果，不啟動串流、不改變任何裝置設定。")]
    public static Verdict Judge(AudioEndpointSample s)
    {
        if (s.Name.Length == 0)
            return new Verdict(LatencyTier.Unknown, "—（沒有端點名稱）", "名稱為空，無從判讀。", false);

        string softNote = s.NameSuggestsSoftware
            ? "（名稱本身透露這是軟體端點——此判斷依名稱字樣，未經匯流排查證）"
            : "";

        if (s.ExclusiveBufferMs <= 0)
        {
            string why = s.ExclusiveError.Length > 0
                ? $"配置失敗（{s.ExclusiveError}）"
                : "沒有回報緩衝區大小";
            return new Verdict(LatencyTier.Unavailable,
                $"—（獨占模式{why}）",
                $"{s.Name}{softNote}：{why}。常見原因是裝置已被另一個應用程式獨占、"
                + "要求的格式不支援，或驅動不允許獨占。這一項讀不到時如實標示，不推測它的延遲。",
                s.NameSuggestsSoftware);
        }

        string requested = s.RequestedMs > 0
            ? $"要求 {s.RequestedMs:0.#} ms"
            : "未指定週期（用裝置預設）";
        string clamping = s.RequestedMs > 0 && s.ExclusiveBufferMs > s.RequestedMs * 1.5
            ? $"，實際配到 {s.ExclusiveBufferMs:0.#} ms——要求值被裝置夾住了，"
              + "那是這個端點能提供的最小週期，不是應用程式選的"
            : "";
        string shared = s.SharedBufferMs > 0
            ? $"，共用模式 {s.SharedBufferMs:0.#} ms（引擎決定、所有應用程式共享，不能當延遲指標）"
            : "";
        string mix = s.MixSampleRate > 0
            ? $"引擎混合格式 {s.MixSampleRate} Hz／{s.MixBits} 位元／{s.MixChannels} 聲道"
            : "引擎混合格式讀不到";

        string core = $"{s.Name}{softNote}：{mix}；獨占模式 {requested}、"
                    + $"實際 {s.ExclusiveBufferMs:0.#} ms（{s.ExclusiveFrames} 個框架）"
                    + clamping + shared + "。";

        if (s.ExclusiveBufferMs < RealtimeThresholdMs)
            return new Verdict(LatencyTier.Realtime,
                $"{s.ExclusiveBufferMs:0.#} ms——可做即時監聽",
                core + $"低於 {RealtimeThresholdMs:0} ms 的獨占緩衝區足以邊聽邊演奏："
                     + "監聽延遲在這個量級下不會干擾演奏，這是專業音訊介面該有的水準。",
                s.NameSuggestsSoftware);

        if (s.ExclusiveBufferMs < LowLatencyThresholdMs)
            return new Verdict(LatencyTier.LowLatency,
                $"{s.ExclusiveBufferMs:0.#} ms——低延遲，單向錄音可行",
                core + $"{RealtimeThresholdMs:0}–{LowLatencyThresholdMs:0} ms 之間："
                     + "單向錄音沒有問題，邊聽邊彈會有一點延遲感；"
                     + "要更小需要音訊介面的原生 ASIO 驅動（Windows 的音訊引擎本身有下限）。",
                s.NameSuggestsSoftware);

        if (s.ExclusiveBufferMs < StandardThresholdMs)
            return new Verdict(LatencyTier.Standard,
                $"{s.ExclusiveBufferMs:0.#} ms——一般用途沒問題，即時監聽不適合",
                core + $"落在 {LowLatencyThresholdMs:0}–{StandardThresholdMs:0} ms："
                     + "播放、看影片、語音通話都完全足夠，但即時監聽（邊聽邊彈）會明顯干擾演奏。"
                     + "這個量級常見於內建音效與 Windows 音訊引擎的預設週期。",
                s.NameSuggestsSoftware);

        return new Verdict(LatencyTier.PlaybackOnly,
            $"{s.ExclusiveBufferMs:0.#} ms——只適合播放",
            core + $"超過 {StandardThresholdMs:0} ms："
                 + "這個緩衝區只適合播放與一般消費用途。"
                 + "大緩衝區本身不是故障——軟體端點與部分無線裝置本來就會用大緩衝來換取穩定；"
                 + "但要用它做任何即時用途（錄音、演奏、監聽）是不可行的。",
            s.NameSuggestsSoftware);
    }

    /// <summary>整組端點的摘要：預設輸出是哪一個、能跑多小的緩衝區。</summary>
    [SpecRef("同上：以獨占模式配置到的緩衝區大小作為端點延遲能力的指標，並區分硬體端點與名稱透露為軟體的端點。計數與比較僅陳述配置結果，不對音質下結論。")]
    public static string Summarize(IReadOnlyList<AudioEndpointSample> endpoints)
    {
        if (endpoints.Count == 0)
            return "沒有讀到任何音訊輸出端點——這通常是查詢失敗或機器沒有音訊裝置，不代表設定有問題。";

        AudioEndpointSample? best = null;
        int usable = 0;
        foreach (var e in endpoints)
        {
            if (e.ExclusiveBufferMs <= 0) continue;
            usable++;
            if (best is null || e.ExclusiveBufferMs < best.Value.ExclusiveBufferMs) best = e;
        }

        // AudioEndpointSample 是 readonly record struct：FirstOrDefault 找不到時回的是
        // default(struct)，Name 會是 null 而不是空字串——直接讀 .Length 會炸。
        AudioEndpointSample? def = null;
        foreach (var e in endpoints)
            if (e.IsDefault) { def = e; break; }
        string defText = def is { } d && d.Name.Length > 0
            ? $"目前的預設輸出是「{d.Name}」"
              + (d.ExclusiveBufferMs > 0
                 ? $"（獨占模式 {d.ExclusiveBufferMs:0.#} ms）"
                 : "（獨占模式配不出來）")
            : "沒有回報預設輸出";

        if (best is null)
            return $"共 {endpoints.Count} 個輸出端點，但沒有任何一個能配置獨占模式——"
                 + "無從比較延遲。{defText}。";

        string tier = best.Value.ExclusiveBufferMs < RealtimeThresholdMs
            ? "可做即時監聽"
            : best.Value.ExclusiveBufferMs < LowLatencyThresholdMs ? "低延遲"
            : best.Value.ExclusiveBufferMs < StandardThresholdMs ? "一般用途"
            : "只適合播放";

        return $"共 {endpoints.Count} 個輸出端點，其中 {usable} 個能配置獨占模式；"
             + $"最小的是「{best.Value.Name}」（{best.Value.ExclusiveBufferMs:0.#} ms，{tier}）。"
             + $"{defText}。數字是獨占模式實際配到的緩衝區，代表這個端點的延遲下限；"
             + "共用模式那個值由引擎決定、所有應用程式共享，不能拿來當延遲指標。";
    }
}
