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
        // Length-prefix each argument: [] and [""] must not identify the same action.
        string argumentKey = string.Join("\0", (arguments ?? []).Select(a =>
            a.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + a));
        return "exe:" + normalizedPath + "\0" + argumentKey;
    }

    internal static string LegacyExecutableIdentity(string path, IEnumerable<string>? arguments = null)
        => "exe:" + NormalizePath(path).ToUpperInvariant() + "\0" + string.Join("\0", arguments ?? []);

    public static string ForUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("網址只接受完整的 HTTP 或 HTTPS 位址。", nameof(value));

        // A fragment can select a SPA route; /tools and /tools/ can be different resources.
        // Uri still canonicalizes the scheme, host and default port without removing those parts.
        return "url:" + uri.AbsoluteUri;
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("程式路徑不可空白。", nameof(path));
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
