using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace XinSpect;

/// <summary>AI 評價供應商：本機免費（Ollama）或任何 OpenAI 相容的 API 端點。</summary>
public enum AiProvider { Ollama, OpenAiCompatible }

/// <summary>對話中的一則訊息（使用者 / AI），供聊天列表繫結。</summary>
public sealed class AiMessage : ObservableObject
{
    public bool IsUser { get; init; }
    public string RoleText => IsUser ? "你" : "AI";
    private string _text = "";
    public string Text { get => _text; set => SetProperty(ref _text, value); }
}

/// <summary>
/// AI 評價服務：把本機真實硬體規格與即時感測數據交給使用者自選的 AI 模型評價。
/// 統一走 OpenAI 相容的 /chat/completions 介面（Ollama 亦相容），支援本機免費模型或雲端 API。
/// 全程真實呼叫，失敗時據實回報錯誤，絕不偽造評價內容。
/// </summary>
public sealed class AiService : ObservableObject
{
    // 逾時放寬：本機 Ollama 首次載入模型可能數十秒，雲端長回覆亦需時間。
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        // 不少 OpenAI 相容中轉站置於 Cloudflare 之後，會以 403 擋掉「無瀏覽器 User-Agent」的請求
        // （.NET HttpClient 預設不帶 UA）。帶上通用瀏覽器 UA 即可通過 Cloudflare 的基本人機驗證。
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        return c;
    }

    private readonly SettingsService _settings;

    /// <summary>由主檢視模型注入：即時彙整目前硬體規格與感測數據為文字快照。</summary>
    public Func<string>? SnapshotProvider { get; set; }

    public AiService(SettingsService settings) => _settings = settings;

    /// <summary>內建的預設提示詞：要求 AI 客觀、公正、只依真實數據評價，不偏袒任何品牌。</summary>
    public const string DefaultSystemPrompt =
        "你是一位客觀、公正、專業的電腦硬體評測顧問。以下會提供一台電腦的真實硬體規格與即時感測數據。\n" +
        "請僅根據這些真實數據，做出中肯、平衡的評價：\n" +
        "1. 先簡述這套配置的整體定位（入門 / 主流 / 高階 / 工作站等）。\n" +
        "2. 分析各主要元件（處理器、記憶體、顯示卡、儲存、散熱與溫度表現）的優點與不足。\n" +
        "3. 指出目前是否有潛在瓶頸、過熱或搭配不均衡之處。\n" +
        "4. 給出務實的升級或最佳化建議；若無明顯需求，也請如實說明無須升級。\n" +
        "要求：只根據提供的數據作答，不臆測未提供的資訊；不誇大、不貶低、不偏袒任何品牌；" +
        "以繁體中文分段或條列回覆，語氣專業而友善。";

    // ── 對話狀態（供獨立 AI 分頁繫結）──────────────────────────────
    public ObservableCollection<AiMessage> Messages { get; } = new();

    /// <summary>由「一鍵獲取」自端點取得的可用模型名稱清單（供設定頁下拉選擇）。</summary>
    public ObservableCollection<string> Models { get; } = new();

    /// <summary>向目前端點查詢可用模型清單（Ollama /api/tags 或 OpenAI 相容 /v1/models）。</summary>
    public async Task FetchModelsAsync()
    {
        string baseUrl = (_settings.AiBaseUrl ?? "").Trim().TrimEnd('/');
        if (baseUrl.Length == 0) { StatusText = "請先填入 API 端點（Base URL）再獲取模型。"; return; }

        StatusText = "正在自端點取得可用模型…";
        try
        {
            var names = await FetchModelListAsync(baseUrl, (_settings.AiApiKey ?? "").Trim());
            Models.Clear();
            foreach (var n in names) Models.Add(n);
            if (names.Count > 0)
            {
                // 目前未設定模型時，自動帶入第一個
                if (string.IsNullOrWhiteSpace(_settings.AiModel)) _settings.AiModel = names[0];
                StatusText = $"已取得 {names.Count} 個模型，請於下拉選單選擇。";
            }
            else StatusText = "端點連線成功，但未回報任何模型。";
        }
        catch (Exception ex)
        {
            StatusText = "取得模型失敗：" + Explain(ex);
        }
    }

    // 先試 Ollama 的 /api/tags，再試 OpenAI 相容的 /v1/models，合併去重。
    // 付費相容端點（如中轉站）的 /v1/models 多需帶 API 金鑰，故一併傳入 Authorization。
    // 兩條路徑皆失敗且無任何模型時，向外拋出最後一個錯誤（通常為 OpenAI 相容路徑的真實 HTTP 狀態，
    // 例如 401/403），避免把「金鑰無效」誤報為「端點無模型」而讓使用者摸不著頭緒。
    private static async Task<List<string>> FetchModelListAsync(string baseUrl, string? apiKey)
    {
        var result = new List<string>();
        Exception? lastError = null;
        void AddUnique(string? n) { n = n?.Trim(); if (!string.IsNullOrEmpty(n) && !result.Contains(n)) result.Add(n); }

        // Ollama：主機根（去掉結尾 /v1）+ /api/tags → { models: [ { name } ] }
        string root = baseUrl.EndsWith("/v1") ? baseUrl[..^3] : baseUrl;
        try
        {
            using var doc = await GetJsonAsync(root.TrimEnd('/') + "/api/tags", apiKey);
            if (doc is not null && doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var nm)) AddUnique(nm.GetString());
        }
        catch (Exception ex) { lastError = ex; /* 非 Ollama 端點會失敗，改試 OpenAI 相容 */ }

        // OpenAI 相容：/v1/models → { data: [ { id } ] }
        string modelsUrl = baseUrl.EndsWith("/v1") ? baseUrl + "/models"
                         : baseUrl.EndsWith("/models") ? baseUrl
                         : baseUrl + "/v1/models";
        try
        {
            using var doc = await GetJsonAsync(modelsUrl, apiKey);
            if (doc is not null && doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("id", out var id)) AddUnique(id.GetString());
        }
        catch (Exception ex) { lastError = ex; /* 記錄真實錯誤，供下方在無結果時回報 */ }

        // 有任何模型即視為成功（忽略另一路徑的預期性失敗）；完全無結果才把真實錯誤拋出。
        if (result.Count == 0 && lastError is not null) throw lastError;

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, string? apiKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            // 據實回報 HTTP 狀態與伺服器訊息，讓「一鍵獲取」能明確顯示 401/403 等原因
            string body = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}：{Trim(body, 200)}");
        }
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(CanSend)); } }
    public bool CanSend => !_isBusy;

    private string _statusText = "尚未連線 — 於「設定 › AI 評價」選擇供應商與模型後即可使用。";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private bool _hasMessages;
    public bool HasMessages { get => _hasMessages; private set => SetProperty(ref _hasMessages, value); }

    /// <summary>清空對話。</summary>
    public void Clear()
    {
        Messages.Clear();
        HasMessages = false;
        StatusText = "對話已清除。";
    }

    /// <summary>一鍵評價：以內建（或使用者自訂）提示詞＋硬體快照，請 AI 產出整機評價。</summary>
    public Task EvaluateAsync() => SendAsync("請根據上述硬體資訊，對這台電腦做一次完整、客觀的評價。");

    /// <summary>送出一則使用者訊息並取得 AI 回覆（含硬體快照作為系統背景）。</summary>
    public async Task SendAsync(string userText)
    {
        if (IsBusy) return;
        userText = (userText ?? "").Trim();
        if (userText.Length == 0) return;

        Messages.Add(new AiMessage { IsUser = true, Text = userText });
        var reply = new AiMessage { IsUser = false, Text = "思考中…" };
        Messages.Add(reply);
        HasMessages = true;

        IsBusy = true;
        StatusText = "正在呼叫 AI 模型…";
        try
        {
            var text = await CompleteAsync();
            reply.Text = text;
            StatusText = $"完成 ・ {_settings.AiModel}（{ProviderLabel(_settings.AiProviderEnum)}）";
        }
        catch (Exception ex)
        {
            reply.Text = "⚠ 呼叫失敗：" + Explain(ex);
            StatusText = "呼叫失敗 — 請檢查設定中的端點、模型與金鑰。";
        }
        finally { IsBusy = false; }
    }

    private static string ProviderLabel(AiProvider p) => p == AiProvider.Ollama ? "本機 Ollama" : "OpenAI 相容 API";

    // ── 實際 HTTP 呼叫（OpenAI 相容 chat/completions）──────────────
    private async Task<string> CompleteAsync()
    {
        string baseUrl = (_settings.AiBaseUrl ?? "").Trim().TrimEnd('/');
        if (baseUrl.Length == 0) throw new InvalidOperationException("尚未設定 API 端點（Base URL）。");
        // 端點正規化：使用者可填 http://host:port 或 .../v1；統一補到 /v1/chat/completions。
        string url = baseUrl.EndsWith("/chat/completions") ? baseUrl
                   : baseUrl.EndsWith("/v1") ? baseUrl + "/chat/completions"
                   : baseUrl + "/v1/chat/completions";

        string model = (_settings.AiModel ?? "").Trim();
        if (model.Length == 0) throw new InvalidOperationException("尚未設定模型名稱。");

        string sys = string.IsNullOrWhiteSpace(_settings.AiSystemPrompt) ? DefaultSystemPrompt : _settings.AiSystemPrompt;
        string snapshot = "";
        try { snapshot = SnapshotProvider?.Invoke() ?? ""; } catch { /* 快照彙整失敗不阻斷對話 */ }
        if (snapshot.Length > 0) sys += "\n\n===== 本機硬體資訊（真實讀值）=====\n" + snapshot;

        var msgs = new List<object> { new { role = "system", content = sys } };
        foreach (var m in Messages)
        {
            if (ReferenceEquals(m, Messages[^1])) continue;            // 最後一則是占位的 AI 回覆
            if (!m.IsUser && m.Text is "思考中…") continue;
            msgs.Add(new { role = m.IsUser ? "user" : "assistant", content = m.Text });
        }

        var payload = new
        {
            model,
            messages = msgs,
            temperature = _settings.AiTemperature,
            stream = false,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        string key = (_settings.AiApiKey ?? "").Trim();
        if (key.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);

        using var resp = await Http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}：{Trim(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content))
        {
            var text = content.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text!.Trim();
        }
        // 部分相容端點的錯誤格式
        if (root.TryGetProperty("error", out var err))
            throw new InvalidOperationException(err.TryGetProperty("message", out var em) ? em.GetString() ?? body : Trim(body, 300));
        throw new InvalidOperationException("回應格式無法解析：" + Trim(body, 300));
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    private static string Explain(Exception ex) => ex switch
    {
        TaskCanceledException => "連線逾時（模型載入或網路過慢）。本機 Ollama 首次載入模型較久，可稍後重試。",
        HttpRequestException he when he.Message.Contains("actively refused") || he.Message.Contains("failed to respond")
            => "無法連線到端點。若使用本機 Ollama，請確認已安裝並執行（ollama serve）且模型已下載。",
        _ => ex.Message,
    };
}
