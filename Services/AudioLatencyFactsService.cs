using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace XinSpect;

/// <summary>
/// 音訊端點緩衝區的<b>讀取</b>層：對每個輸出端點實際配置一次獨占模式與共用模式，量出它配得到的緩衝區。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要真的配置一次：</b>「這個端點能跑多小的緩衝區」沒有查詢 API——只能試。
/// Windows 會把過小的要求往上夾到裝置能提供的最小週期，所以要求 1 ms 與要求 20 ms
/// 常常得到同一個值；那個值才是端點的延遲下限。<b>只讀格式而不嘗試配置，是問不出來的。</b>
/// </para>
/// <para>
/// <b>安全性：</b>只呼叫 <c>IAudioClient::Initialize</c> 取得緩衝區大小後立刻 <c>Dispose</c>，
/// <b>不呼叫 Start</b>——不送出任何音訊、不佔用裝置、不改系統音量或預設裝置。
/// 這與「播放靜音」是兩件事：這裡連串流都沒有啟動。
/// </para>
/// <para>
/// 每個端點都用<b>新的 AudioClient</b>：同一個 client 實例重複 Initialize 會因為前一次的
/// 狀態殘留而失敗（實測會拿到 NullReferenceException），這是 NAudio 包裝層的行為。
/// </para>
/// </remarks>
public static class AudioLatencyFactsService
{
    private const string Category = "音訊";

    /// <summary>收集音訊端點緩衝區事實。測試以注入樣本取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<AudioEndpointSample>>? probe = null)
    {
        IReadOnlyList<AudioEndpointSample> endpoints;
        try { endpoints = (probe ?? ReadAll)(); }
        catch (Exception ex)
        {
            return
            [
                new HardwareFact("audio.latency", Category, "音訊端點緩衝區", "", "",
                    "Windows Core Audio（IAudioClient）", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.ReadError, "讀取失敗：" + ex.Message),
            ];
        }

        if (endpoints.Count == 0)
            return
            [
                new HardwareFact("audio.latency", Category, "音訊端點緩衝區", "", "",
                    "Windows Core Audio（IAudioClient）", FactTrustLevel.Unknown, false, at, null,
                    FactAvailability.NotApplicable,
                    "本機沒有作用中的音訊輸出端點——沒有端點可配置，不是設定有問題"),
            ];

        var list = new List<HardwareFact>
        {
            new("audio.latency", Category, "音訊端點緩衝區",
                AudioLatencyJudge.Summarize(endpoints), "",
                "Windows Core Audio（IAudioClient::Initialize 的配置結果）",
                FactTrustLevel.Measured, false, at, null, FactAvailability.Present),
        };

        foreach (var e in endpoints)
        {
            var v = AudioLatencyJudge.Judge(e);
            list.Add(new HardwareFact($"audio.latency.{e.Name}", Category, e.Name,
                v.Headline, "", $"Windows Core Audio（獨占模式配置結果）",
                FactTrustLevel.Measured, false, at,
                e.ExclusiveBufferMs > 0 ? e.ExclusiveBufferMs : null,
                v.Tier == AudioLatencyJudge.LatencyTier.Unavailable
                    ? FactAvailability.NotSupported : FactAvailability.Present,
                v.Tier == AudioLatencyJudge.LatencyTier.Unavailable ? v.Evidence : null));
        }

        return list;
    }

    /// <summary>
    /// 逐端點實測。每個端點各自嘗試：獨占模式（找最小配得到的緩衝區）與共用模式。
    /// 任何一步失敗都不讓整批失敗，失敗原因記在該端點的錯誤欄。
    /// </summary>
    internal static IReadOnlyList<AudioEndpointSample> ReadAll()
    {
        var result = new List<AudioEndpointSample>();
        using var enumerator = new MMDeviceEnumerator();
        string defaultId = "";
        try { defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID; }
        catch (Exception ex) { Diag.Swallow("預設音訊端點查詢", ex, "改為不標示預設端點"); }

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            string name = SafeName(device);
            if (name.Length == 0) continue;

            int rate = 0, bits = 0, ch = 0;
            try
            {
                var mix = device.AudioClient.MixFormat;
                rate = mix.SampleRate; bits = mix.BitsPerSample; ch = mix.Channels;
            }
            catch (Exception ex) { Diag.Swallow("音訊混合格式查詢", ex, "該端點格式讀不到"); }

            var (exMs, exFrames, exErr) = ProbeExclusive(device, rate);
            double shMs = ProbeShared(device);

            result.Add(new AudioEndpointSample(
                name,
                IsDefault: defaultId.Length > 0 && SafeId(device) == defaultId,
                MixSampleRate: rate, MixBits: bits, MixChannels: ch,
                ExclusiveBufferMs: exMs, ExclusiveFrames: exFrames, RequestedMs: 1.0,
                SharedBufferMs: shMs, ExclusiveError: exErr,
                NameSuggestsSoftware: LooksLikeSoftware(name)));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>
    /// 要求 1 ms 的獨占緩衝區，看裝置實際配多少。
    /// <b>要求值刻意取小</b>：Windows 會往上夾到裝置能提供的最小週期，
    /// 所以配到的值就是這個端點的下限。若 1 ms 都配不出來，改用裝置預設週期再試一次。
    /// </summary>
    private static (double Ms, int Frames, string Error) ProbeExclusive(MMDevice device, int rate)
    {
        if (rate <= 0) return (0, 0, "取樣率讀不到，無從換算");

        // 16 位元立體聲是獨占模式最普遍的格式；先試它
        foreach (var (dur, label) in new[] { (10_000L, "1 ms"), (0L, "裝置預設") })
        {
            try
            {
                var client = device.AudioClient;
                var format = new WaveFormat(rate, 16, 2);
                client.Initialize(AudioClientShareMode.Exclusive, AudioClientStreamFlags.None,
                                  dur, 0, format, Guid.Empty);
                int frames = client.BufferSize;
                client.Dispose();
                if (frames > 0)
                    return (frames / (double)rate * 1000.0, frames, "");
            }
            catch (Exception ex)
            {
                int hr = ex is System.Runtime.InteropServices.COMException com ? com.HResult : 0;
                // 第一次（1 ms）失敗不代表端點不行，改用預設週期再試
                if (label == "裝置預設")
                    return (0, 0, hr != 0 ? $"0x{hr:X8}" : ex.GetType().Name);
            }
        }
        return (0, 0, "配置失敗");
    }

    /// <summary>共用模式的緩衝區（引擎決定）。失敗回 0，不影響獨占模式的結果。</summary>
    private static double ProbeShared(MMDevice device)
    {
        try
        {
            var client = device.AudioClient;
            var mix = client.MixFormat;
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
                              0, 0, mix, Guid.Empty);
            int frames = client.BufferSize;
            client.Dispose();
            return frames > 0 && mix.SampleRate > 0 ? frames / (double)mix.SampleRate * 1000.0 : 0;
        }
        catch (Exception ex)
        {
            Diag.Swallow("共用模式緩衝區配置", ex, "該端點共用模式緩衝區讀不到");
            return 0;
        }
    }

    /// <summary>
    /// 名稱是否透露這是軟體端點。判斷依名稱字樣（Virtual／Cable／Streaming／VB-Audio 等），
    /// <b>未經匯流排查證</b>——判讀文字會明說這一點。
    /// </summary>
    internal static bool LooksLikeSoftware(string name)
    {
        foreach (string token in new[] { "virtual", "cable", "streaming", "vb-audio", "voicemeeter",
                                          "steam", "uu", "nvidia broadcast", "obs", "dante via" })
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string SafeName(MMDevice d)
    {
        try { return d.FriendlyName ?? ""; }
        catch (Exception ex) { Diag.Swallow("音訊端點名稱查詢", ex, "該端點略過"); return ""; }
    }

    private static string SafeId(MMDevice d)
    {
        try { return d.ID ?? ""; }
        catch { return ""; }
    }
}
