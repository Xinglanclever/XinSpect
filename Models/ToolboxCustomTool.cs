using System.IO;

namespace XinSpect;

/// <summary>A user-owned entry in the toolbox catalog. Built-in entries are never serialized here.</summary>
public sealed record ToolboxCustomTool
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Group { get; init; } = "自訂工具";
    public ToolKind Kind { get; init; }
    public string Target { get; init; } = "";
    public string[] Arguments { get; init; } = [];
}

public sealed record ToolboxAddResult(ToolItem Item, bool Added, bool Merged);

/// <summary>Canonical identities used to merge duplicate executable and web entries.</summary>
public static class ToolboxIdentity
{
    public static string ForExecutable(string path, IEnumerable<string>? arguments = null)
    {
        string normalizedPath = NormalizePath(path).ToUpperInvariant();
        string argumentKey = string.Join("\0", arguments ?? []);
        return "exe:" + normalizedPath + "\0" + argumentKey;
    }

    public static string ForUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("網址只接受完整的 HTTP 或 HTTPS 位址。", nameof(value));

        var builder = new UriBuilder(uri) { Fragment = "" };
        if (builder.Path.Length > 1) builder.Path = builder.Path.TrimEnd('/');
        var normalized = builder.Uri;
        string path = normalized.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        string query = normalized.GetComponents(UriComponents.Query, UriFormat.UriEscaped);
        return "url:" + normalized.GetLeftPart(UriPartial.Authority) + "/" + path
             + (query.Length == 0 ? "" : "?" + query);
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("程式路徑不可空白。", nameof(path));
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
