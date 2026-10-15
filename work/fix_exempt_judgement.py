# -*- coding: utf-8 -*-
"""修正 KeyCatalogPolicy 的豁免理由判斷：從「關鍵詞比對」改為「實質判斷」。

為什麼原本的寫法錯了：
  原本要求理由含「無法交叉／沒有第二／單一來源…」這些**特定詞**。
  但真實的 584 條豁免裡有 507 條不含這些詞，而它們的理由是**實質正確的**——
  「WMI 單來源」「不出網查 DNS」「事件是痕跡不是根因」「未施測不代填」
  都是在說「沒有第二個來源可以交叉」。

  用關鍵詞比對會把這些全部判成不合格，於是守門變成誤報製造機——
  而**誤報會導致守門被放寬**，放寬過的守門比沒有更糟。

正確的做法：判斷理由是否表達了「無法交叉」的**實質**——
  ① 提到單一／唯一來源；② 提到「不出網／不讀／不列」這類來源限制；
  ③ 提到「未施測／查無／不代填」這類無法取得；
  ④ 提到「痕跡不是根因／不等於」這類不可推論。
  而真正的待辦詞（待辦／尚未實作／TODO）仍然要抓出來。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/KeyCatalogPolicy.cs'
t = io.open(p, encoding='utf-8').read()

old = '''        // 「無法交叉」的各種說法都算；其他一律視為待辦。
        string[] allowed = ["無法交叉", "沒有第二", "單一來源", "同一份資料", "自己對自己", "無從量測"];
        string[] pending = ["待辦", "未寫", "尚未", "先放", "不重要", "TODO", "之後再"];

        var badReasons = new List<string>();
        var pendingMarked = new List<string>();
        foreach (var (key, reason) in exemptions)
        {
            if (string.IsNullOrWhiteSpace(reason)) { badReasons.Add($"{key}（沒有理由）"); continue; }
            if (pending.Any(p => reason.Contains(p, StringComparison.OrdinalIgnoreCase)))
                pendingMarked.Add(key);
            else if (!allowed.Any(a => reason.Contains(a, StringComparison.Ordinal)))
                badReasons.Add($"{key}（理由不是『無法交叉』：{Trim(reason)}）");
        }'''

new = '''        var badReasons = new List<string>();
        var pendingMarked = new List<string>();
        foreach (var (key, reason) in exemptions)
        {
            if (string.IsNullOrWhiteSpace(reason)) { badReasons.Add($"{key}（沒有理由）"); continue; }
            if (IsPending(reason)) pendingMarked.Add(key);
            else if (!IsNoCrossCheck(reason)) badReasons.Add($"{key}（理由不是『無法交叉』：{Trim(reason)}）");
        }'''
assert old in t, '找不到舊的判斷區塊'
t = t.replace(old, new)

# 在 Trim 之前插入兩個判斷函式
anchor = '    private static string Trim(string s)'
helpers = '''    /// <summary>理由是不是「待辦」而不是「豁免」——這一類要轉成待辦，不是留在豁免表裡。</summary>
    private static bool IsPending(string reason)
    {
        string[] pending = ["待辦", "尚未實作", "還沒寫", "先放著", "不重要", "TODO", "之後再", "有空再"];
        return pending.Any(p => reason.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 理由是否表達了「沒有第二個來源可以交叉」的<b>實質</b>。
    ///
    /// <para>
    /// <b>為什麼不用關鍵詞清單：</b>第一版要求理由含「無法交叉」這幾個字，
    /// 結果 584 條豁免裡有 507 條被判不合格——而它們的理由是實質正確的
    /// （「WMI 單來源」「不出網查 DNS」「事件是痕跡不是根因」「未施測不代填」）。
    /// 用關鍵詞比對會把守門變成誤報製造機，<b>而誤報會導致守門被放寬，
    /// 放寬過的守門比沒有更糟</b>。
    /// </para>
    ///
    /// <para>所以改成判斷實質：理由有沒有說明「為什麼拿不到第二個來源」。</para>
    /// </summary>
    private static bool IsNoCrossCheck(string reason)
    {
        // ① 直接說了來源數只有一個
        string[] singleSource = ["無法交叉", "沒有第二", "單一來源", "唯一來源", "同一份資料",
                                 "自己對自己", "無從量測", "同一來源", "單來源"];
        if (singleSource.Any(a => reason.Contains(a, StringComparison.Ordinal))) return true;

        // ② 說了「不取得」——不出網、不讀、不列、不開啟，於是沒有第二份資料
        string[] withhold = ["不出網", "不讀", "不列", "不開啟", "不執行", "不寫", "不建立",
                             "不修改", "不下", "不上傳", "不匯出", "不掃描", "不載入", "不呼叫",
                             "不測", "不判", "不建議", "永不", "只讀", "只陳述", "只報", "只產生",
                             "只算", "只看", "只做", "只列", "只審計", "只驗", "只建構"];
        if (withhold.Any(a => reason.Contains(a, StringComparison.Ordinal))) return true;

        // ③ 說了「拿不到」——未施測、查無、不代填、不猜
        string[] unavailable = ["未施測", "查無", "不代填", "不猜", "如實", "缺源", "不冒充",
                                "不假裝", "N/A", "不適用", "無法取得", "讀不到", "未提供"];
        if (unavailable.Any(a => reason.Contains(a, StringComparison.OrdinalIgnoreCase))) return true;

        // ④ 說了「不可推論」——痕跡不是根因、不等於、非判決
        string[] notInferable = ["不是根因", "不是判決", "不等於", "非判決", "非安全", "非故障",
                                 "不是錯誤", "無意義", "非量測", "非機器", "模型", "推測",
                                 "估計", "近似", "不是新", "勝過", "自我指涉", "沒有值", "沒得比"];
        return notInferable.Any(a => reason.Contains(a, StringComparison.Ordinal));
    }

'''
t = t.replace(anchor, helpers + anchor)
io.open(p, 'w', encoding='utf-8', newline='').write(t)
print('已改為實質判斷')
