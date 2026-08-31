/**
 * 曦覽 XinSpect ・ 免費共用額度中轉（Cloudflare Worker）
 *
 * 為什麼要有這一層：作者想分享自己付費的 AI 額度給所有使用者，但金鑰一旦編進 exe 就等於公開
 * （strings 撈得到、dnSpy 反編譯得到、抓包也看得到明文；就算再加一層解密，解密金鑰也在同一顆
 * 執行檔裡）。所以 exe 裡只放這支 Worker 的網址——那是公開資訊，被誰知道都無所謂；真正的金鑰
 * 只存在 Cloudflare 的 Secret 裡，連作者自己在儀表板上也看不到明文。
 *
 * 這支 Worker 同時做三件事：
 *   POST /v1/chat/completions   OpenAI 相容的聊天中轉（只給「一鍵評價」用）
 *   POST /feedback              收「留言建議」（只收使用者自己打的字）
 *   GET  /inbox?k=…             作者自己看留言的唯讀列表（要帶 INBOX_KEY）
 *
 * 部署方式見同一個資料夾的 README.md。
 */

// ── 可調參數：改這裡就好，不必動下面的邏輯 ──────────────────────────────────
const LIMITS = {
  ipPerHour: 6,           // 同一個來源 IP 每小時可用幾次評價
  dayTotal: 400,          // 全站每天總次數上限（避免額度一夜燒光）
  maxTokens: 1200,        // 回覆長度上限（不管客端要求多少）
  maxPromptChars: 24000,  // 送給上游的提示詞總長度上限
  feedbackIpPerHour: 3,   // 同一個來源 IP 每小時可送幾則建議
  feedbackMaxChars: 2000, // 單則建議長度上限（與 FeedbackService.MaxLength 一致）
};

const DEFAULT_UPSTREAM = "https://api.openai.com/v1/chat/completions";
// 預設走 Cloudflare 自家的 Workers AI：不需要任何外部金鑰，免費方案每天有一定額度。
const DEFAULT_MODEL = "@cf/meta/llama-3.3-70b-instruct-fp8-fast";

export default {
  async fetch(request, env) {
    if (request.method === "OPTIONS") return cors(new Response(null, { status: 204 }));
    const url = new URL(request.url);
    try {
      if (url.pathname === "/" || url.pathname === "/health")
        return json({ ok: true, service: "xinspect-ai-proxy", enabled: isEnabled(env) });
      if (url.pathname === "/v1/chat/completions" && request.method === "POST")
        return await handleChat(request, env);
      if (url.pathname === "/feedback" && request.method === "POST")
        return await handleFeedback(request, env);
      if (url.pathname === "/inbox" && request.method === "GET")
        return await handleInbox(request, env);
      return json({ error: { message: "沒有這個路徑。" } }, 404);
    } catch (e) {
      // 上游或 KV 出錯時，如實回一句話就好——不要把內部細節（更不要把金鑰）倒出去。
      return json({ error: { message: "中轉發生錯誤：" + short(e && e.message ? e.message : e) } }, 502);
    }
  },
};

// ── 一鍵評價 ────────────────────────────────────────────────────────────────
async function handleChat(request, env) {
  if (!isEnabled(env))
    return json({ error: { message: "作者已暫時關閉免費共用額度。請改用本機 Ollama，或在設定裡自填端點與金鑰。" } }, 503);
  if (!env.AI && !env.UPSTREAM_KEY)
    return json({ error: { message: "中轉尚未接上任何上游（沒有 AI 綁定，也沒有 UPSTREAM_KEY）。" } }, 503);

  const ip = clientIp(request);
  const gate = await checkQuota(env, ip, "ai", LIMITS.ipPerHour, LIMITS.dayTotal);
  if (gate) return gate;

  let body;
  try { body = await request.json(); }
  catch { return json({ error: { message: "請求不是合法的 JSON。" } }, 400); }

  const messages = Array.isArray(body && body.messages) ? body.messages : null;
  if (!messages || messages.length === 0)
    return json({ error: { message: "messages 是空的。" } }, 400);

  // 只留 role/content 兩個欄位：客端不該有辦法從這裡夾帶 tools、function_call 之類的東西進上游。
  const clean = [];
  let chars = 0;
  for (const m of messages) {
    const role = m && typeof m.role === "string" ? m.role : "user";
    const content = m && typeof m.content === "string" ? m.content : "";
    chars += content.length;
    if (chars > LIMITS.maxPromptChars)
      return json({ error: { message: `內容太長（超過 ${LIMITS.maxPromptChars} 字），共用額度無法處理。` } }, 413);
    clean.push({ role: role === "system" || role === "assistant" ? role : "user", content });
  }

  // 模型由中轉決定，不看客端送什麼——共用額度得由作者控制成本。
  const model = env.MODEL || DEFAULT_MODEL;
  const maxTokens = Math.min(
    Number(body.max_tokens) > 0 ? Number(body.max_tokens) : LIMITS.maxTokens, LIMITS.maxTokens);
  const t = Number(body.temperature);
  const temperature = Number.isFinite(t) ? Math.max(0, Math.min(2, t)) : undefined;

  // 設了 UPSTREAM_KEY 就走外部 OpenAI 相容端點，否則用 Cloudflare 自家的 Workers AI。
  return env.UPSTREAM_KEY
    ? await viaOpenAi(env, model, clean, maxTokens, temperature, body.stream === true)
    : await viaWorkersAi(env, model, clean, maxTokens, temperature);
}

/** 外部 OpenAI 相容端點：整包轉發，串流回應原樣通過。 */
async function viaOpenAi(env, model, messages, maxTokens, temperature, stream) {
  const payload = { model, messages, stream, max_tokens: maxTokens };
  if (temperature !== undefined) payload.temperature = temperature;

  const upstream = await fetch(env.UPSTREAM_URL || DEFAULT_UPSTREAM, {
    method: "POST",
    headers: { "content-type": "application/json", authorization: "Bearer " + env.UPSTREAM_KEY },
    body: JSON.stringify(payload),
  });

  // 直接把上游的 body 串回去，但只挑安全的標頭轉發。
  const headers = new Headers({
    "content-type": upstream.headers.get("content-type") || "application/json",
    "cache-control": "no-store",
  });
  return cors(new Response(upstream.body, { status: upstream.status, headers }));
}

/**
 * Workers AI：回的是 { response: "…" }，不是 OpenAI 的形狀，所以在這裡包成 OpenAI 的
 * chat.completions 回應——程式那側只認得 OpenAI 格式，不該為了中轉的實作細節去改客端。
 *
 * 這條路一律不串流。客端要求 stream=true 時收到的是一般 JSON，AiService 判斷回應不是
 * text/event-stream 就會自動整段解析（Services/AiService.cs 的 SendOnceAsync），不會出錯；
 * 代價是評價文字會一次出現而不是逐字冒出來。要串流就得把 Workers AI 的 SSE 逐塊改寫成
 * OpenAI 的 delta 形狀，為了共用額度這個用途不值得。
 */
async function viaWorkersAi(env, model, messages, maxTokens, temperature) {
  const input = { messages, max_tokens: maxTokens };
  if (temperature !== undefined) input.temperature = temperature;

  const out = await env.AI.run(model, input);
  const text = typeof out === "string"
    ? out
    : (out && (out.response ?? (out.result && out.result.response))) || "";
  if (!text) return json({ error: { message: "上游沒有回傳內容，請稍後再試。" } }, 502);

  return json({
    id: "chatcmpl-" + crypto.randomUUID(),
    object: "chat.completion",
    created: Math.floor(Date.now() / 1000),
    model,
    choices: [{ index: 0, message: { role: "assistant", content: text }, finish_reason: "stop" }],
  });
}

// ── 留言建議 ────────────────────────────────────────────────────────────────
// 只收三個欄位：留言、選填的聯絡方式、版本號。其他一律丟掉——程式那側說了「只送你打的字」，
// 這側就不能偷收別的，否則那句話就是騙人的。
async function handleFeedback(request, env) {
  const ip = clientIp(request);
  const gate = await checkQuota(env, ip, "fb", LIMITS.feedbackIpPerHour, 0);
  if (gate) return gate;

  let body;
  try { body = await request.json(); }
  catch { return json({ error: { message: "請求不是合法的 JSON。" } }, 400); }

  const message = typeof body.message === "string" ? body.message.trim() : "";
  if (message.length === 0) return json({ error: { message: "留言是空的。" } }, 400);
  if (message.length > LIMITS.feedbackMaxChars)
    return json({ error: { message: `留言太長（超過 ${LIMITS.feedbackMaxChars} 字）。` } }, 413);

  const record = {
    at: new Date().toISOString(),
    version: typeof body.version === "string" ? short(body.version, 40) : "",
    contact: typeof body.contact === "string" ? short(body.contact, 120) : "",
    message,
    country: request.headers.get("cf-ipcountry") || "",
  };

  // 留一份在 KV（作者可用 wrangler kv key list 撈出來看），有設 webhook 就同時推一則通知。
  if (env.QUOTA) {
    const key = `feedback:${record.at}:${Math.random().toString(36).slice(2, 8)}`;
    await env.QUOTA.put(key, JSON.stringify(record));
  }
  if (env.FEEDBACK_WEBHOOK) {
    const text = `【曦覽建議】v${record.version || "?"}${record.country ? " ・ " + record.country : ""}`
      + `${record.contact ? " ・ 聯絡：" + record.contact : " ・ 匿名"}\n${record.message}`;
    try {
      await fetch(env.FEEDBACK_WEBHOOK, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ content: text.slice(0, 1900), text: text.slice(0, 1900) }),
      });
    } catch { /* 通知推不出去不該讓使用者的建議白寫，KV 那份已經存好了 */ }
  }
  if (!env.QUOTA && !env.FEEDBACK_WEBHOOK)
    return json({ error: { message: "中轉尚未設定收件方式（KV 或 webhook）。" } }, 503);

  return json({ ok: true });
}

// ── 作者收件匣 ──────────────────────────────────────────────────────────────

/** 一頁幾則。KV 每讀一筆算一個子請求，免費方案單次請求上限 50 個，所以一頁不能貪多。 */
const INBOX_PAGE = 30;

/**
 * 唯讀的留言列表，只有作者看得到：帶對 INBOX_KEY 才回內容，只讀不寫，不提供刪除
 * （要刪還是回儀表板或 wrangler，避免手機上誤觸就少一則建議）。
 * 密碼放網址的 k 參數是為了手機點一下就能看，代價是它會留在瀏覽器歷史紀錄裡——
 * 所以這裡一律 no-store、noindex、no-referrer，並且對同一個 IP 也套次數上限，讓猜密碼不划算。
 */
async function handleInbox(request, env) {
  const url = new URL(request.url);
  if (!env.INBOX_KEY)
    return page(503, "收件匣尚未啟用", "還沒設定 INBOX_KEY，先跑一次 <code>wrangler secret put INBOX_KEY</code>。");
  if (!env.QUOTA)
    return page(503, "沒有資料來源", "這支 Worker 沒有綁 KV，留言沒有存下來的地方。");

  const gate = await checkQuota(env, clientIp(request), "ib", 60, 0);
  if (gate) return gate;

  const auth = request.headers.get("authorization") || "";
  const given = url.searchParams.get("k") || (auth.startsWith("Bearer ") ? auth.slice(7) : "");
  if (!safeEqual(given, String(env.INBOX_KEY)))
    return page(401, "密碼不對", "網址後面要帶 <code>?k=你設定的那組密碼</code>。");

  // key 名開頭就是 ISO 時間戳，字典序等於時間序；倒過來就是最新的在最前面。
  const keys = [];
  let cursor, truncated = false;
  for (let i = 0; i < 3; i++) {
    const r = await env.QUOTA.list({ prefix: "feedback:", limit: 1000, cursor });
    for (const k of r.keys) keys.push(k.name);
    if (r.list_complete) { truncated = false; break; }
    cursor = r.cursor;
    truncated = true;                                       // 還沒列完就用光了配額
  }
  keys.reverse();

  const skip = Math.max(0, Number(url.searchParams.get("skip")) || 0);
  const slice = keys.slice(skip, skip + INBOX_PAGE);
  const values = await Promise.all(slice.map(k => env.QUOTA.get(k)));
  return renderInbox(given, keys.length, skip, slice, values, truncated);
}

/** 把一頁留言排成卡片。壞掉的紀錄原樣列出，不要因為一筆爛資料就整頁看不到。 */
function renderInbox(key, total, skip, keys, values, truncated) {
  const cards = keys.map((name, i) => {
    let r = null;
    try { r = JSON.parse(values[i] || ""); } catch { /* 下面會原樣列出 */ }
    if (!r || typeof r !== "object")
      return `<div class="c"><div class="m">${esc(name)}</div>`
           + `<pre>（這筆不是合法 JSON，原樣列出）\n${esc(values[i] || "")}</pre></div>`;
    const meta = [
      taipei(r.at),
      r.version ? "v" + r.version : "",
      r.country || "",
      r.contact ? "聯絡：" + r.contact : "",             // 沒留聯絡方式就整段不顯示
    ].filter(Boolean).map(esc).join(" ・ ");
    return `<div class="c"><div class="m">${meta}</div><pre>${esc(r.message || "")}</pre></div>`;
  }).join("\n");

  const q = s => `?k=${encodeURIComponent(key)}${s > 0 ? "&skip=" + s : ""}`;
  const nav = [];
  if (skip > 0) nav.push(`<a href="${esc(q(Math.max(0, skip - INBOX_PAGE)))}">← 較新的</a>`);
  if (skip + INBOX_PAGE < total) nav.push(`<a href="${esc(q(skip + INBOX_PAGE))}">較早的 →</a>`);
  const sub = total === 0
    ? "目前沒有任何留言。"
    : `共 ${total} 則${truncated ? "（超過 3000 則，只列到前 3000）" : ""}`
      + ` ・ 顯示第 ${skip + 1}–${skip + keys.length} 則 ・ 最新的在最上面`;

  return page(200, "留言建議",
    `<p class="sub">${esc(sub)}</p>${cards}<p class="sub">${nav.join(" ・ ")}</p>`);
}

/**
 * ISO 時間轉台北時間，格式 2026-8-31 12:10:19。轉不動就原樣顯示，不要為了好看而編一個時間出來。
 * 直接加 8 小時而不靠 Intl：台北沒有日光節約時間，這個偏移永遠成立，也不必擔心執行環境的時區資料。
 */
function taipei(iso) {
  const d = new Date(iso);
  if (isNaN(d.getTime())) return String(iso || "");
  const t = new Date(d.getTime() + 8 * 3600 * 1000);
  const p = n => String(n).padStart(2, "0");
  return `${t.getUTCFullYear()}-${t.getUTCMonth() + 1}-${t.getUTCDate()} `
       + `${p(t.getUTCHours())}:${p(t.getUTCMinutes())}:${p(t.getUTCSeconds())}`;
}

function esc(s) {
  return String(s === undefined || s === null ? "" : s)
    .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
}

// 逐字比到底、不提前 return：不讓回應時間洩漏「前幾個字猜對了」。長度會洩漏，那個無妨。
function safeEqual(a, b) {
  a = String(a); b = String(b);
  if (a.length !== b.length || a.length === 0) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

// ── 額度與工具 ──────────────────────────────────────────────────────────────

// 總開關：把 ENABLED 設成 0／false／off 就整條關掉（額度用完或被濫用時的緊急煞車）。
function isEnabled(env) {
  const v = (env.ENABLED === undefined || env.ENABLED === null) ? "1" : String(env.ENABLED).toLowerCase();
  return !(v === "0" || v === "false" || v === "off" || v === "no");
}

function clientIp(request) {
  return request.headers.get("cf-connecting-ip") || "0.0.0.0";
}

/**
 * 每小時的 IP 次數與每天的全站總次數。沒綁 KV 就沒有上限——這是刻意的：
 * 寧可先能跑，也在 README 裡把「沒綁 KV 等於沒有防濫用」寫清楚。
 * 回傳 null 代表通過，回傳 Response 代表已經擋下來了。
 */
async function checkQuota(env, ip, tag, perHour, perDay) {
  if (!env.QUOTA) return null;
  const now = new Date();
  const hour = now.toISOString().slice(0, 13);              // 2026-08-31T14
  const day = now.toISOString().slice(0, 10);               // 2026-08-31

  if (perHour > 0) {
    const used = await bump(env, `rl:${tag}:${hour}:${ip}`, 3900);
    if (used > perHour)
      return json({ error: { message: `使用太頻繁：同一個來源每小時上限 ${perHour} 次，請稍後再試。` } }, 429);
  }
  if (perDay > 0) {
    const used = await bump(env, `rl:${tag}:day:${day}`, 90000);
    if (used > perDay)
      return json({ error: { message: "今天的共用額度已經用完了，請明天再來，或改用本機 Ollama／自填金鑰。" } }, 429);
  }
  return null;
}

// KV 沒有原子遞增；併發時可能少算一兩次。對「防濫用」這個用途夠了，不值得為它上 Durable Object。
async function bump(env, key, ttlSeconds) {
  const cur = Number(await env.QUOTA.get(key)) || 0;
  const next = cur + 1;
  await env.QUOTA.put(key, String(next), { expirationTtl: ttlSeconds });
  return next;
}

function json(obj, status = 200) {
  return cors(new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" },
  }));
}

/**
 * 收件匣用的 HTML 外殼。刻意不套 cors()：這一頁是給作者自己看的，不該讓任何網站跨站讀走。
 * 密碼在網址裡，所以 no-store（不留快取）、noindex（不進搜尋引擎）、no-referrer（不外洩網址）。
 */
function page(status, title, bodyHtml) {
  const html = `<!doctype html><html lang="zh-Hant"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="robots" content="noindex,nofollow"><title>${esc(title)} ・ 曦覽收件匣</title>
<style>
:root{color-scheme:light dark}
body{margin:0;padding:16px;font:15px/1.7 "Microsoft JhengHei",system-ui,sans-serif;
     background:#f6f7f9;color:#1b1d21;max-width:820px;margin:0 auto}
h1{font-size:18px;margin:0 0 2px}
.sub{color:#7a8290;font-size:13px;margin:0 0 14px}
.c{background:#fff;border:1px solid #e2e5ea;border-left:3px solid #4C8DFF;
   border-radius:6px;padding:10px 12px;margin:0 0 10px}
.m{color:#7a8290;font-size:12px;margin-bottom:6px}
pre{margin:0;white-space:pre-wrap;word-break:break-word;font:inherit}
a{color:#4C8DFF;text-decoration:none}
code{font-size:13px}
@media(prefers-color-scheme:dark){
  body{background:#15171b;color:#e6e8ec}
  .c{background:#1e2126;border-color:#2c3037}
}
</style></head><body><h1>曦覽 XinSpect ・ 留言建議</h1>${bodyHtml}</body></html>`;
  return new Response(html, {
    status,
    headers: {
      "content-type": "text/html; charset=utf-8",
      "cache-control": "no-store",
      "referrer-policy": "no-referrer",
      "x-robots-tag": "noindex, nofollow",
    },
  });
}

function cors(resp) {
  resp.headers.set("access-control-allow-origin", "*");
  resp.headers.set("access-control-allow-headers", "content-type, authorization");
  resp.headers.set("access-control-allow-methods", "POST, GET, OPTIONS");
  return resp;
}

function short(s, n = 160) {
  s = String(s === undefined || s === null ? "" : s);
  return s.length > n ? s.slice(0, n) + "…" : s;
}
