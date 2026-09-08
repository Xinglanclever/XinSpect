using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 建立、保存、驗證及比較硬體時間膠囊。此服務只吃呼叫端組成的 fact collection，
/// 不直接碰 WMI、SMBIOS 或裝置控制碼，因此 UI 可自由整合既有服務，測試也不需要硬體。
/// </summary>
public static partial class HardwareSnapshotService
{
    public const string RedactedValue = "[已遮蔽]";
    public const string RedactedPrefix = "[已遮蔽]:";
    private const int MaxFileBytes = 16 * 1024 * 1024;
    private const int MaxFacts = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>UI 相容入口：使用 fact 中的敏感機器材料產生匿名 ID；沒有時使用內容指紋。</summary>
    public static HardwareSnapshot Create(string appVersion, IEnumerable<HardwareFact> facts, bool includeSensitive)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var source = facts.ToArray();
        var stableIdentity = source.Where(x => x.Key is "system.uuid" or "board.serial")
            .Select(x => x.Value).Where(IsUsableIdentity).Take(1).ToArray();
        var identity = stableIdentity.Length > 0 ? stableIdentity
            : source.Where(x => !x.Sensitive && x.Key is "board.model" or "system.model" or "cpu.name")
                .Select(x => x.Key + "=" + x.Value).ToArray();
        if (identity.Length == 0)
            identity = [string.Join("\0", source.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value))];
        var converted = source.Select(x => new HardwareSnapshotFact
        {
            Key = x.Key,
            Category = x.Category,
            Name = x.Name,
            Value = x.Value,
            NumericValue = x.NumericValue ?? TryNumeric(x.Value),
            Unit = x.Unit,
            Source = x.Source,
            Trust = x.Trust,
            Sensitive = x.Sensitive,
            MeasuredAtUtc = x.MeasuredAtUtc,
        });
        return Create(converted, identity, DateTimeOffset.UtcNow, appVersion,
            includeSensitive ? SensitiveValuePolicy.Preserve : SensitiveValuePolicy.Redact);
    }

    public static Task SaveAsync(string path, HardwareSnapshot snapshot, HardwareSnapshotSaveOptions? options = null)
        => Task.Run(() => Save(path, snapshot, options));

    public static Task<HardwareSnapshot> LoadAsync(string path)
        => Task.Run(() => Load(path));

    public static string AnonymousKey(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormKC))))[..24];

    public static string StableKey(string value)
    {
        string normalized = new(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        normalized = Regex.Replace(normalized, "-+", "-").Trim('-');
        if (normalized.Length is > 0 and <= 80) return normalized;
        return "id-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    }

    public static string TrustText(FactTrustLevel trust) => trust switch
    {
        FactTrustLevel.Measured => "直接量測",
        FactTrustLevel.Derived => "衍生計算",
        FactTrustLevel.Reported => "來源回報",
        _ => "未知",
    };

    /// <summary>
    /// 由原始機器材料產生不可逆匿名識別。材料可由 UUID、主機板或磁碟序號組成，
    /// 只保存加上用途前綴後的 SHA-256，不把任何原始材料放進 snapshot。
    /// </summary>
    public static string CreateAnonymousMachineId(IEnumerable<string?> machineIdentityParts)
    {
        ArgumentNullException.ThrowIfNull(machineIdentityParts);
        var parts = machineIdentityParts
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim().Normalize(NormalizationForm.FormKC))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (parts.Length == 0)
            throw new ArgumentException("至少需要一項非空白的機器識別材料。", nameof(machineIdentityParts));

        string payload = "XinSpect hardware snapshot machine id v1\0" + string.Join("\0", parts);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>建立尚未存檔的 snapshot；完整性摘要會依其內容立即計算。</summary>
    public static HardwareSnapshot Create(
        IEnumerable<HardwareSnapshotFact> facts,
        IEnumerable<string?> machineIdentityParts,
        DateTimeOffset? capturedAtUtc = null,
        string? appVersion = null,
        SensitiveValuePolicy sensitiveValues = SensitiveValuePolicy.Redact)
    {
        string machineId = CreateAnonymousMachineId(machineIdentityParts);
        return CreateWithAnonymousMachineId(facts, machineId, capturedAtUtc, appVersion, sensitiveValues);
    }

    /// <summary>已由安全元件取得匿名 ID 時使用；格式必須是本服務的 SHA-256 ID。</summary>
    public static HardwareSnapshot CreateWithAnonymousMachineId(
        IEnumerable<HardwareSnapshotFact> facts,
        string anonymousMachineId,
        DateTimeOffset? capturedAtUtc = null,
        string? appVersion = null,
        SensitiveValuePolicy sensitiveValues = SensitiveValuePolicy.Redact)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ValidateAnonymousMachineId(anonymousMachineId);
        ValidatePolicy(sensitiveValues);

        var normalized = NormalizeFacts(facts, sensitiveValues);
        var snapshot = new HardwareSnapshot
        {
            SchemaVersion = HardwareSnapshotSchema.CurrentVersion,
            AppVersion = RequireText(appVersion ?? AppInfo.Version, nameof(appVersion), 128),
            AnonymousMachineId = anonymousMachineId,
            CapturedAtUtc = NormalizeUtc(capturedAtUtc ?? DateTimeOffset.UtcNow),
            SensitiveValuesPreserved = sensitiveValues == SensitiveValuePolicy.Preserve,
            Facts = normalized,
            Integrity = new HardwareSnapshotIntegrity
            {
                Algorithm = HardwareSnapshotIntegrity.Sha256Algorithm,
                Hash = new string('0', 64),
            },
        };
        return WithIntegrity(snapshot);
    }

    /// <summary>保存為確定性 UTF-8 JSON；相同 snapshot 會產生逐位元組相同的內容。</summary>
    public static void Save(
        string path,
        HardwareSnapshot snapshot,
        HardwareSnapshotSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new HardwareSnapshotSaveOptions();
        ValidatePolicy(options.SensitiveValues);
        string fullPath = ValidatePath(path, forWrite: true);

        var safe = PrepareForSave(snapshot, options.SensitiveValues);
        byte[] json = Serialize(safe);
        if (json.Length > MaxFileBytes)
            throw new InvalidDataException($"時間膠囊超過 {MaxFileBytes / 1024 / 1024} MiB 上限。");

        string tempPath = fullPath + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            if (!options.Overwrite && File.Exists(fullPath))
                throw new IOException("目標時間膠囊已存在，且未允許覆寫。");
            File.Move(tempPath, fullPath, options.Overwrite);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>載入並驗證 schema、資料限制與 SHA-256 完整性；不接受尾隨或未知 JSON 欄位。</summary>
    public static HardwareSnapshot Load(string path)
    {
        string fullPath = ValidatePath(path, forWrite: false);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("找不到硬體時間膠囊。", fullPath);
        if (info.Length <= 0 || info.Length > MaxFileBytes)
            throw new InvalidDataException("硬體時間膠囊大小無效或超過安全上限。");

        byte[] json = File.ReadAllBytes(fullPath);
        HardwareSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<HardwareSnapshot>(json, JsonOptions)
                ?? throw new InvalidDataException("硬體時間膠囊內容是 null。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("硬體時間膠囊不是可接受的 JSON 格式。", ex);
        }

        ValidateSnapshot(snapshot);
        string expected = ComputeIntegrityHash(snapshot);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(snapshot.Integrity.Hash)))
            throw new InvalidDataException("硬體時間膠囊的 SHA-256 完整性驗證失敗；檔案可能已損壞或遭修改。這不是簽章驗證。");
        return Canonicalize(snapshot);
    }

    /// <summary>以穩定 key 比較兩份 snapshot，產出新增、移除、變更與不變。</summary>
    public static HardwareSnapshotDiff Diff(HardwareSnapshot before, HardwareSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ValidateSnapshot(before);
        ValidateSnapshot(after);

        var left = before.Facts.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var right = after.Facts.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var keys = left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        var entries = new List<HardwareFactChange>();

        foreach (string key in keys)
        {
            left.TryGetValue(key, out var oldFact);
            right.TryGetValue(key, out var newFact);
            var kind = oldFact is null ? SnapshotChangeKind.Added
                : newFact is null ? SnapshotChangeKind.Removed
                : Equivalent(oldFact, newFact) ? SnapshotChangeKind.Unchanged
                : SnapshotChangeKind.Changed;
            entries.Add(new HardwareFactChange
            {
                Key = key,
                Kind = kind,
                Previous = oldFact is null ? null : ToPublicFact(oldFact),
                Current = newFact is null ? null : ToPublicFact(newFact),
                NumericDelta = NumericDelta(oldFact, newFact),
            });
        }

        return new HardwareSnapshotDiff
        {
            BeforeMachineId = before.AnonymousMachineId,
            AfterMachineId = after.AnonymousMachineId,
            BeforeCapturedAtUtc = before.CapturedAtUtc,
            AfterCapturedAtUtc = after.CapturedAtUtc,
            Changes = entries,
        };
    }

    private static HardwareSnapshot PrepareForSave(HardwareSnapshot snapshot, SensitiveValuePolicy policy)
    {
        ValidateSnapshot(snapshot);
        var normalized = NormalizeFacts(snapshot.Facts, policy);
        return WithIntegrity(snapshot with
        {
            CapturedAtUtc = NormalizeUtc(snapshot.CapturedAtUtc),
            SensitiveValuesPreserved = policy == SensitiveValuePolicy.Preserve,
            Facts = normalized,
        });
    }

    private static HardwareSnapshot WithIntegrity(HardwareSnapshot snapshot)
    {
        string hash = ComputeIntegrityHash(snapshot);
        return snapshot with
        {
            Integrity = new HardwareSnapshotIntegrity
            {
                Algorithm = HardwareSnapshotIntegrity.Sha256Algorithm,
                Hash = hash,
            },
        };
    }

    private static string ComputeIntegrityHash(HardwareSnapshot snapshot)
    {
        var unsigned = Canonicalize(snapshot with
        {
            Integrity = new HardwareSnapshotIntegrity
            {
                Algorithm = HardwareSnapshotIntegrity.Sha256Algorithm,
                Hash = "",
            },
        });
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(unsigned, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }

    private static byte[] Serialize(HardwareSnapshot snapshot)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(Canonicalize(snapshot), JsonOptions);
        var result = new byte[body.Length + 1];
        Buffer.BlockCopy(body, 0, result, 0, body.Length);
        result[^1] = (byte)'\n';
        return result;
    }

    private static HardwareSnapshot Canonicalize(HardwareSnapshot snapshot) => snapshot with
    {
        Facts = snapshot.Facts.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(),
    };

    private static IReadOnlyList<HardwareSnapshotFact> NormalizeFacts(
        IEnumerable<HardwareSnapshotFact> facts,
        SensitiveValuePolicy policy)
    {
        var list = new List<HardwareSnapshotFact>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (fact is null) throw new ArgumentException("事實集合不可包含 null。", nameof(facts));
            ValidateFact(fact);
            if (!keys.Add(fact.Key)) throw new ArgumentException($"事實 key 重複：{fact.Key}", nameof(facts));
            list.Add(fact with
            {
                Value = fact.Sensitive && policy == SensitiveValuePolicy.Redact
                    ? IsRedacted(fact.Value) ? fact.Value : RedactedToken(fact.Value)
                    : fact.Value,
                NumericValue = fact.Sensitive && policy == SensitiveValuePolicy.Redact ? null : fact.NumericValue,
                MeasuredAtUtc = NormalizeUtc(fact.MeasuredAtUtc),
            });
            if (list.Count > MaxFacts) throw new ArgumentException($"事實筆數不可超過 {MaxFacts:N0}。", nameof(facts));
        }
        return list.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateSnapshot(HardwareSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != HardwareSnapshotSchema.CurrentVersion)
            throw new NotSupportedException($"不支援 schemaVersion {snapshot.SchemaVersion}；目前只支援 {HardwareSnapshotSchema.CurrentVersion}。");
        RequireText(snapshot.AppVersion, nameof(snapshot.AppVersion), 128);
        ValidateAnonymousMachineId(snapshot.AnonymousMachineId);
        if (snapshot.CapturedAtUtc == default) throw new InvalidDataException("capturedAtUtc 不可為預設值。");
        if (snapshot.Facts is null || snapshot.Facts.Count > MaxFacts) throw new InvalidDataException("facts 缺少或超過安全上限。");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in snapshot.Facts)
        {
            if (fact is null) throw new InvalidDataException("facts 不可包含 null。");
            ValidateFact(fact);
            if (!keys.Add(fact.Key)) throw new InvalidDataException($"事實 key 重複：{fact.Key}");
        }
        if (snapshot.Integrity is null
            || !string.Equals(snapshot.Integrity.Algorithm, HardwareSnapshotIntegrity.Sha256Algorithm, StringComparison.Ordinal)
            || !Sha256HexRegex().IsMatch(snapshot.Integrity.Hash ?? ""))
            throw new InvalidDataException("integrity 必須是 64 位小寫十六進位 SHA-256；它不是簽章。");
    }

    private static void ValidateFact(HardwareSnapshotFact fact)
    {
        if (!FactKeyRegex().IsMatch(fact.Key ?? ""))
            throw new ArgumentException("事實 key 必須為 1–160 字元的穩定 ASCII 路徑（小寫字母、數字、點、底線、連字號或方括號）。");
        RequireText(fact.Category, nameof(fact.Category), 128);
        RequireText(fact.Name, nameof(fact.Name), 256);
        RequireText(fact.Value, nameof(fact.Value), 16_384);
        RequireText(fact.Source, nameof(fact.Source), 512);
        if (fact.Unit is { } unit && unit.Length > 64) throw new ArgumentException("unit 過長。");
        if (fact.NumericValue is { } n && (double.IsNaN(n) || double.IsInfinity(n)))
            throw new ArgumentException("numericValue 必須是有限數值。");
        if (!Enum.IsDefined(fact.Trust)) throw new ArgumentException("trust 無效。");
        if (fact.MeasuredAtUtc == default) throw new ArgumentException("measuredAtUtc 不可為預設值。");
    }

    private static void ValidateAnonymousMachineId(string value)
    {
        if (!AnonymousIdRegex().IsMatch(value ?? ""))
            throw new ArgumentException("anonymousMachineId 必須是 sha256: 加 64 位小寫十六進位，不可放原始序號。", nameof(value));
    }

    private static string ValidatePath(string path, bool forWrite)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路徑不可空白。", nameof(path));
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOf('\0') >= 0)
            throw new ArgumentException("路徑包含無效字元。", nameof(path));
        string fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(fullPath)) throw new ArgumentException("必須使用完整路徑。", nameof(path));
        if (forWrite)
        {
            string? parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                throw new DirectoryNotFoundException("目標資料夾不存在；服務不會隱式建立路徑。");
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("基於路徑安全，拒絕直接寫入重新解析點資料夾。");
        }
        return fullPath;
    }

    private static HardwareFact ToPublicFact(HardwareSnapshotFact fact) => new(
        fact.Key, fact.Category, fact.Name, fact.Value, fact.Unit ?? "", fact.Source,
        fact.Trust, fact.Sensitive, fact.MeasuredAtUtc, fact.NumericValue);

    private static double? TryNumeric(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
           && double.IsFinite(parsed) ? parsed : null;

    private static double? NumericDelta(HardwareSnapshotFact? before, HardwareSnapshotFact? after)
    {
        if (before?.NumericValue is not { } oldValue || after?.NumericValue is not { } newValue) return null;
        if (!string.Equals(before.Unit ?? "", after.Unit ?? "", StringComparison.Ordinal)) return null;
        double delta = newValue - oldValue;
        return double.IsFinite(delta) ? delta : null;
    }

    private static bool Equivalent(HardwareSnapshotFact a, HardwareSnapshotFact b)
        => string.Equals(a.Category, b.Category, StringComparison.Ordinal)
        && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && string.Equals(a.Value, b.Value, StringComparison.Ordinal)
        && Nullable.Equals(a.NumericValue, b.NumericValue)
        && string.Equals(a.Unit ?? "", b.Unit ?? "", StringComparison.Ordinal)
        && string.Equals(a.Source, b.Source, StringComparison.Ordinal)
        && a.Trust == b.Trust
        && a.Sensitive == b.Sensitive;

    public static bool IsRedacted(string value)
        => value == RedactedValue || value.StartsWith(RedactedPrefix, StringComparison.Ordinal);

    private static string RedactedToken(string value)
        => RedactedPrefix + AnonymousKey("XinSpect sensitive fact v1\0" + value);

    private static bool IsUsableIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = value.Trim().Replace("-", "", StringComparison.Ordinal).Replace("0", "", StringComparison.Ordinal);
        if (normalized.Length == 0) return false;
        return value.Trim() is not ("—" or "Default string" or "To Be Filled By O.E.M." or "To Be Filled By OEM" or "System Serial Number");
    }

    private static string RequireText(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} 不可空白。", name);
        if (value.Length > maxLength) throw new ArgumentException($"{name} 超過 {maxLength} 字元。", name);
        return value;
    }

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value)
        => value.ToUniversalTime();

    private static void ValidatePolicy(SensitiveValuePolicy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
    }

    private static readonly Regex FactKeyPattern = new(
        @"^[a-z0-9](?:[a-z0-9._\-\[\]]{0,159})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AnonymousIdPattern = new(
        @"^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha256HexPattern = new(
        @"^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static Regex FactKeyRegex() => FactKeyPattern;
    private static Regex AnonymousIdRegex() => AnonymousIdPattern;
    private static Regex Sha256HexRegex() => Sha256HexPattern;
}
