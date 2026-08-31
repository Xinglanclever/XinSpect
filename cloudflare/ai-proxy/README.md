# 免費共用額度中轉（Cloudflare Worker）

曦覽的「免費共用（作者提供）」那個 AI 選項，連的就是這支 Worker。
上游用的是 Cloudflare 自家的 **Workers AI**，所以整條路上沒有任何 API 金鑰。

## 為什麼不把金鑰直接編進 exe

編進去就等於公開。`strings XinSpect.exe` 撈得到、dnSpy 反編譯得到、抓包看得到明文；
就算加一層自製解密，解密金鑰也躺在同一顆執行檔裡，反編譯的人多花五分鐘而已。
如果那把金鑰還進了公開倉庫，GitHub 的機密掃描會直接通報供應商把它作廢。

所以分工是這樣：

| 東西 | 放哪 | 公開會怎樣 |
|------|------|-----------|
| Worker 網址 | `Services/SharedAiEndpoint.cs`（編進 exe） | 沒事，本來就是公開端點 |
| 呼叫模型的權限 | Worker 的 `[ai]` 綁定 | 只有這支 Worker 用得到，帳號外的人拿不到 |

exe 裡從頭到尾沒有秘密，所以沒有「要不要加密」的問題。

## 部署（第一次）

需要一個 Cloudflare 帳號（免費方案就夠）與 Node.js。以下都在 `cloudflare/ai-proxy/` 底下執行。

```bash
npx wrangler login
```

瀏覽器會跳出 Cloudflare 的授權頁，捲到最下面按 **Allow**，然後回到終端機。

接著建一個 KV 命名空間來記次數與留言：

```bash
npx wrangler kv namespace create QUOTA
```

它會印出一行 `id = "…"`，把那串填進 `wrangler.toml` 的 `PUT_KV_NAMESPACE_ID_HERE`。
然後部署：

```bash
npx wrangler deploy
```

部署完會印出一個 `https://xinspect-ai.<你的子網域>.workers.dev`。把它加上 `/v1` 填進
`Services/SharedAiEndpoint.cs` 的 `BaseUrl`，重新建置，那個選項才會從「作者尚未啟用」變成可選：

```csharp
public static readonly string BaseUrl = "https://xinspect-ai.你的子網域.workers.dev/v1";
```

## 驗一下有沒有活

```bash
curl https://xinspect-ai.你的子網域.workers.dev/
```

回 `{"ok":true,"service":"xinspect-ai-proxy","enabled":true}` 就是通了。

## 端點

| 路徑 | 用途 |
|------|------|
| `POST /v1/chat/completions` | OpenAI 相容聊天中轉。模型由中轉決定，客端送什麼 `model` 都不看。 |
| `POST /feedback` | 收留言建議，只認 `message`、`contact`、`version` 三個欄位。 |
| `GET /` | 健康檢查與總開關狀態。 |

Workers AI 回的不是 OpenAI 的形狀，Worker 會把它包成 `chat.completion` 再回給程式。
這條路一律不串流：客端要求 `stream=true` 也會收到一般 JSON，`AiService` 認得出來會自動整段解析，
只是評價文字會一次出現，不是逐字冒出來。

## 上限與煞車

參數都在 `worker.js` 開頭的 `LIMITS`：每個 IP 每小時 6 次評價、全站每天 400 次、
回覆上限 1200 tokens、提示詞上限 24000 字、留言每 IP 每小時 3 則。改完要重新 `deploy`。

要臨時關掉整條共用額度，把 `wrangler.toml` 的 `ENABLED` 改成 `"0"` 再 deploy；
程式那側會收到 503 並顯示「作者已暫時關閉免費共用額度」，不會當掉。

## 看留言

```bash
npx wrangler kv key list --binding QUOTA --prefix feedback:
```

拿到 key 之後：

```bash
npx wrangler kv key get --binding QUOTA "feedback:2026-08-31T..."
```

## 換模型或換上游

Workers AI 的模型清單：<https://developers.cloudflare.com/workers-ai/models/>。
換一個就改 `wrangler.toml` 的 `MODEL` 再 deploy。

想改用外部的 OpenAI 相容端點（自己的 OpenAI 金鑰、或某個中轉站），把 `wrangler.toml` 裡
`UPSTREAM_URL` 那行取消註解填好，再存一把金鑰：

```bash
npx wrangler secret put UPSTREAM_KEY
```

它會互動式要你貼上金鑰，不會出現在指令歷史裡，之後在儀表板上也看不到明文。
一旦設了 `UPSTREAM_KEY`，Worker 就改走外部端點，不再用 Workers AI；那時每一次評價都在花你的錢，
`LIMITS` 那幾個數字要重新想一遍。

## 成本

Workers 免費方案每天 10 萬次請求，這個用量用不完；Workers AI 每天也有一定的免費額度，
超過才計費。曦覽只讓「一鍵評價」走這條額度——自由對話、主動診斷與診斷代理都不走
（代理一次提問可能連發六、七次請求），這個限制寫在 `Services/SharedAiEndpoint.cs`。

