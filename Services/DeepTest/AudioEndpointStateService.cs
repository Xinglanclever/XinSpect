using NAudio.CoreAudioApi;

namespace XinSpect;

/// <summary>
/// U10-TS-0128「端點音量/靜音狀態核對」：<c>ts.aud.endpoint.state</c>。
/// </summary>
/// <remarks>
/// <para>
/// 來源：WASAPI <c>IAudioEndpointVolume</c>（NAudio 包裝；usermode 唯讀）——
/// 每個作用中端點的主音量純量（0–1）與靜音位元。<b>只 Get、不 Set</b>：
/// 核對是為了發現「被靜音/音量歸零導致的沒聲音」，任何寫入都會把檢測變成干預。
/// </para>
/// <para>
/// 誠實界線：讀不到某端點的音量（少數驅動不支援端點音量）如實標 ReadError，
/// 不以 0 冒充「音量歸零」——0% 與「讀不到」是兩件完全不同的事。
/// </para>
/// </remarks>
public static class AudioEndpointStateService
{
    private const string Category = "音訊";
    public const string Key = "ts.aud.endpoint.state";
    /// <summary>逐端點家族前綴（成員＝端點索引）——名字刻意不含 Key，是家族前綴不是一把鍵。</summary>
    public const string EndpointStatePrefix = "ts.aud.endpoint.state.";
    public const string Source = "WASAPI IAudioEndpointVolume（NAudio；只讀不寫）";

    /// <summary>單一端點的狀態列；null＝該項讀不到。</summary>
    public sealed record EndpointStateRow(string Name, string Flow, double? VolumeScalar, bool? Muted);

    /// <summary>收集端點音量/靜音狀態。測試以注入取代。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<EndpointStateRow>>? endpoints = null)
    {
        var list = endpoints is null ? RealEndpoints() : endpoints();
        if (list.Count == 0)
            return [new HardwareFact(Key, Category, "端點音量/靜音", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "MMDevice API 無作用中音訊端點（或列舉失敗）——如實標")];

        var facts = new List<HardwareFact>();
        for (int i = 0; i < list.Count; i++)
        {
            var (value, availability, reason) = Describe(list[i]);
            facts.Add(new HardwareFact($"{EndpointStatePrefix}{i}", Category, $"端點狀態：{list[i].Name}", value, "", Source,
                FactTrustLevel.Reported, false, at, null, availability, reason));
        }
        return facts;
    }

    /// <summary>純函式：端點狀態 → 判讀。</summary>
    public static (string Value, FactAvailability Availability, string? UnavailableReason) Describe(
        EndpointStateRow row)
    {
        if (row.VolumeScalar is null && row.Muted is null)
            return ("", FactAvailability.ReadError, "端點音量與靜音都讀不到（驅動不支援端點音量介面）");
        string value = $"{row.Flow}；音量 " + (row.VolumeScalar is { } v
            ? $"{v * 100:0.#}%" + (v == 0 ? "（歸零——這是讀到的真值，不是讀不到）" : "")
            : "讀不到")
            + "；靜音 " + (row.Muted is { } m ? (m ? "是" : "否") : "讀不到");
        return (value, FactAvailability.Present, null);
    }

    // ── 真實 WASAPI 通路（薄；測試以注入取代）──

    private static IReadOnlyList<EndpointStateRow> RealEndpoints()
    {
        var result = new List<EndpointStateRow>();
        try
        {
            var enumerator = new MMDeviceEnumerator();
            foreach (var (flow, flowName) in new[] { (DataFlow.Render, "渲染"), (DataFlow.Capture, "擷取") })
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    double? volume = null;
                    bool? muted = null;
                    try { volume = device.AudioEndpointVolume.MasterVolumeLevelScalar; }
                    catch { /* 端點不支援音量——如實留空 */ }
                    try { muted = device.AudioEndpointVolume.Mute; }
                    catch { /* 同上 */ }
                    result.Add(new EndpointStateRow(device.FriendlyName, flowName, volume, muted));
                }
            }
        }
        catch { /* 音訊列舉為附加功能，失敗由上層標三態 */ }
        return result;
    }
}
