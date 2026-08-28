using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace XinSpect;

/// <summary>
/// 測速協定：Cloudflare（__down/__up）、LibreSpeed（garbage.php/empty.php）、
/// NtuSpeed5（台大 speed5 自訂端點 ?module=download/upload），以及 WebPage（無公開端點，改以瀏覽器開啟官方測速頁）。
/// </summary>
public enum NodeProtocol { Cloudflare, LibreSpeed, NtuSpeed5, WebPage }

/// <summary>一個測速節點的定義（端點與協定）。</summary>
public sealed class SpeedNode
{
    public string Name { get; init; } = "";
    public string Note { get; init; } = "";
    public NodeProtocol Protocol { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string UploadUrl { get; init; } = "";
    public string PingUrl { get; init; } = "";
    public string WebUrl { get; init; } = "";   // WebPage 節點：以瀏覽器開啟的官方測速頁
    public override string ToString() => Note.Length > 0 ? $"{Name}（{Note}）" : Name;
}

/// <summary>單次進度快照（供 UI 即時更新，透過 IProgress 於 UI 執行緒回報）。</summary>
public sealed record SpeedSample(
    string Phase, double PingMs, double JitterMs,
    double DownMbps, double UpMbps, double LiveMbps, string Status, bool Done);

/// <summary>
/// 網速測試：對所選節點量測延遲／抖動、下載與上傳吞吐量。多執行緒串流、時間窗取樣，
/// 支援 NTU 台大（speed5 學術網路，台灣）等原生量測節點；
/// HKBN 香港寬頻未提供可供程式直接量測的公開端點，故改跳轉至內建瀏覽器開啟其官方測速頁（WebPage）。
/// Cloudflare 節點目前量測結果不穩定（標註 Bug），暫置於清單末位。
/// 需連外；除本測試外不傳送任何本機資料。
/// </summary>
public sealed class NetworkSpeedService
{
    public static readonly SpeedNode[] Nodes =
    {
        new() { Name = "NTU 台大", Note = "學術網路・台灣", Protocol = NodeProtocol.NtuSpeed5,
                DownloadUrl = "http://speed5.ntu.edu.tw/speed5/server/?module=download&size=",
                UploadUrl   = "http://speed5.ntu.edu.tw/speed5/server/?module=upload",
                PingUrl     = "http://speed5.ntu.edu.tw/speed5/server/" },
        new() { Name = "HKBN 香港寬頻", Note = "官方網頁測速", Protocol = NodeProtocol.WebPage,
                WebUrl = "https://www.hkbn.net/personal/broadband/tc/speedtest" },
        new() { Name = "Cloudflare", Note = "全球公開・Bug", Protocol = NodeProtocol.Cloudflare,
                DownloadUrl = "https://speed.cloudflare.com/__down",
                UploadUrl   = "https://speed.cloudflare.com/__up",
                PingUrl     = "https://speed.cloudflare.com/__down?bytes=0" },
    };

    private const int StreamCount = 4;
    private static readonly TimeSpan DlWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UlWindow = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;

    public NetworkSpeedService()
    {
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
        };
        _http = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("XinSpect-SpeedTest/1.0");
    }

    public async Task RunAsync(SpeedNode node, IProgress<SpeedSample> progress, CancellationToken ct)
    {
        double ping = 0, jitter = 0, down = 0, up = 0;
        try
        {
            progress.Report(new("準備", 0, 0, 0, 0, 0, $"連線至 {node.Name} …", false));

            (ping, jitter) = await MeasurePingAsync(node, ct);
            progress.Report(new("延遲", ping, jitter, 0, 0, 0,
                $"延遲 {ping:0.0} ms ・ 抖動 {jitter:0.0} ms", false));

            down = await MeasureAsync(node, upload: false,
                live => progress.Report(new("下載", ping, jitter, live, 0, live, $"下載測試中… {live:0.0} Mbps", false)), ct);
            progress.Report(new("下載", ping, jitter, down, 0, 0, $"下載 {down:0.0} Mbps", false));

            up = await MeasureAsync(node, upload: true,
                live => progress.Report(new("上傳", ping, jitter, down, live, live, $"上傳測試中… {live:0.0} Mbps", false)), ct);

            progress.Report(new("完成", ping, jitter, down, up, 0,
                $"完成 ・ 延遲 {ping:0.0} ms ・ 下載 {down:0.0} Mbps ・ 上傳 {up:0.0} Mbps", true));
        }
        catch (OperationCanceledException)
        {
            progress.Report(new("取消", ping, jitter, down, up, 0, "已取消測速。", true));
        }
        catch (Exception ex)
        {
            progress.Report(new("錯誤", ping, jitter, down, up, 0, "測速失敗：" + ex.Message, true));
        }
    }

    // 以多次小請求量測延遲與抖動；全部失敗時拋出友善訊息。
    private async Task<(double ping, double jitter)> MeasurePingAsync(SpeedNode node, CancellationToken ct)
    {
        var samples = new List<double>();
        string lastErr = "";
        for (int i = 0; i < 6; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string url = node.PingUrl + (node.PingUrl.Contains('?') ? "&" : "?") + "r=" + Guid.NewGuid().ToString("N");
                var sw = Stopwatch.StartNew();
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    lastErr = $"HTTP {(int)resp.StatusCode}";
                    continue;
                }
                await resp.Content.CopyToAsync(Stream.Null, ct);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastErr = ex.Message; }
        }
        if (samples.Count == 0)
            throw new InvalidOperationException(lastErr.Length > 0 ? lastErr : "無法連線至測速節點。");

        samples.Sort();
        double ping = samples[0];   // 取最小值（最接近純網路往返）
        double jitter = 0;
        for (int i = 1; i < samples.Count; i++) jitter += Math.Abs(samples[i] - samples[i - 1]);
        jitter = samples.Count > 1 ? jitter / (samples.Count - 1) : 0;
        return (ping, jitter);
    }

    // 下載／上傳共用：開 StreamCount 條串流於時間窗內持續傳輸，200ms 取樣即時速度，最後取窗內平均。
    private async Task<double> MeasureAsync(SpeedNode node, bool upload, Action<double> live, CancellationToken ct)
    {
        long bytes = 0;
        var window = upload ? UlWindow : DlWindow;
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(ct);
        byte[]? payload = null;
        if (upload)
        {
            payload = new byte[4 * 1024 * 1024];               // 4MB；內容不重要（伺服器丟棄）
            new Random(20260828).NextBytes(payload);
        }

        var workers = new Task[StreamCount];
        for (int i = 0; i < StreamCount; i++)
            workers[i] = upload
                ? UploadWorker(node, payload!, n => Interlocked.Add(ref bytes, n), phase.Token)
                : DownloadWorker(node, n => Interlocked.Add(ref bytes, n), phase.Token);

        var sw = Stopwatch.StartNew();
        long lastB = 0; double lastT = 0;
        while (sw.Elapsed < window)
        {
            await Task.Delay(200, ct);
            long b = Interlocked.Read(ref bytes);
            double t = sw.Elapsed.TotalSeconds;
            if (t > lastT) live((b - lastB) * 8.0 / (t - lastT) / 1e6);
            lastB = b; lastT = t;
        }
        double total = Interlocked.Read(ref bytes);
        phase.Cancel();
        try { await Task.WhenAll(workers); } catch { /* 取消所致，忽略 */ }
        return total * 8.0 / window.TotalSeconds / 1e6;
    }

    private async Task DownloadWorker(SpeedNode node, Action<long> add, CancellationToken ct)
    {
        var buf = new byte[81920];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string url = node.Protocol switch
                {
                    NodeProtocol.Cloudflare => $"{node.DownloadUrl}?bytes=104857600&r={Guid.NewGuid():N}",   // 100MB／次，靠時間窗截斷
                    NodeProtocol.NtuSpeed5  => $"{node.DownloadUrl}104857600&id={Guid.NewGuid():N}",          // speed5：?module=download&size=<bytes>&id=
                    _                       => $"{node.DownloadUrl}?ckSize=1024&r={Guid.NewGuid():N}",        // LibreSpeed 亂數資料
                };
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                using var s = await resp.Content.ReadAsStreamAsync(ct);
                int n;
                while ((n = await s.ReadAsync(buf, ct)) > 0)
                {
                    add(n);
                    if (ct.IsCancellationRequested) break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch { try { await Task.Delay(50, ct); } catch { break; } }
        }
    }

    private async Task UploadWorker(SpeedNode node, byte[] payload, Action<long> add, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                string url = node.UploadUrl + (node.UploadUrl.Contains('?') ? "&" : "?") + "r=" + Guid.NewGuid().ToString("N");
                using var resp = await _http.PostAsync(url, content, ct);
                add(payload.Length);   // 送出即計入（伺服器回應內容不重要）
            }
            catch (OperationCanceledException) { break; }
            catch { try { await Task.Delay(50, ct); } catch { break; } }
        }
    }
}
