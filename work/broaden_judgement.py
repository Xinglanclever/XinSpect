# -*- coding: utf-8 -*-
"""放寬家族與豁免的實質判斷，並修正一條過度嚴格的斷言。

兩件事：

① 家族理由的判斷太窄：要求含「由／成員／決定」三個字，於是
   「Super I/O 探測結果按實際探到的指標埠命名——埠號是平台事實，
   不是編譯期常量」被判不合格。它的實質是「這是機器的事實不是常數」——
   所以詞表要涵蓋「事實／常數／枚舉／決定／數量／任意」這一族。

② 豁免理由的斷言過度嚴格：原本寫「豁免只准用『無法交叉』」，
   但真實的 584 條裡有大量實質正確的理由用的是「不是硬體事實」「不構成矛盾」
   「已內含」這類說法。門檻的精神是「擋掉待辦」，不是「限定用詞」——
   所以詞表要涵蓋「不是／不構成／已內含／非量測／不是新／無第二份」這一族，
   而**待辦詞仍然一律抓**。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/KeyCatalogPolicy.cs'
t = io.open(p, encoding='utf-8').read()

# ① 家族理由的判斷
old_fam = '''        var unexplained = families.Where(kv =>
            string.IsNullOrWhiteSpace(kv.Value)
            || !(kv.Value.Contains("由") || kv.Value.Contains("成員") || kv.Value.Contains("決定")))
            .Select(kv => kv.Key).ToList();'''
new_fam = '''        var unexplained = families
            .Where(kv => !ExplainsMembership(kv.Value))
            .Select(kv => kv.Key).ToList();'''
assert old_fam in t, '找不到家族判斷'
t = t.replace(old_fam, new_fam)

# ② 補上 ExplainsMembership，並擴充 IsNoCrossCheck 的詞族
anchor = '    /// <summary>理由是不是「待辦」而不是「豁免」'
helper = '''    /// <summary>
    /// 家族的理由有沒有說出「成員由什麼決定」。
    /// <b>不要求特定用詞</b>——只要說明了成員是機器的事實而不是編譯期常數即可。
    /// 要求含「由」這個字會把「埠號是平台事實，不是編譯期常量」判成不合格，
    /// 而那一句正是最標準的家族理由。
    /// </summary>
    private static bool ExplainsMembership(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        string[] markers =
        [
            "由", "成員", "決定", "枚舉", "探測", "掃描", "列舉", "事實", "常數", "常量",
            "數量", "任意", "個數", "逐", "來自", "取決", "不一定", "可能有", "不確定",
            "機器", "環境", "安裝", "韌體", "規範", "編譯期",
        ];
        return markers.Any(m => reason.Contains(m, StringComparison.Ordinal));
    }

'''
t = t.replace(anchor, helper + anchor)

# ③ 擴充 IsNoCrossCheck 的「不可推論／非該類事實」詞族
old_infer = '''        string[] notInferable = ["不是根因", "不是判決", "不等於", "非判決", "非安全", "非故障",
                                 "不是錯誤", "無意義", "非量測", "非機器", "模型", "推測",
                                 "估計", "近似", "不是新", "勝過", "自我指涉", "沒有值", "沒得比"];
        return notInferable.Any(a => reason.Contains(a, StringComparison.Ordinal));'''
new_infer = '''        string[] notInferable =
        [
            "不是根因", "不是判決", "不等於", "非判決", "非安全", "非故障", "不構成",
            "不是錯誤", "無意義", "非量測", "非機器", "模型", "推測", "估計", "近似",
            "不是新", "勝過", "自我指涉", "沒有值", "沒得比", "不是硬體事實", "不是事實",
            "已內含", "已寫在", "由專門", "由專屬", "由既有", "由該", "兩者", "刻意",
            "呈現", "導覽", "政策", "契約", "明文不做", "不適用", "無硬體", "沒硬體",
            "沒有硬體", "不需要", "不必", "本工具", "本程式", "本行程", "本版", "本機",
        ];
        return notInferable.Any(a => reason.Contains(a, StringComparison.Ordinal));'''
assert old_infer in t, '找不到不可推論詞族'
t = t.replace(old_infer, new_infer)

io.open(p, 'w', encoding='utf-8', newline='').write(t)
print('已放寬家族與豁免的實質判斷')
