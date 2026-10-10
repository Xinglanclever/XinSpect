using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XinSpect;

/// <summary>SBOM 的一個元件（CycloneDX 的最小欄位集）。</summary>
public sealed record SbomComponent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("supplier")] string? Supplier);

/// <summary>CycloneDX 的 metadata 區塊。</summary>
public sealed record SbomMetadata(
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("component")] SbomComponent Component);

/// <summary>輸出的 CycloneDX 文件（僅本專案需要的欄位；不追求完整規格覆蓋）。</summary>
public sealed record SbomDocument(
    [property: JsonPropertyName("bomFormat")] string BomFormat,
    [property: JsonPropertyName("specVersion")] string SpecVersion,
    [property: JsonPropertyName("serialNumber")] string SerialNumber,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("metadata")] SbomMetadata Metadata,
    [property: JsonPropertyName("components")] IReadOnlyList<SbomComponent> Components);

/// <summary>
/// 軟體物料清單（Vol 2 批次 D／RS-002）：把「這台機器上跑著什麼」輸出成標準格式。
/// <para>
/// <b>做什麼：</b>產生 CycloneDX 1.5 的 JSON（<c>bomFormat</c>／<c>specVersion</c>／
/// <c>metadata</c>／<c>components</c>），序號由內容雜湊推導——同一份輸入永遠得到同一份文件
/// （可重現，才能拿去比對）。
/// </para>
/// <para>
/// <b>界線：</b>①元件來源是<b>本機可列舉的驅動＋作業系統＋本程式</b>，<b>不是</b>已安裝應用程式套件
/// （那需要另一條盤點通路）；②不含授權資訊與 CPE；③本版只產生內容與摘要，
/// <b>不主動寫檔</b>——匯出到檔案是報告層的動作，要經過使用者選擇路徑（寫入一律明示）。
/// </para>
/// </summary>
public static class SbomService
{
    public const string Category = "報告輸出";
    public const string SbomKey = "rs.sbom";
    public const string SpecVersion = "1.5";
    public const string BomFormat = "CycloneDX";

    private const string Source = "本機驅動清單（WMI Win32_PnPSignedDriver）＋作業系統＋本程式";

    /// <summary>把元件排成確定順序：型別、名稱、版本——同一組輸入必得同一份文件。</summary>
    public static IReadOnlyList<SbomComponent> Normalize(IReadOnlyList<SbomComponent> components) =>
        components
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => (c.Type, c.Name, c.Version))
            .Select(g => g.First())
            .OrderBy(c => c.Type, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>產生 CycloneDX 文件（serialNumber 由內容雜湊推導，可重現）。</summary>
    public static SbomDocument Build(IReadOnlyList<SbomComponent> components, string appName, string appVersion,
        DateTimeOffset at)
    {
        var list = Normalize(components);
        var app = new SbomComponent("application", appName, appVersion, null);
        string fingerprint = string.Join("|", list.Select(c => $"{c.Type}:{c.Name}:{c.Version}"))
                             + "|" + appName + ":" + appVersion;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
        string serial = "urn:uuid:" + new Guid(hash.AsSpan(0, 16)).ToString();

        return new SbomDocument(BomFormat, SpecVersion, serial, 1,
            new SbomMetadata(at, app), list);
    }

    /// <summary>產生 JSON 文字（縮排、UTF-8 友善：非 ASCII 不轉義）。</summary>
    public static string ToJson(SbomDocument document) => JsonSerializer.Serialize(document, JsonOptions);

    /// <summary>文件內容的 SHA-256（小寫十六進位）——摘要事實帶著它，識別「同一份 SBOM」。</summary>
    public static string ContentHash(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    /// <summary>收集 SBOM 事實（摘要一條）。傳入 <paramref name="probe"/> 供測試替換元件來源。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, string appName, string appVersion,
        Func<(IReadOnlyList<SbomComponent> Components, string? Note)>? probe = null)
    {
        (IReadOnlyList<SbomComponent> Components, string? Note) source;
        try { source = (probe ?? RealComponents)(); }
        catch (Exception ex)
        {
            return
            [
                new HardwareFact(SbomKey, Category, "軟體物料清單（SBOM）", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    $"元件來源讀取失敗（{ex.GetType().Name}）——讀不到就是不猜，不給一份空 SBOM"),
            ];
        }

        var doc = Build(source.Components, appName, appVersion, at);
        string json = ToJson(doc);
        var types = doc.Components.GroupBy(c => c.Type).Select(g => $"{g.Key} {g.Count()}");
        string note = source.Note is null ? "" : $"（{source.Note}）";

        return
        [
            new HardwareFact(SbomKey, Category, "軟體物料清單（SBOM）",
                $"格式 {BomFormat} {SpecVersion}・元件 {doc.Components.Count} 個（{string.Join("、", types)}）・" +
                $"序號 {doc.SerialNumber}・內容 SHA-256 {ContentHash(json)[..16]}…・" +
                "尚未寫檔（本層只產生內容；匯出檔案由報告層選路徑）。" +
                "元件來源限於本機可列舉的驅動＋作業系統＋本程式，不含已安裝應用程式套件與授權資訊。" + note,
                "個", Source, FactTrustLevel.Derived, false, at, doc.Components.Count),
        ];
    }

    /// <summary>真實元件來源：作業系統、本程式之外，逐一列出驅動（名稱＋版本）。</summary>
    public static (IReadOnlyList<SbomComponent> Components, string? Note) RealComponents()
    {
        var components = new List<SbomComponent>
        {
            new("operating-system", OsDescription(), Environment.OSVersion.Version.ToString(), "Microsoft"),
        };
        string? note = null;
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT DeviceName, DriverVersion, Manufacturer FROM Win32_PnPSignedDriver");
            foreach (ManagementObject o in searcher.Get())
                using (o)
                {
                    string name = o["DeviceName"] as string ?? "";
                    if (name.Length == 0) continue;
                    components.Add(new SbomComponent("device-driver", name,
                        o["DriverVersion"] as string ?? "未提供",
                        o["Manufacturer"] as string));
                }
        }
        catch (Exception ex)
        {
            note = $"驅動清單讀不到（{ex.GetType().Name}）——SBOM 只含作業系統";
        }
        return (components, note);
    }

    private static string OsDescription() =>
        Environment.OSVersion.VersionString is { Length: > 0 } s ? s : "Windows";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
