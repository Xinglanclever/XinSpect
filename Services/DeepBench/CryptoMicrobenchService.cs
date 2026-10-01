using System.Diagnostics;
using System.Security.Cryptography;

namespace XinSpect;

/// <summary>
/// CPU 加解密與雜湊吞吐深測。AES 先做來回解密驗證，之後才計時；
/// 結果只陳述 .NET crypto API 在本機達成的吞吐，不標註特定硬體指令，也不宣稱認證。
/// </summary>
public sealed class CryptoMicrobenchService : IDeepBenchTest
{
    private const string TestId = "cpu.aes-sha";
    private const double BytesPerMib = 1024 * 1024;

    public string Id => TestId;

    public async Task<DeepBenchTestResult> RunAsync(DeepBenchRunContext context, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CryptoWorkload workload = GetWorkload(context.Profile);
            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.05, "AES 來回驗證"));
            (List<double> Aes, int AesInvalid, List<double> Sha, int ShaInvalid) measured = await Task.Run(
                () => MeasureAll(workload, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (measured.Aes.Count == 0 || measured.Sha.Count == 0)
            {
                throw new InvalidOperationException("加解密或雜湊沒有任何有限吞吐樣本；不補值。");
            }

            var conditions = new List<string>
            {
                "AES 先完成加密→解密→逐位元組比對，才開始計時。",
                "暖機輪不計入吞吐；每輪保留原始 MiB/s 樣本。",
            };
            if (measured.AesInvalid + measured.ShaInvalid > 0)
            {
                conditions.Add($"已排除非有限樣本 {measured.AesInvalid + measured.ShaInvalid} 筆；不將其改為零。");
            }

            context.Progress.Report(new DeepBenchProgress(TestId, 0, 1, 0.95, "整理原始樣本"));
            return new DeepBenchTestResult(
                TestId,
                context.SessionId,
                context.Profile,
                started,
                DateTime.UtcNow,
                $"AES-256-CBC / PKCS7：{workload.AesRounds} × {workload.AesBytesPerRound / BytesPerMib:0} MiB；SHA-256：{workload.ShaRounds} × {workload.ShaBytesPerRound / BytesPerMib:0} MiB",
                [
                    CreateMetric("cpu.aes.cbc.throughput", "AES-256-CBC throughput", measured.Aes, $"{workload.AesRounds} × {workload.AesBytesPerRound / BytesPerMib:0} MiB; round-trip verified"),
                    CreateMetric("cpu.sha256.throughput", "SHA-256 throughput", measured.Sha, $"{workload.ShaRounds} × {workload.ShaBytesPerRound / BytesPerMib:0} MiB"),
                ],
                conditions,
                [
                    ".NET crypto API measurement；硬體實作由 .NET/OS 選擇，本結果不做特定指令集或認證宣稱。",
                    "吞吐包含演算法與 API 呼叫成本；不是純指令延遲。",
                ],
                DeepBenchFailureKind.None,
                null);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(context, started);
        }
        catch (Exception exception)
        {
            return Failed(context, started, DeepBenchFailureKind.PlatformError, exception.Message);
        }
    }

    internal static CryptoWorkload GetWorkload(DeepBenchRunProfile profile) => profile switch
    {
        DeepBenchRunProfile.Quick => new CryptoWorkload(2, 2 * 1024 * 1024, 2, 4 * 1024 * 1024),
        DeepBenchRunProfile.Full => new CryptoWorkload(5, 16 * 1024 * 1024, 5, 32 * 1024 * 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    internal static bool VerifyRoundTrip(byte[] plaintext, byte[] key, byte[] iv)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(key.Length, 32);
        ArgumentOutOfRangeException.ThrowIfNotEqual(iv.Length, 16);
        if (plaintext.Length == 0) return false;

        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        byte[] cipher = new byte[plaintext.Length + 16];
        byte[] roundTrip = new byte[plaintext.Length + 16];
        using (ICryptoTransform encryptor = aes.CreateEncryptor())
        {
            int written = encryptor.TransformBlock(plaintext, 0, plaintext.Length, cipher, 0);
            byte[] final = encryptor.TransformFinalBlock(plaintext, plaintext.Length, 0);
            final.CopyTo(cipher, written);
            written += final.Length;
            using ICryptoTransform decryptor = aes.CreateDecryptor();
            int plainLength = decryptor.TransformBlock(cipher, 0, written, roundTrip, 0);
            byte[] finalPlain = decryptor.TransformFinalBlock(cipher, written, 0);
            finalPlain.CopyTo(roundTrip, plainLength);
            plainLength += finalPlain.Length;
            return plainLength == plaintext.Length && roundTrip.AsSpan(0, plainLength).SequenceEqual(plaintext);
        }
    }

    internal static (double[] Samples, int InvalidCount) FiniteSamples(IEnumerable<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double[] finite = samples.Where(double.IsFinite).ToArray();
        int invalid = samples.Count(value => double.IsFinite(value) == false);
        return (finite, invalid);
    }

    private static (List<double> Aes, int AesInvalid, List<double> Sha, int ShaInvalid) MeasureAll(CryptoWorkload workload, CancellationToken cancellationToken)
    {
        int maxBytes = Math.Max(workload.AesBytesPerRound, workload.ShaBytesPerRound);
        byte[] plain = new byte[maxBytes];
        byte[] key = new byte[32];
        byte[] iv = new byte[16];
        RandomNumberGenerator.Fill(key);
        RandomNumberGenerator.Fill(iv);
        RandomNumberGenerator.Fill(plain);

        if (!VerifyRoundTrip(plain, key, iv))
        {
            throw new InvalidOperationException("AES 來回驗證失敗；不進入計時。");
        }

        List<double> aes = MeasureAes(plain, key, iv, workload.AesBytesPerRound, workload.AesRounds, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        List<double> sha = MeasureSha(plain, workload.ShaBytesPerRound, workload.ShaRounds, cancellationToken);

        (double[] aesFinite, int aesInvalid) = FiniteSamples(aes);
        (double[] shaFinite, int shaInvalid) = FiniteSamples(sha);
        return ([.. aesFinite], aesInvalid, [.. shaFinite], shaInvalid);
    }

    private static List<double> MeasureAes(byte[] plain, byte[] key, byte[] iv, int bytesPerRound, int rounds, CancellationToken cancellationToken)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        byte[] cipher = new byte[bytesPerRound + 16];
        using ICryptoTransform encryptor = aes.CreateEncryptor();

        int WarmUp() => EncryptOnce(encryptor, plain, cipher, bytesPerRound);
        int warm = WarmUp();
        if (warm < bytesPerRound) throw new InvalidOperationException("AES 輸出長度異常。");

        var samples = new List<double>(rounds);
        for (int round = 0; round < rounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long timestamp = Stopwatch.GetTimestamp();
            int written = EncryptOnce(encryptor, plain, cipher, bytesPerRound);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(timestamp);
            if (written < bytesPerRound || elapsed.TotalSeconds <= 0)
            {
                throw new InvalidOperationException("AES 計時輪沒有完成有效輸出。");
            }
            samples.Add(bytesPerRound / BytesPerMib / elapsed.TotalSeconds);
        }
        return samples;
    }

    private static int EncryptOnce(ICryptoTransform encryptor, byte[] plain, byte[] cipher, int length)
    {
        int written = encryptor.TransformBlock(plain, 0, length, cipher, 0);
        byte[] final = encryptor.TransformFinalBlock(plain, length, 0);
        final.CopyTo(cipher, written);
        return written + final.Length;
    }

    private static List<double> MeasureSha(byte[] plain, int bytesPerRound, int rounds, CancellationToken cancellationToken)
    {
        _ = SHA256.HashData(plain.AsSpan(0, bytesPerRound));
        var samples = new List<double>(rounds);
        for (int round = 0; round < rounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long timestamp = Stopwatch.GetTimestamp();
            _ = SHA256.HashData(plain.AsSpan(0, bytesPerRound));
            TimeSpan elapsed = Stopwatch.GetElapsedTime(timestamp);
            if (elapsed.TotalSeconds <= 0) throw new InvalidOperationException("SHA-256 計時輪時間異常。");
            samples.Add(bytesPerRound / BytesPerMib / elapsed.TotalSeconds);
        }
        return samples;
    }

    private static DeepBenchMetric CreateMetric(string id, string title, IReadOnlyList<double> samples, string configuration) => new(
        id,
        title,
        "MiB/s",
        true,
        configuration,
        samples,
        samples.Select((value, index) => new DeepBenchMetricPoint(
            value,
            new Dictionary<string, string> { ["round"] = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
            [value])).ToArray());

    private static DeepBenchTestResult Cancelled(DeepBenchRunContext context, DateTime started) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "已取消", [], [], ["取消前未配置或計時任何工作負載。"], DeepBenchFailureKind.Cancelled, "使用者取消。");

    private static DeepBenchTestResult Failed(DeepBenchRunContext context, DateTime started, DeepBenchFailureKind kind, string error) =>
        new(TestId, context.SessionId, context.Profile, started, DateTime.UtcNow, "未完成", [], [], ["不推算缺失樣本。"], kind, error);

    internal readonly record struct CryptoWorkload(int AesRounds, int AesBytesPerRound, int ShaRounds, int ShaBytesPerRound);
}

