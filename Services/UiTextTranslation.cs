using System.Text;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>本機產品詞庫的格式模板翻譯；替換已收錄的句型，參數保持原值。</summary>
internal static class UiTextTranslation
{
    private static readonly Regex Placeholders = new(
        @"(?<!\{)\{(?<index>\d+)(?:,-?\d+)?(?::[^{}]*)?\}(?!\})",
        // 此模式只掃描內建詞庫模板，沒有使用者輸入；冷啟動／排程延遲不應讓 lazy 詞庫永久失敗。
        RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout);

    private sealed record Template(string Source, string English, Pattern Traditional, Pattern Simplified, Pattern EnglishPattern, bool EnglishHasLiteralAnchor);
    private sealed record Pattern(Regex Expression, string Prefix, string Suffix, int[] Indices);
    private static readonly Lazy<Template[]> Templates = new(() => EnglishStrings.Keys
        .Where(k => ContainsChinese(k) && Placeholders.IsMatch(k))
        .Select(k =>
        {
            string english = EnglishStrings.Lookup(k)!;
            return new Template(k, english, Build(k), Build(LanguageService.ToSimplified(k)),
                Build(english), HasLiteralAnchor(english));
        })
        .OrderByDescending(t => t.Source.Length - Placeholders.Matches(t.Source).Sum(m => m.Length))
        .ThenBy(t => Placeholders.Matches(t.Source).Count)
        .ThenByDescending(t => t.Source.Length).ToArray());

    internal static bool ContainsChinese(string text)
        => text.Any(c => c is >= '㐀' and <= '鿿' or >= '豈' and <= '﫿');

    internal static string ToEnglish(string text)
    {
        if (EnglishStrings.Lookup(text) is { } exact) return exact;
        if (!ContainsChinese(text)) return text;
        if (TranslateTemplate(text, english: true, simplified: false) is { } formatted) return formatted;
        // 簡體輸入也查同一份繁中原文表；只採 exact key，不在硬體／路徑中做子字串取代。
        if (EnglishStrings.Lookup(LanguageService.ToTraditional(text)) is { } canonical) return canonical;
        if (text.Contains(" ・ ", StringComparison.Ordinal))
            return string.Join(" ・ ", text.Split(" ・ ").Select(ToEnglish));
        if (text.Contains('\n')) return string.Join("\n", text.Split('\n').Select(ToEnglish));
        return text; // 未收錄如實保留，不用泛稱或隱藏文字冒充譯文。
    }

    internal static string? TranslateTemplate(string text, bool simplified)
        => TranslateTemplate(text, english: false, simplified);

    // ProductTextProjection must not classify arbitrary payload as catalogued prose just
    // because a placeholder-only English template (for example "{0}") matched it.
    internal static bool HasAnchoredTemplateMatch(string text)
    {
        if (text.Length > 16384 || !ContainsChinese(text)) return false;
        foreach (var template in Templates.Value)
        {
            if (!template.EnglishHasLiteralAnchor) continue;
            if (text.Contains('\n') && !template.Source.Contains('\n')) continue;
            if (MatchValues(template.Traditional, text) is not null || MatchValues(template.Simplified, text) is not null)
                return true;
        }
        return false;
    }

    // 唯讀報告可能夾帶路徑與原始紀錄；只處理產品已收錄的原文／模板，逐行保留其他 bytes。
    internal static string KnownDisplayText(string text, bool english, bool simplified)
    {
        if (EnglishStrings.Lookup(text) is { } exact)
            return LanguageService.Catify(text, english ? exact : simplified ? LanguageService.ToSimplified(text) : text);
        if (TranslateTemplate(text, english, simplified) is { } formatted) return formatted;
        if (!text.Contains('\n')) return text;
        return string.Join("\n", text.Split('\n').Select(line =>
        {
            string body = line.Trim();
            if (body.Length == 0) return line;
            string result = KnownDisplayText(body, english, simplified);
            return result == body ? line : line.Replace(body, result, StringComparison.Ordinal);
        }));
    }

    internal static string? OriginalFromEnglishTemplate(string text)
    {
        if (text.Length > 16384) return null;
        foreach (var template in Templates.Value)
        {
            // A translation such as "{0}" discarded every distinguishing word. Reversing it
            // would treat arbitrary input (for example a Chinese label) as the placeholder.
            if (!template.EnglishHasLiteralAnchor) continue;
            if (text.Contains('\n') && !template.English.Contains('\n')) continue;
            var values = MatchValues(template.EnglishPattern, text);
            if (values is not null) return Fill(template.Source, values);
        }
        return null;
    }

    private static string? TranslateTemplate(string text, bool english, bool simplified)
    {
        if (text.Length > 16384 || !ContainsChinese(text)) return null;
        if (Placeholders.IsMatch(text))
        {
            string source = EnglishStrings.Lookup(text) is not null ? text : LanguageService.ToTraditional(text);
            if (Placeholders.IsMatch(source) && EnglishStrings.Lookup(source) is { } exact
                && (source == text || LanguageService.ToSimplified(source) == text))
                return english ? exact : simplified ? LanguageService.ToSimplified(source) : source;
        }
        foreach (var template in Templates.Value)
        {
            if (text.Contains('\n') && !template.Source.Contains('\n')) continue;
            var values = MatchValues(template.Traditional, text) ?? MatchValues(template.Simplified, text);
            if (values is null) continue;
            string target = english ? template.English
                : simplified ? LanguageService.ToSimplified(template.Source) : template.Source;
            return Fill(target, values);
        }
        return null;
    }

    private static bool HasLiteralAnchor(string template)
    {
        int offset = 0;
        foreach (Match placeholder in Placeholders.Matches(template))
        {
            if (HasNonWhitespace(template.AsSpan(offset, placeholder.Index - offset))) return true;
            offset = placeholder.Index + placeholder.Length;
        }
        return HasNonWhitespace(template.AsSpan(offset));
    }

    private static bool HasNonWhitespace(ReadOnlySpan<char> text)
    {
        foreach (char character in text)
            if (!char.IsWhiteSpace(character)) return true;
        return false;
    }

    private static string Fill(string template, Dictionary<int, string> values)
        => Placeholders.Replace(template, m => values.TryGetValue(int.Parse(m.Groups["index"].Value), out var value)
            ? value : m.Value);

    private static Dictionary<int, string>? MatchValues(Pattern pattern, string text)
    {
        if (!text.StartsWith(pattern.Prefix, StringComparison.Ordinal)
            || !text.EndsWith(pattern.Suffix, StringComparison.Ordinal)) return null;
        Match? match = MatchSafely(pattern.Expression, text);
        if (match is null) return null;
        if (!match.Success) return null;
        var values = new Dictionary<int, string>();
        for (int i = 0; i < pattern.Indices.Length; i++)
        {
            string value = match.Groups[$"v{i}"].Value;
            if (values.TryGetValue(pattern.Indices[i], out var previous) && previous != value) return null;
            values[pattern.Indices[i]] = value;
        }
        return values;
    }

    internal static Match? MatchSafely(Regex expression, string text, Func<Regex, string, Match>? matcher = null)
    {
        try { return matcher is null ? expression.Match(text) : matcher(expression, text); }
        catch (RegexMatchTimeoutException) { return null; } // 顯示層保留原字串，不能讓格式比對中止 WPF Loaded。
    }

    private static Pattern Build(string template)
    {
        var expression = new StringBuilder(@"\A");
        var indices = new List<int>();
        int offset = 0;
        string prefix = template;
        foreach (Match match in Placeholders.Matches(template))
        {
            if (indices.Count == 0) prefix = template[..match.Index];
            expression.Append(Regex.Escape(template[offset..match.Index]));
            expression.Append($"(?<v{indices.Count}>.*?)");
            indices.Add(int.Parse(match.Groups["index"].Value));
            offset = match.Index + match.Length;
        }
        expression.Append(Regex.Escape(template[offset..])).Append(@"\z");
        return new Pattern(new Regex(expression.ToString(),
            RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(25)), prefix, template[offset..], indices.ToArray());
    }
}
