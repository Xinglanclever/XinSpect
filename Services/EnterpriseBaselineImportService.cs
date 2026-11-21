using System.IO;
using System.Text;

namespace XinSpect;

/// <summary>
/// Evidence-page import of a signed policy, separately deployed trust key and saved snapshot.
/// The package service owns cryptography, validity periods and all comparison semantics.
/// This view projection exposes private aggregate counts only and never recollects hardware.
/// </summary>
public sealed class EnterpriseBaselineImportService : ObservableObject
{
    private readonly Func<string, string, string, EnterpriseBaselineEvaluation> _evaluate;
    private int _running;
    private string _status = "尚未匯入。請備妥組織預先部署公鑰、簽章政策與已存快照。";
    private string _summary = "尚無評估結果。";
    private string _policySignature = "政策簽章尚未驗證。";
    private string _schema = "—";
    private string _signerFingerprint = "—";

    public EnterpriseBaselineImportService() : this(EvaluateFiles) { }

    internal EnterpriseBaselineImportService(
        Func<string, string, string, EnterpriseBaselineEvaluation> evaluate)
        => _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));

    public bool CanRun => Volatile.Read(ref _running) == 0;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string PolicySignature { get => _policySignature; private set => SetProperty(ref _policySignature, value); }
    public string Schema { get => _schema; private set => SetProperty(ref _schema, value); }
    public string SignerFingerprint { get => _signerFingerprint; private set => SetProperty(ref _signerFingerprint, value); }

    public void ResetSelection()
    {
        if (!CanRun) return;
        Summary = "尚無本次結果；取消選檔不會沿用前次結果。";
        PolicySignature = "政策簽章尚未驗證。";
        Schema = "—";
        SignerFingerprint = "—";
        Status = "請依序選擇組織預先部署公鑰、簽章政策與已存快照。";
    }

    public async Task EvaluateAsync(string packagePath, string trustedPublicKeyPath, string snapshotPath)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        OnPropertyChanged(nameof(CanRun));
        Summary = "尚無本次結果；驗證完成後才顯示計數。";
        PolicySignature = "本次政策簽章尚未驗證。";
        Schema = "—";
        SignerFingerprint = "—";
        Status = "正在驗證政策與所選快照；全程只讀，不重新採集硬體。";
        try
        {
            var evaluation = await Task.Run(() => _evaluate(packagePath, trustedPublicKeyPath, snapshotPath));
            if (!evaluation.SignatureValid)
            {
                PolicySignature = "政策簽章或格式未通過驗證。";
                Summary = "拒絕評估；沒有符合結果。";
                Status = evaluation.FailureReason ?? "政策無法驗證。";
                return;
            }

            PolicySignature = "政策簽章已由所選公鑰驗證；該公鑰的組織信任來源須另行核對。";
            SignerFingerprint = evaluation.SignerKeyId;
            Schema = $"schema {evaluation.SchemaVersion}；{evaluation.Semantics}";
            if (evaluation.FailureReason is not null)
            {
                Summary = evaluation.Expired ? "政策已過期；沒有執行條件比對。" : "政策尚未生效；沒有執行條件比對。";
                Status = evaluation.FailureReason;
                return;
            }

            Summary = $"所選快照：符合 {evaluation.Satisfied} 項、不符合 {evaluation.NotSatisfied} 項、無法驗證 {evaluation.Unverifiable} 項。";
            Status = "本次結果僅對所選已存快照有效；不是目前硬體狀態或整體安全判決。";
        }
        catch (Exception ex)
        {
            // Exception messages may include selected paths, PEM details or snapshot values.
            Summary = "本次匯入失敗；沒有符合結果。";
            PolicySignature = "未建立可供評估的政策與快照。";
            Schema = "—";
            SignerFingerprint = "—";
            Status = "所選檔案讀取或格式驗證失敗（" + ex.GetType().Name + "）；未保留前次結果。";
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            OnPropertyChanged(nameof(CanRun));
        }
    }

    internal static EnterpriseBaselineEvaluation EvaluateFiles(
        string packagePath, string trustedPublicKeyPath, string snapshotPath)
        => EvaluateInputs(
            () => ReadBoundedUtf8(packagePath, EnterpriseBaselinePackageService.MaxPackageBytes),
            () => ReadBoundedUtf8(trustedPublicKeyPath, EnterpriseBaselinePackageService.MaxKeyPemBytes),
            () => HardwareSnapshotService.Load(snapshotPath), () => DateTimeOffset.UtcNow);

    // File access is injected here for real business regressions: rejected/future/expired policy
    // MUST return before touching the snapshot. Tests do not need to initialize a WPF view or probe.
    internal static EnterpriseBaselineEvaluation EvaluateInputs(
        Func<string> readPackage, Func<string> readTrustedKey, Func<HardwareSnapshot> loadSnapshot,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(readPackage);
        ArgumentNullException.ThrowIfNull(readTrustedKey);
        ArgumentNullException.ThrowIfNull(loadSnapshot);
        ArgumentNullException.ThrowIfNull(utcNow);
        string package = readPackage();
        string trustedKey = readTrustedKey();
        var verified = EnterpriseBaselinePackageService.Evaluate(package, trustedKey,
            new Dictionary<string, HardwareFact>(StringComparer.Ordinal), utcNow());
        if (!verified.SignatureValid || verified.FailureReason is not null) return verified;

        var snapshot = loadSnapshot();
        // Snapshot loading validates its hash, enums and duplicate keys. Preserve an unknown
        // nullable Unit: null is not an explicit unitless empty string for schema 2.
        var facts = snapshot.Facts.ToDictionary(fact => fact.Key, fact => new HardwareFact(
            fact.Key, fact.Category, fact.Name, fact.Value, fact.Unit!, fact.Source, fact.Trust,
            fact.Sensitive, fact.MeasuredAtUtc, fact.NumericValue, fact.Availability, fact.UnavailableReason),
            StringComparer.Ordinal);
        return EnterpriseBaselinePackageService.Evaluate(package, trustedKey, facts, utcNow());
    }

    private static string ReadBoundedUtf8(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) throw new InvalidDataException("輸入超過安全大小上限。");
        byte[] buffer = new byte[maximumBytes + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0) break;
            length += read;
        }
        if (length > maximumBytes) throw new InvalidDataException("輸入超過安全大小上限。");
        string text = new UTF8Encoding(false, true).GetString(buffer, 0, length);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
