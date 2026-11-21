using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>
/// Desktop 版百科下載器。百科不再跟著單檔執行檔散布；只有使用者從「關於」頁點開時，
/// 才從 XinSpect 最新 Release 下載 HTML，並依同一 Release 的 SHA256SUMS 對帳後快取。
/// CLI 的百科維持各自自包含，不走這條路徑。
/// </summary>
public static class EncyclopediaCatalog
{
    public const string FileName = "百科.html";
    public const string AssetFileName = "XinSpect-Encyclopedia.html";
    public const string ChecksumsFileName = "SHA256SUMS.txt";
    public const string MissingReason = "百科下載失敗，而且沒有可用的已驗證快取。";
    public const int MaxHtmlBytes = 8 * 1024 * 1024;
    private const int MaxChecksumBytes = 1024 * 1024;
    private const string CacheMetadataName = "百科.html.sha256";
    private const string ReleaseBaseUrl = "https://github.com/Xinglanclever/XinSpect/releases/latest/download/";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim LoadGate = new(1, 1);

    public sealed record EncyclopediaReport(
        bool Available, string? UnavailableReason, string? Text, string Source, bool UsedOfflineCache = false);

    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XinSpect", "docs");

    public static async Task<EncyclopediaReport> LoadAsync(CancellationToken cancellationToken = default)
    {
        await LoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? downloadError = null;
            try
            {
                byte[] sumsBytes = await DownloadAsync(ChecksumsFileName, MaxChecksumBytes, cancellationToken)
                    .ConfigureAwait(false);
                string expected = ParseExpectedHash(DecodeUtf8(sumsBytes, ChecksumsFileName));
                byte[] htmlBytes = await DownloadAsync(AssetFileName, MaxHtmlBytes, cancellationToken)
                    .ConfigureAwait(false);
                string text = ValidateHtml(htmlBytes, expected);
                await WriteCacheAsync(htmlBytes, expected, cancellationToken).ConfigureAwait(false);
                return new EncyclopediaReport(true, null, text, "release:" + AssetFileName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                downloadError = ex.GetType().Name + "：" + ex.Message;
            }

            var cached = await ReadVerifiedCacheAsync(cancellationToken).ConfigureAwait(false);
            if (cached is not null)
                return new EncyclopediaReport(true, null, cached, "cache:" + Path.Combine(CacheDirectory, FileName),
                    UsedOfflineCache: true);

            return new EncyclopediaReport(false, "下載百科失敗（" + downloadError + "）。" + MissingReason,
                null, "none");
        }
        finally
        {
            LoadGate.Release();
        }
    }

    /// <summary>從 SHA256SUMS 只取唯一且檔名完全相符的百科項目。</summary>
    public static string ParseExpectedHash(string checksums)
    {
        if (string.IsNullOrWhiteSpace(checksums))
            throw new InvalidDataException("SHA256SUMS.txt 是空的。");

        string? found = null;
        foreach (string rawLine in checksums.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2) continue;
            string name = fields[1].StartsWith("*", StringComparison.Ordinal) ? fields[1][1..] : fields[1];
            if (!string.Equals(name, AssetFileName, StringComparison.Ordinal)) continue;
            if (found is not null)
                throw new InvalidDataException("SHA256SUMS.txt 對百科有重複項目。");
            if (fields[0].Length != 64 || !IsHex(fields[0]))
                throw new InvalidDataException("SHA256SUMS.txt 的百科 SHA-256 格式不正確。");
            found = fields[0].ToLowerInvariant();
        }

        return found ?? throw new InvalidDataException("SHA256SUMS.txt 沒有百科項目。");
    }

    /// <summary>檢查尺寸、SHA-256、UTF-8 與預期的靜態百科結構。</summary>
    public static string ValidateHtml(byte[] bytes, string expectedHash)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > MaxHtmlBytes)
            throw new InvalidDataException("百科 HTML 空白或超過大小上限。");
        if (expectedHash.Length != 64 || !IsHex(expectedHash))
            throw new InvalidDataException("百科的預期 SHA-256 格式不正確。");

        byte[] actual = SHA256.HashData(bytes);
        byte[] expected = Convert.FromHexString(expectedHash);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("百科 SHA-256 與 Release 清單不符。");

        string html = DecodeUtf8(bytes, AssetFileName);
        if (!HasExpectedDocumentShape(html))
            throw new InvalidDataException("下載內容不是可辨認的 XinSpect 百科 HTML。");
        return html;
    }

    public static bool HasExpectedDocumentShape(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;
        return html.Contains("<html", StringComparison.OrdinalIgnoreCase)
               && html.Contains("<head", StringComparison.OrdinalIgnoreCase)
               && html.Contains("id=\"toc\"", StringComparison.Ordinal)
               && html.Contains("</html>", StringComparison.OrdinalIgnoreCase)
               && !html.Contains("<script", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> DownloadAsync(string name, int maxBytes, CancellationToken cancellationToken)
    {
        Uri uri = new(ReleaseBaseUrl + Uri.EscapeDataString(name));
        using HttpResponseMessage response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps || !IsGitHubDownloadHost(finalUri.Host))
            throw new InvalidDataException("百科下載被導向非 GitHub HTTPS 網址。");
        if (response.Content.Headers.ContentLength is long length && (length < 1 || length > maxBytes))
            throw new InvalidDataException("下載內容超過允許大小。");

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[32 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maxBytes)
                throw new InvalidDataException("下載內容超過允許大小。");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0)
            throw new InvalidDataException("伺服器回傳空檔案。");
        return output.ToArray();
    }

    private static async Task WriteCacheAsync(byte[] html, string expectedHash, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(CacheDirectory);
        string path = Path.Combine(CacheDirectory, FileName);
        string metadataPath = Path.Combine(CacheDirectory, CacheMetadataName);
        string suffix = "." + Guid.NewGuid().ToString("N") + ".tmp";
        string htmlTemp = path + suffix;
        string metadataTemp = metadataPath + suffix;
        try
        {
            await File.WriteAllBytesAsync(htmlTemp, html, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(metadataTemp, expectedHash + Environment.NewLine,
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(htmlTemp, path, overwrite: true);
            File.Move(metadataTemp, metadataPath, overwrite: true);
        }
        finally
        {
            TryDelete(htmlTemp);
            TryDelete(metadataTemp);
        }
    }

    private static async Task<string?> ReadVerifiedCacheAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(CacheDirectory, FileName);
        string metadataPath = Path.Combine(CacheDirectory, CacheMetadataName);
        try
        {
            if (!File.Exists(path) || !File.Exists(metadataPath)) return null;
            string expected = (await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false)).Trim();
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return ValidateHtml(bytes, expected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string DecodeUtf8(byte[] bytes, string description)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException(description + " 不是有效的 UTF-8。", ex); }
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static bool IsGitHubDownloadHost(string host)
        => host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 暫存檔清理失敗不覆蓋原始下載結果 */ }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XinSpect/" + AppInfo.Version);
        return client;
    }
}
