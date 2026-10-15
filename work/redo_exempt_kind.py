# -*- coding: utf-8 -*-
"""重做 P1-082：豁免的正當性改由具名類別判斷，不再用關鍵詞比對。

為什麼要重做（這一輪最重要的一件事）：
  第一版要求豁免理由的文字含「無法交叉」這一類詞，584 條真實豁免裡有 507 條被判不合格。
  我當時的處置是**連續三次擴大關鍵詞表**——那正是本計畫 P1-063 警告的事：
  用擴大詞表讓紅燈變綠，只是形式上看起來像修正。

  用「理由文字含什麼詞」判斷「豁免是否正當」從一開始就不成立：
  詞表窄則大量誤報（誤報導致守門被放寬），詞表寬則形同虛設。

正確做法：正當性由有限的類別列舉表達，理由文字仍然必填但只給人看。
  這樣待辦就選不出合法類別——待辦不是另一種豁免，是還沒做的事。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/KeyCatalogPolicy.cs'
t = io.open(p, encoding='utf-8').read()

# ── 1) 插入類別列舉與申報型別（放在 ToExemptFact 之前）────────────────
anchor = '    public static HardwareFact ToExemptFact(DateTimeOffset at,'
insert = '''    /// <summary>
    /// 豁免的正當性類別（P1-082）。
    ///
    /// <para>
    /// <b>這是有限的列舉，不是關鍵詞清單——這一點是刻意的。</b>
    /// 第一版用「理由文字含什麼詞」判斷豁免是否正當，結果 584 條真實豁免裡
    /// 有 507 條被判不合格；而當時的處置是<b>連續擴大詞表</b>，
    /// 那正是本計畫 P1-063 警告的事：用擴大詞表讓紅燈變綠，只是形式上看起來像修正。
    /// </para>
    ///
    /// <para>
    /// 關鍵詞比對必然走向兩條路之一：<b>詞表窄則大量誤報</b>
    /// （而誤報會導致守門被放寬，放寬過的守門比沒有更糟），<b>詞表寬則形同虛設</b>。
    /// 所以改成要求申報者<b>指名一個類別</b>——類別有限，判斷就不會漂移。
    /// </para>
    ///
    /// <para>
    /// <b>為什麼這樣能擋掉待辦：</b>「還沒寫對帳規則」選不出任何一個合法類別。
    /// 待辦不是「另一種豁免」，是<b>還沒做的事</b>。
    /// </para>
    /// </summary>
    public enum ExemptionKind
    {
        /// <summary>那項事實只有一個來源——沒有第二份資料可以交叉。</summary>
        SingleSource,
        /// <summary>那不是量測值（是政策宣告、契約、呈現、導覽、模型），沒有可對的對象。</summary>
        NotMeasurement,
        /// <summary>刻意不取得第二份來源（不出網、不讀內容、不列名稱、不開啟裝置）。</summary>
        SourceWithheld,
        /// <summary>來源本身就是這份申報——自己對自己交叉沒有意義（刻意無遞迴）。</summary>
        SelfReferential,
        /// <summary>本機沒有那個硬體或環境，無從量測。</summary>
        NotApplicable,
    }

    /// <summary>類別的中文名——申報時要印出來，讓讀者不必查列舉。</summary>
    public static string Describe(ExemptionKind kind) => kind switch
    {
        ExemptionKind.SingleSource => "單一來源",
        ExemptionKind.NotMeasurement => "非量測值",
        ExemptionKind.SourceWithheld => "刻意不取得",
        ExemptionKind.SelfReferential => "自我指涉",
        _ => "本機不適用",
    };

    /// <summary>
    /// 一條豁免的申報。<b>類別是機器判的，理由是給人看的</b>——兩者都要有。
    /// </summary>
    /// <param name="Kind">正當性類別（列舉，有限）。</param>
    /// <param name="Reason">為什麼是這個類別（給人讀，不得空白）。</param>
    public sealed record ExemptionEntry(ExemptionKind Kind, string Reason)
    {
        public bool IsComplete => !string.IsNullOrWhiteSpace(Reason) && Reason.Trim().Length >= 10;
    }

'''
assert anchor in t
t = t.replace(anchor, insert + anchor, 1)

# ── 2) 換掉 ToExemptFact 的簽章與主體 ────────────────────────────────
s = t.index('    public static HardwareFact ToExemptFact(DateTimeOffset at,')
e = t.index('    /// <summary>找出登記了卻沒有任何鍵符合的前綴')
new_fn = '''    public static HardwareFact ToExemptFact(DateTimeOffset at,
        IReadOnlyDictionary<string, ExemptionEntry> exemptions, int cap, int pendingCount = 0)
    {
        if (pendingCount > 0)
            return new HardwareFact(ExemptKey, Category, "豁免理由",
                $"{pendingCount} 條登記為豁免但其實是待辦", "條", Source,
                FactTrustLevel.Derived, false, at, exemptions.Count,
                FactAvailability.ReadError,
                "把待辦登記成豁免，就是讓申報比實際乾淨——待辦選不出任何一個合法的豁免類別");

        if (exemptions.Count == 0)
            return new HardwareFact(ExemptKey, Category, "豁免理由",
                "沒有任何豁免", "條", Source, FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "沒有豁免可以檢查");

        var incomplete = exemptions.Where(kv => !kv.Value.IsComplete).Select(kv => kv.Key).ToList();
        if (incomplete.Count > 0)
            return new HardwareFact(ExemptKey, Category, "豁免理由",
                $"{incomplete.Count} 條豁免沒有寫出理由：" + string.Join("、", incomplete.Take(5)),
                "條", Source, FactTrustLevel.Derived, false, at,
                exemptions.Count - incomplete.Count, FactAvailability.ReadError,
                "類別要機器判，理由要給人看——缺理由的豁免與沒有豁免無異");

        var byKind = exemptions.GroupBy(kv => kv.Value.Kind)
            .ToDictionary(g => g.Key, g => g.Count());
        string detail = $"{exemptions.Count} 條豁免（"
            + string.Join("、", byKind.OrderBy(kv => kv.Key)
                .Select(kv => $"{Describe(kv.Key)} {kv.Value}")) + "）";

        if (exemptions.Count > cap)
            return new HardwareFact(ExemptKey, Category, "豁免理由", detail, "條", Source,
                FactTrustLevel.Derived, false, at, exemptions.Count, FactAvailability.Unknown,
                $"已達上限 {cap}——先檢視這些豁免是不是該處理了，而不是提高上限");

        return new HardwareFact(ExemptKey, Category, "豁免理由", detail, "條", Source,
            FactTrustLevel.Derived, false, at, exemptions.Count, FactAvailability.Present, null);
    }

'''
t = t[:s] + new_fn + t[e:]

# ── 3) 刪掉不再使用的 IsPending / IsNoCrossCheck / Trim ──────────────
for fname in ('IsPending', 'IsNoCrossCheck'):
    i = t.find(f'    private static bool {fname}')
    if i < 0:
        print(f'!! 找不到 {fname}')
        continue
    # 往前吃掉它的註解區塊
    j = i
    while True:
        k = t.rfind('    ///', 0, j)
        if k < 0: break
        # 註解必須緊接在 j 之前（中間只有空行）
        between = t[k:j]
        if between.strip().startswith('///') and set(between.replace('\r','').replace('\n','').strip()) <= set('')\
           or all(l.strip().startswith('///') or not l.strip() for l in between.split('\n')):
            j = k
        else:
            break
    # 找到函式結尾
    end = t.index('\n    }\n', i) + len('\n    }\n')
    t = t[:j] + t[end:]

i = t.find('    private static string Trim(string s)')
if i >= 0:
    end = t.index('\n', t.index('=>', i))
    t = t[:i] + t[end + 1:]

io.open(p, 'w', encoding='utf-8', newline='').write(t)
print('已改為類別列舉判斷')
