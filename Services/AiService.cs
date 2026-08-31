using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// AI 評價供應商：本機免費（Ollama）、任何 OpenAI 相容的 API 端點，
/// 或作者自付費用分享的免費共用額度（走自建中轉，程式裡不含任何金鑰，見 <see cref="SharedAiEndpoint"/>）。
/// </summary>
/// <remarks>列舉值直接對應設定頁下拉選單的 <c>SelectedIndex</c>，並以整數存進 settings.json；
/// 只能往後追加，不可調換既有順序，否則舊設定檔會選到別的供應商。</remarks>
public enum AiProvider { Ollama, OpenAiCompatible, SharedFree }

/// <summary>
/// 一次請求的來源。共用額度只開放 <see cref="Evaluate"/>——
/// 判斷寫在 <see cref="SharedAiEndpoint.Allows"/>，這裡只負責如實標記請求是誰發的。
/// </summary>
public enum AiRequestKind
{
    /// <summary>使用者在對話框裡自己打的字。</summary>
    Chat,
    /// <summary>「一鍵評價」：一次性的整機評價。</summary>
    Evaluate,
    /// <summary>硬體警示觸發的主動診斷。</summary>
    Proactive,
}

/// <summary>對話中的一則訊息（使用者 / AI / 本機工具查詢紀錄），供聊天列表繫結。</summary>
public sealed class AiMessage : ObservableObject
{
    public bool IsUser { get; init; }
    /// <summary>是否為「診斷代理呼叫本機唯讀工具」的紀錄列（僅供人看，不當成對話內容送回模型）。</summary>
    public bool IsTool { get; init; }
    /// <summary>是否為模型的一般回覆（用於聊天氣泡樣式判斷）。</summary>
    public bool IsAssistant => !IsUser && !IsTool;
    public string RoleText => IsUser ? "你" : IsTool ? "查詢" : "AI";
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
    private readonly AiChatStore _chat;

    /// <summary>由主檢視模型注入：即時彙整目前硬體規格與感測數據為文字快照。</summary>
    public Func<string>? SnapshotProvider { get; set; }

    /// <summary>診斷代理可呼叫的本機唯讀工具箱（由 <c>AiToolboxBuilder</c> 建立並注入）。</summary>
    public AiToolbox? Tools { get; set; }

    public AiService(SettingsService settings, string? chatFolder = null)
    {
        _settings = settings;
        _chat = new AiChatStore(chatFolder);

        // 「保留對話」關閉的那一刻就把檔案刪掉。設定頁寫著「關閉時會一併刪檔」，
        // 若只是停止續寫，舊檔會留在磁碟上——那就是介面在說謊。
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsService.AiKeepHistory) && !_settings.AiKeepHistory)
                _chat.Delete();
        };

        if (!_settings.AiKeepHistory) return;

        foreach (var m in _chat.Load()) Messages.Add(m);
        if (Messages.Count == 0) return;
        HasMessages = true;
        StatusText = $"已接續上次對話（{Messages.Count} 則）。";
    }

    /// <summary>
    /// 內建的預設提示詞：要求 AI 客觀、公正、只依真實數據評價，不偏袒任何品牌。
    /// 除了角色與六段回覆結構，還把幾個最容易被模型誤讀的前提寫成鐵則——
    /// 「沒量到不等於量到 0」、「快照只是某一瞬間」、「健康總評是本程式的規則引擎結論而非原始讀值」——
    /// 並要求動筆前先盤點資料、認出機器形態、用負載讀值推定取樣情境，再去對照互相矛盾的欄位。
    /// 這是本程式全程真實讀值的延伸：資料誠實，講解也要誠實。
    /// </summary>
    public const string DefaultSystemPrompt =
        "你是一位客觀、公正、專業的電腦硬體評測顧問，服務對象是這台電腦的持有者。\n" +
        "下方「本機硬體資訊（真實讀值）」是 XinSpect 從這台機器實際讀到的規格與即時感測數據，也是你唯一的事實來源。\n" +
        "\n" +
        "【鐵則】\n" +
        "1. 只依提供的數據作答。沒有的就寫「資料未提供」，並說明缺這一項會讓哪個判斷無法定論；" +
        "絕不編造型號、跑分、功耗、價格、市場排名或世代比較的數字。\n" +
        "2. 讀值是「—」「未測試」「尚未量測」「不支援」「N/A」或 0 時，代表沒量到，不是量到 0，不可據此下結論；" +
        "並告訴使用者可以到哪一頁按下量測、或開啟「診斷代理」讓工具去讀。\n" +
        "3. 感測數據是送出訊息那一瞬間的快照，不是長期表現；描述時用「此刻讀值」而不是「這台機器就是這樣」。\n" +
        "4. 明顯不合理的讀值（風扇 0 RPM、負溫度、時脈為 0、容量或電壓離譜）先標為" +
        "「讀值可疑，可能是感測器不支援或讀取失敗」，不拿它下結論。\n" +
        "5. 區分「實測事實」與「依規格推論」，推論要寫出依據；不確定就說不確定。\n" +
        "6. 「健康總評」是 XinSpect 自己的規則引擎給的分數，可以引用，但你要獨立判斷；" +
        "若它與原始讀值對不上，指出矛盾，不要照抄分數當結論。\n" +
        "\n" +
        "【動筆前先做三件事】\n" +
        "甲、盤點：先確認哪些類別真的有讀值、哪些沒有。有資料的才展開寫，整批缺的合併成一句帶過" +
        "（例如「本次快照未包含風扇轉速、SMART 屬性、供電與網路」），不要為了填滿結構而灌水。\n" +
        "乙、認形態：從「機型」與作業系統判斷這是桌機、筆電、一體機、伺服器還是虛擬機，並據此調整結論與建議——" +
        "筆電換不了顯示卡、散熱與功耗天花板本來就低、溫度基準也與桌機不同；" +
        "伺服器與虛擬機常缺大部分感測讀值，也不適用消費級調校建議。看不出形態就說看不出，不要預設是桌機。\n" +
        "丙、定取樣情境：用負載讀值推定這份快照是待機還是負載中" +
        "（處理器或顯示卡負載約一成以下視為待機、一到五成輕中載、八成以上接近滿載），" +
        "並明說這是依負載讀值推定。待機溫度不能當散熱結論；只有連負載都讀不到時，才同時給出待機與滿載兩種解讀。\n" +
        "\n" +
        "【交叉檢查：最有價值的發現常在互相矛盾的欄位之間】\n" +
        "即時頻率對基準時脈與負載：低載卻低於基準、或高載時掉下來，是降頻或電源政策的線索。\n" +
        "即時功耗對 TDP：貼著上限跑代表撞到功耗牆，這時改善散熱通常比換零件有用。\n" +
        "溫度對負載與功耗：高溫而功耗也高是「發熱本來就多」；高溫但負載與功耗都低，才是散熱異常。\n" +
        "記憶體時序與模組數：只有一條模組、或時序顯示未啟用 XMP／EXPO，代表可能跑在單通道或標稱值以下。\n" +
        "磁碟區容量與剩餘空間：系統碟剩餘吃緊會直接拖慢整機，比升級零件更該先處理。\n" +
        "處理器與顯示卡的層級落差：只在兩邊資料都齊時才下判斷。\n" +
        "任何一組對不上的數字都要寫出來，並給最可能的解釋與驗證方式。\n" +
        "\n" +
        "【回覆結構】\n" +
        "一、整體定位：判定為入門／主流／中高階／高階／旗艦／工作站／伺服器或特化用途，" +
        "用兩三句說明依據（核心數與世代、記憶體容量與通道、顯示卡層級、儲存介面、平台特性），" +
        "並指出它最勝任與最不勝任哪些用途（文書、遊戲、內容創作、程式編譯、虛擬化、AI 推論等）。\n" +
        "二、逐項分析：處理器、記憶體、顯示卡、儲存、主機板與平台、散熱與溫度、供電與功耗、網路與其他。" +
        "每項照「關鍵讀值 → 優點 → 不足或風險 → 現在是否構成限制」四段寫，並引用你依據的實際數字；" +
        "整類無資料就寫「未提供，無法評估」。\n" +
        "三、瓶頸與均衡度：檢視處理器與顯示卡等級是否相稱、記憶體容量／通道／時脈是否拖累平台、" +
        "儲存介面與健康度（SMART、剩餘空間）是否有隱憂、溫度與時脈是否顯示降頻或撞上功耗牆、散熱是否跟得上發熱量。" +
        "結論明確歸為「均衡」「單點瓶頸（指名哪一點）」或「資料不足以判定」，" +
        "並說明該瓶頸在哪些情境才會被觸發、哪些情境不受影響。\n" +
        "四、溫度與穩定性判讀：對每個有讀值的溫度來源，歸類為偏涼／正常／偏高但安全／接近上限／需處理，" +
        "並說明理由，且要把判斷綁在前面推定的取樣情境上（同一個 70 °C 在待機與滿載是兩回事）；" +
        "連負載都讀不到時，才同時給出兩種解讀。\n" +
        "五、建議：先列免錢或低成本的最佳化（清灰、風扇曲線、電源計畫、驅動與 BIOS、釋放儲存空間、" +
        "確認 XMP／EXPO 是否已啟用——僅在數據看得出來時才提），再列需要花錢的升級；" +
        "細節依下方【建議要分級，也要說風險】。\n" +
        "六、一句話總結：兩三句收尾，含最重要的一個行動建議，或「維持現狀即可」。\n" +
        "\n" +
        "【建議要分級，也要說風險】\n" +
        "依「先免費、再便宜、最後花大錢」排序，每項寫清楚改善什麼場景、大致的改善幅度、" +
        "以及這個平台的相容前提（插槽、記憶體世代、介面、機殼與電源餘裕；資料不足就註明「需先確認」）。\n" +
        "涉及拆機、刷新 BIOS、調電壓或超頻的建議，一律標明風險、是否可逆、以及有沒有更安全的替代做法。\n" +
        "若整機均衡、沒有明顯瓶頸，就直接說「目前無須升級」，並說明出現什麼徵兆時才值得動手。" +
        "不主動推薦特定品牌型號或報價。\n" +
        "\n" +
        "【篇幅與追問】\n" +
        "首次評價約六百到一千二百字，重點清楚即可；不要把快照原封不動複述一遍，只引用支撐論點的數字（含單位）。\n" +
        "使用者追問時，只回答被問的那一段，不要每次重跑整份六段報告。\n" +
        "使用者若說明了用途、預算或實際困擾，就以他的需求重排建議順序——他的問題優先於這裡的固定結構。\n" +
        "\n" +
        "【語氣與禁忌】\n" +
        "以繁體中文、台灣慣用術語書寫，分段與條列並用，專業而友善，像對懂電腦但不熟細節的人解釋。\n" +
        "不誇大、不貶低、不偏袒任何品牌，禁用行銷腔（「猛獸」「無情輾壓」「毫無懸念」之類）。\n" +
        "優點與不足都要講：硬體較舊或較低階不等於差，只依它是否勝任可判斷的用途來評價；規格高也不等於沒問題。\n" +
        "寧可少下一個結論，也不要下一個沒有數據支撐的結論。";

    /// <summary>診斷代理模式下附加的說明：告訴模型有哪些本機唯讀工具，以及「不准編造」的鐵則。</summary>
    public const string AgentPrompt =
        "【診斷代理】你可以呼叫本機的唯讀查詢工具，主動取得更多真實數據："
        + "完整規格、處理器拓樸、即時讀值、所有溫度、感測器總表、風扇與曲線現況、磁碟健康、"
        + "事件時間軸、歷史統計、顯示卡與處理器調校現況、場景設定、健康總評、環境自檢、效能測試成績、"
        + "記憶體時序與 SPD（含 XMP／EXPO）、網路組態與流量、螢幕 EDID 色域、效能天梯名次與同級對手、"
        + "升級建議規則引擎、電池健康、開機啟動項、藍屏傾印記錄。\n"
        + "另有一批「硬核」工具，量的是別的工具問不到的底層事實："
        + "Top-down 管線歸因（慢在哪個環節）、頻率真相（BCLK／倍頻表／逐核有效時脈）、"
        + "平台可信度（MSR 讀值可不可信）、BIOS 與 ME 韌體與微碼版本、逐核時間歸因（中斷與 DPC 吃掉哪顆核）、"
        + "電源政策、記憶體認可帳本、機器檢查與 WHEA 硬體錯誤、處理器免疫位元、RDT 快取占用與記憶體頻寬。\n"
        + "規則：需要資料時先呼叫工具，不要憑印象猜測；一次可以呼叫多個工具；"
        + "工具回報「尚未測試」「尚未量測」「無資料」或「不可用」時，就如實說明本機沒有該項資料，"
        + "並可以告訴使用者要開哪一頁按哪個按鈕才量得到——但絕不編造數值；"
        + "硬核工具多半要使用者先按下量測，沒量過就是沒有，不要把 0 當成量到的結果；"
        + "要解讀任何 MSR 類讀值（Top-down、頻率真相、機器檢查、免疫位元）之前，先查平台可信度；"
        + "工具明確標示某項推算值不是實測、或列出量測限制（如 EDID 色域、天梯分數、升級效益範圍、"
        + "Bad Speculation 低估、逐核非同一瞬間、認可帳本不等於實際寫出頁面）時，轉述時也必須一併說明；"
        + "取得足夠資料後，再用繁體中文給出結論與建議。";

    // ── 對話狀態（供獨立 AI 分頁繫結）──────────────────────────────
    public ObservableCollection<AiMessage> Messages { get; } = new();

    /// <summary>由「一鍵獲取」自端點取得的可用模型名稱清單（供設定頁下拉選擇）。</summary>
    public ObservableCollection<string> Models { get; } = new();

    /// <summary>向目前端點查詢可用模型清單（Ollama /api/tags 或 OpenAI 相容 /v1/models）。</summary>
    public async Task FetchModelsAsync()
    {
        if (_settings.AiProviderEnum == AiProvider.SharedFree)
        {
            StatusText = "共用額度用哪個模型由中轉決定，這裡不需要（也無法）選擇模型。";
            return;
        }

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
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(CanCancel));
        }
    }
    public bool CanSend => !_isBusy;

    /// <summary>是否有進行中的請求可以取消（供 AI 分頁的「停止」按鈕繫結）。</summary>
    public bool CanCancel => _isBusy;

    // 進行中請求的取消來源；沒有請求時為 null。
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 停止目前的請求。已經串流出來的文字會留在畫面上並註明是中途停止的——
    /// 半截的回答就該看得出是半截，不能讓它看起來像完整結論。
    /// </summary>
    public void Cancel()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* 已收尾，無事可做 */ }
    }

    private string _statusText = "尚未連線 — 於「設定 › AI 評價」選擇供應商與模型後即可使用。";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private bool _hasMessages;
    public bool HasMessages { get => _hasMessages; private set => SetProperty(ref _hasMessages, value); }

    /// <summary>清空對話（同時刪除本機保存檔）。</summary>
    public void Clear()
    {
        Messages.Clear();
        HasMessages = false;
        _chat.Delete();
        StatusText = "對話已清除。";
    }

    /// <summary>一鍵評價：以內建（或自訂）提示詞＋硬體快照，請 AI 產出整機評價。共用額度只開放這一種請求。</summary>
    public Task EvaluateAsync() => SendAsync(
        "請根據上述硬體資訊，對這台電腦做一次完整、客觀的評價。", AiRequestKind.Evaluate);

    /// <summary>
    /// 主動診斷：溫度／負載警示觸發時自動請 AI 就地分析一次。
    /// 端點或模型還沒填、或此刻正在對話中，就安靜略過——寧可不診斷，也不要打斷使用者或丟出失敗訊息。
    /// 呼叫端負責檢查 <see cref="SettingsService.AiProactive"/> 與觸發頻率。
    /// </summary>
    public Task ProactiveAsync(string label, string alertText)
    {
        if (IsBusy) return Task.CompletedTask;
        // 共用額度不開放主動診斷：安靜略過，不要冒出一則「不允許」的訊息去打斷使用者。
        if (_settings.AiProviderEnum == AiProvider.SharedFree) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(_settings.AiBaseUrl) || string.IsNullOrWhiteSpace(_settings.AiModel))
            return Task.CompletedTask;

        return SendAsync($"【主動診斷】本機剛剛觸發硬體警示：{alertText}。"
            + $"請針對「{label}」呼叫必要的唯讀工具查明現況（例如即時讀值、所有溫度、事件時間軸、歷史統計、風扇現況），"
            + "說明最可能的原因，以及現在具體該做什麼。查不到的資料請如實說明沒有，不要臆測。",
            AiRequestKind.Proactive);
    }

    /// <summary>送出一則使用者訊息並取得 AI 回覆（含硬體快照作為系統背景；代理模式下可自行查工具）。</summary>
    public async Task SendAsync(string userText, AiRequestKind kind = AiRequestKind.Chat)
    {
        if (IsBusy) return;
        userText = (userText ?? "").Trim();
        if (userText.Length == 0) return;

        Messages.Add(new AiMessage { IsUser = true, Text = userText });
        var reply = new AiMessage { IsUser = false, Text = Placeholder };
        Messages.Add(reply);
        HasMessages = true;

        // 共用額度的範圍限制：直接把原因與替代方案寫在氣泡裡，不發出請求也不假裝失敗。
        if (_settings.AiProviderEnum == AiProvider.SharedFree && !SharedAiEndpoint.Allows(kind))
        {
            reply.Text = SharedAiEndpoint.NotAllowedText;
            StatusText = "共用額度只開放一鍵評價。";
            if (_settings.AiKeepHistory) _chat.Save(Messages);
            return;
        }

        IsBusy = true;
        StatusText = "正在呼叫 AI 模型…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            await RunAsync(reply, cts.Token);
            StatusText = $"完成 ・ {ResolveModel()}（{ProviderLabel(_settings.AiProviderEnum)}）";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 使用者按了「停止」：這不是錯誤，但也不能假裝回答已經完成。
            if (reply.Text is Placeholder or "") reply.Text = "（已停止，這次沒有取得任何回覆）";
            else reply.Text += "\n\n（已由你手動停止，以上內容並非完整回答）";
            StatusText = "已停止。";
        }
        catch (Exception ex)
        {
            // 已經串流出部分內容時保留它，只在末尾附註中斷原因——不假造完整結論。
            if (reply.Text is Placeholder or "") reply.Text = "⚠ 呼叫失敗：" + Explain(ex);
            else reply.Text += "\n\n⚠ 回覆中斷：" + Explain(ex);
            StatusText = "呼叫失敗 — 請檢查設定中的端點、模型與金鑰。";
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            IsBusy = false;
            if (_settings.AiKeepHistory) _chat.Save(Messages);
        }
    }

    /// <summary>回覆尚未有內容時的占位字（測試會據此驗證占位訊息不會被回送模型）。</summary>
    internal const string Placeholder = "思考中…";

    internal static string ProviderLabel(AiProvider p) => p switch
    {
        AiProvider.Ollama => "本機 Ollama",
        AiProvider.SharedFree => "免費共用",
        _ => "OpenAI 相容 API",
    };

    // ── 代理迴圈（OpenAI 相容 chat/completions＋tool calling）──────
    /// <summary>最多允許模型連續查工具的輪數；超過即要求它直接以文字作答，避免無止境迴圈。</summary>
    private const int MaxToolRounds = 5;

    /// <summary>模型要求執行的一次工具呼叫。</summary>
    private sealed record ToolCall(string Id, string Name, string Args);

    /// <summary>一輪呼叫的結果：模型說的文字，以及它要求執行的工具（空表示這就是最終答案）。</summary>
    private sealed record Round(string Content, List<ToolCall> Calls);

    /// <summary>端點不接受 <c>stream=true</c> 時內部使用，用來退回整段模式重試一次。</summary>
    private sealed class StreamUnsupportedException(string message) : Exception(message);

    /// <summary>
    /// 把模型輸出逐段寫進聊天氣泡；第一段會清掉「思考中…」占位字。
    /// 內部以 <see cref="StringBuilder"/> 累積、再整份指派給氣泡：
    /// 直接對 <c>Text</c> 做 <c>+=</c> 會每個 token 配置一條新字串（O(n²)），
    /// 長篇回覆到後段會明顯卡頓。
    /// </summary>
    private sealed class ReplySink(AiMessage target)
    {
        private readonly StringBuilder _buf = new();
        private bool _started;

        public void Append(string chunk)
        {
            if (chunk.Length == 0) return;
            if (!_started) { _buf.Clear(); _started = true; }
            _buf.Append(chunk);
            target.Text = _buf.ToString();
        }

        /// <summary>工具查詢後模型繼續說話時換段，避免前後文黏在一起。</summary>
        public void Break()
        {
            if (!_started || _buf.Length == 0) return;
            if (_buf[^1] == '\n') return;
            _buf.Append("\n\n");
            target.Text = _buf.ToString();
        }

        /// <summary>全程沒有任何文字時的收尾說明（絕不假造內容）。</summary>
        public void Fallback(string text)
        {
            if (_started) return;
            _buf.Clear();
            _buf.Append(text);
            _started = true;
            target.Text = text;
        }
    }

    private async Task RunAsync(AiMessage reply, CancellationToken ct)
    {
        string url = ResolveUrl();
        string model = ResolveModel();
        // 共用額度不開放診斷代理：代理一次提問會連續發出多次請求（每輪工具查詢都是一次），
        // 一個人就能把大家的額度吃光。想用代理請改本機 Ollama 或自填金鑰。
        bool useTools = _settings.AiAgentMode && Tools is { HasTools: true }
                        && _settings.AiProviderEnum != AiProvider.SharedFree;
        var msgs = BuildMessages(useTools);
        var sink = new ReplySink(reply);

        for (int round = 1; ; round++)
        {
            bool allowTools = useTools && round <= MaxToolRounds;
            var r = await CallAsync(url, model, msgs, allowTools, sink, ct);

            if (r.Calls.Count == 0)
            {
                sink.Fallback("（模型沒有回覆任何內容）");
                return;
            }

            // 先把「助理要求呼叫工具」原樣記回對話，模型才認得後續的 tool 結果。
            msgs.Add(new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = r.Content.Length > 0 ? r.Content : null,
                ["tool_calls"] = r.Calls.Select(c => new
                {
                    id = c.Id,
                    type = "function",
                    function = new { name = c.Name, arguments = c.Args },
                }).ToList(),
            });

            StatusText = $"代理正在查詢本機讀值（第 {round} 輪，{r.Calls.Count} 項）…";
            foreach (var c in r.Calls)
            {
                ct.ThrowIfCancellationRequested();
                string result = Tools!.Invoke(c.Name, c.Args);
                InsertToolRow(reply, c, result);
                msgs.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = c.Id,
                    ["name"] = c.Name,
                    ["content"] = Trim(result, 6000),
                });
            }
            sink.Break();
        }
    }

    // 串流優先；端點拒收 stream=true 時自動退回整段模式重試一次。
    private async Task<Round> CallAsync(string url, string model, List<object> msgs,
                                        bool allowTools, ReplySink sink, CancellationToken ct)
    {
        if (_settings.AiStreaming)
        {
            try { return await SendOnceAsync(url, model, msgs, allowTools, sink, stream: true, ct); }
            catch (StreamUnsupportedException) { /* 改走整段模式，錯誤訊息由第二次呼叫如實回報 */ }
        }
        return await SendOnceAsync(url, model, msgs, allowTools, sink, stream: false, ct);
    }

    // 只有這些狀態碼才可能代表「這個端點不吃 stream=true」。
    // 401/403/429/5xx 是金鑰、額度或伺服器問題，退回整段模式重試只是白等一次，
    // 而且會讓錯誤訊息晚一輪才出現——直接照實拋出。
    internal static bool MayRejectStreaming(int status) => status is 400 or 404 or 405 or 422 or 501;

    private async Task<Round> SendOnceAsync(string url, string model, List<object> msgs,
                                            bool allowTools, ReplySink sink, bool stream, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = msgs,
            ["temperature"] = _settings.AiTemperature,
            ["stream"] = stream,
        };
        if (_settings.AiMaxTokens > 0) payload["max_tokens"] = _settings.AiMaxTokens;
        if (allowTools) payload["tools"] = Tools!.ToSchema();

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        string key = ResolveKey();
        if (key.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);

        using var resp = await Http.SendAsync(req, stream
            ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync(ct);
            int status = (int)resp.StatusCode;
            string msg = $"HTTP {status}：{Trim(err, 300)}";
            // 只在狀態碼真有可能是「不支援串流」時才退回整段模式重試一次；
            // 其餘一律當成真實錯誤立刻回報，不讓使用者多等一輪才看到 401。
            throw stream && MayRejectStreaming(status)
                ? new StreamUnsupportedException(msg)
                : new HttpRequestException(msg);
        }

        bool sse = (resp.Content.Headers.ContentType?.MediaType ?? "")
            .Contains("event-stream", StringComparison.OrdinalIgnoreCase);
        if (stream && sse) return await ReadStreamAsync(resp, sink, ct);

        return ParseWhole(await resp.Content.ReadAsStringAsync(ct), sink);
    }

    private static async Task<Round> ReadStreamAsync(HttpResponseMessage resp, ReplySink sink, CancellationToken ct)
    {
        var content = new StringBuilder();
        var accs = new SortedDictionary<int, ToolAcc>();

        using var raw = await resp.Content.ReadAsStreamAsync(ct);
        using var rd = new StreamReader(raw, Encoding.UTF8);
        while (await rd.ReadLineAsync(ct) is string line)
        {
            ct.ThrowIfCancellationRequested();
            line = line.Trim();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            string json = line[5..].Trim();
            if (json.Length == 0) continue;
            if (json == "[DONE]") break;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("choices", out var ch)
                    || ch.ValueKind != JsonValueKind.Array || ch.GetArrayLength() == 0) continue;
                if (!ch[0].TryGetProperty("delta", out var d)) continue;

                if (d.TryGetProperty("content", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    string piece = t.GetString() ?? "";
                    if (piece.Length > 0) { content.Append(piece); sink.Append(piece); }
                }
                if (d.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                    foreach (var call in tc.EnumerateArray()) Accumulate(accs, call);
            }
            catch (JsonException) { /* 中轉站偶爾插入非 JSON 心跳列，略過即可 */ }
        }

        var calls = accs.Select((kv, n) => new ToolCall(SyntheticId(kv.Value.Id, kv.Value.Name, n),
                                                       kv.Value.Name, kv.Value.Args.ToString()))
            .Where(c => c.Name.Length > 0)
            .ToList();
        return new Round(content.ToString(), calls);
    }

    /// <summary>
    /// 端點沒給 tool_call id 時自己補一個。必須帶序號：同一輪呼叫兩次同名工具時，
    /// 若兩者 id 相同，模型會把兩筆 tool 結果對到同一次呼叫，配對就錯了。
    /// </summary>
    internal static string SyntheticId(string id, string name, int index)
        => id.Length > 0 ? id : $"call_{name}_{index}";

    /// <summary>串流中逐塊拼回一個工具呼叫（名稱、參數會被切成很多片送來）。</summary>
    private sealed class ToolAcc
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Args = new();
    }

    private static void Accumulate(SortedDictionary<int, ToolAcc> accs, JsonElement call)
    {
        int idx = call.TryGetProperty("index", out var i) && i.TryGetInt32(out int n) ? n : accs.Count;
        if (!accs.TryGetValue(idx, out var a)) accs[idx] = a = new ToolAcc();

        if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(id.GetString())) a.Id = id.GetString()!;
        if (!call.TryGetProperty("function", out var f)) return;
        if (f.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(nm.GetString())) a.Name = nm.GetString()!;
        if (f.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.String)
            a.Args.Append(ar.GetString());
    }

    private static Round ParseWhole(string body, ReplySink sink)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { throw new InvalidOperationException("回應不是有效的 JSON：" + Trim(body, 300)); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message))
            {
                string text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                    ? (c.GetString() ?? "") : "";
                var calls = new List<ToolCall>();
                if (message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                    foreach (var call in tc.EnumerateArray())
                    {
                        if (!call.TryGetProperty("function", out var f)) continue;
                        string name = f.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                        if (name.Length == 0) continue;
                        string args = f.TryGetProperty("arguments", out var ar)
                            ? (ar.ValueKind == JsonValueKind.String ? ar.GetString() ?? "" : ar.GetRawText())
                            : "";
                        string id = call.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                        calls.Add(new ToolCall(SyntheticId(id, name, calls.Count), name, args));
                    }

                if (text.Trim().Length > 0) sink.Append(text.Trim());
                if (text.Trim().Length > 0 || calls.Count > 0) return new Round(text, calls);
            }

            // 部分相容端點的錯誤格式
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
                throw new InvalidOperationException(err.ValueKind == JsonValueKind.Object
                    && err.TryGetProperty("message", out var em)
                    ? em.GetString() ?? Trim(body, 300) : Trim(body, 300));
            throw new InvalidOperationException("回應格式無法解析：" + Trim(body, 300));
        }
    }

    // 系統提示 ＋ 硬體快照 ＋ 既有對話（工具紀錄與失敗提示不回送模型）
    private List<object> BuildMessages(bool useTools)
    {
        string sys = string.IsNullOrWhiteSpace(_settings.AiSystemPrompt) ? DefaultSystemPrompt : _settings.AiSystemPrompt;
        string snapshot = "";
        try { snapshot = SnapshotProvider?.Invoke() ?? ""; } catch { /* 快照彙整失敗不阻斷對話 */ }
        if (snapshot.Length > 0) sys += "\n\n===== 本機硬體資訊（真實讀值）=====\n" + snapshot;
        if (useTools) sys += "\n\n" + AgentPrompt;

        // 先挑出「真的該回送」的訊息，再由後往前只留最近 N 則。
        var keep = SelectHistory(Messages, _settings.AiHistoryTurns, out int dropped);
        // 有裁掉東西就讓模型知道，否則它會以為自己看到的是完整對話而誤引「你剛才說過」。
        if (dropped > 0) sys += $"\n\n（註：為控制長度，本次僅回送最近 {keep.Count} 則對話，較早的 {dropped} 則已省略。）";

        var msgs = new List<object> { new { role = "system", content = sys } };
        foreach (var m in keep)
            msgs.Add(new { role = m.IsUser ? "user" : "assistant", content = m.Text });
        return msgs;
    }

    /// <summary>
    /// 挑出該回送給模型的歷史訊息：略過最後一則（占位中的 AI 回覆）、工具紀錄（只給人看）、
    /// 空字串與失敗提示，再依 <paramref name="limit"/> 由前往後裁掉過舊的幾則。
    /// 硬體快照本身就佔掉不少上下文，整段歷史一併送出會越談越貴，也容易撞上模型上限。
    /// <paramref name="limit"/> 為 0 表示不裁。<paramref name="dropped"/> 回報裁掉幾則——
    /// 呼叫端必須把這個數字告訴模型，不能讓它以為自己看到的是完整對話。
    /// </summary>
    internal static List<AiMessage> SelectHistory(IReadOnlyList<AiMessage> all, int limit, out int dropped)
    {
        var keep = new List<AiMessage>();
        for (int i = 0; i < all.Count - 1; i++)          // 最後一則是占位的 AI 回覆
        {
            var m = all[i];
            if (m.IsTool) continue;                      // 工具紀錄只給人看
            if (m.Text.Length == 0) continue;
            if (!m.IsUser && (m.Text == Placeholder || m.Text.StartsWith('⚠'))) continue;
            keep.Add(m);
        }

        dropped = 0;
        if (limit > 0 && keep.Count > limit)
        {
            dropped = keep.Count - limit;
            keep.RemoveRange(0, dropped);
        }
        return keep;
    }

    // 工具紀錄插在回覆氣泡之前，讓「先查詢、後結論」的順序在畫面上讀得順。
    private void InsertToolRow(AiMessage reply, ToolCall call, string result)
    {
        string args = call.Args.Trim();
        if (args is "{}" or "null") args = "";
        var row = new AiMessage
        {
            IsUser = false,
            IsTool = true,
            Text = call.Name + (args.Length > 0 ? " " + Trim(args, 120) : "")
                   + " → " + Trim(OneLine(result), 160),
        };
        int at = Messages.IndexOf(reply);
        if (at < 0) Messages.Add(row); else Messages.Insert(at, row);
    }

    private static string OneLine(string s) => s.Replace("\r", "").Replace('\n', '；');

    private string ResolveUrl()
    {
        // 共用額度：端點寫死在程式裡（不吃使用者設定），這樣就算他填過別的端點也不會把
        // 共用金鑰以外的東西送錯地方——不過這裡本來就沒有金鑰可送，驗證由中轉那側做。
        if (_settings.AiProviderEnum == AiProvider.SharedFree)
        {
            if (!SharedAiEndpoint.IsConfigured)
                throw new InvalidOperationException("免費共用額度目前尚未啟用，請改用本機 Ollama 或自填端點與金鑰。");
            return Normalize(SharedAiEndpoint.BaseUrl.Trim().TrimEnd('/'));
        }

        string baseUrl = (_settings.AiBaseUrl ?? "").Trim().TrimEnd('/');
        if (baseUrl.Length == 0) throw new InvalidOperationException("尚未設定 API 端點（Base URL）。");
        return Normalize(baseUrl);

        // 端點正規化：使用者可填 http://host:port 或 .../v1；統一補到 /v1/chat/completions。
        static string Normalize(string b)
            => b.EndsWith("/chat/completions") ? b
             : b.EndsWith("/v1") ? b + "/chat/completions"
             : b + "/v1/chat/completions";
    }

    private string ResolveModel()
    {
        // 共用額度刻意不讓使用者挑模型：送一個代號過去，用哪個模型由中轉決定。
        if (_settings.AiProviderEnum == AiProvider.SharedFree) return SharedAiEndpoint.Model;

        string model = (_settings.AiModel ?? "").Trim();
        if (model.Length == 0) throw new InvalidOperationException("尚未設定模型名稱。");
        return model;
    }

    /// <summary>這次請求要帶的 API 金鑰；共用額度不帶（金鑰在中轉那側，程式裡沒有）。</summary>
    private string ResolveKey()
        => _settings.AiProviderEnum == AiProvider.SharedFree ? "" : (_settings.AiApiKey ?? "").Trim();

    private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    private static string Explain(Exception ex) => ex switch
    {
        TaskCanceledException => "連線逾時（模型載入或網路過慢）。本機 Ollama 首次載入模型較久，可稍後重試。",
        HttpRequestException he when he.Message.Contains("actively refused") || he.Message.Contains("failed to respond")
            => "無法連線到端點。若使用本機 Ollama，請確認已安裝並執行（ollama serve）且模型已下載。",
        _ => ex.Message,
    };
}
