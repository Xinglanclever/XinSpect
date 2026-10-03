using System.Diagnostics;
using System.IO;

namespace XinSpect;

/// <summary>一次假容量驗證的結果。</summary>
public sealed record FakeCapacityResult(
    long WrittenBytes, long VerifiedBytes, int MismatchedBytes, bool FullReached, long ElapsedMs, string TempFilePath);

/// <summary>
/// 假容量寫入驗證（H2testw 式，**危險項・同意閘門複用 WP22 模式**）：
/// 在目標磁碟寫入可重現樣本到指定上限（或寫滿），沖刷後讀回逐位元組驗證——
/// 「寫不進去／讀回不一致」是假容量卡（標 512GB 實為 8GB 的刷板卡）與劣化碟的直接證據。
/// <list type="bullet">
/// <item>**同意閘門**：userConsent 非 true 丟例外——寫入量可觀且耗時，可能加劇瀕死媒體的損耗。</item>
/// <item>**誠實標註**：本測試會對目標媒體做大量寫入——對瀕死碟有加劇損壞的風險，驗完立即刪檔。</item>
/// <item>樣本為 xorshift 派生（chunkIndex 混入種子），讀回逐位元組核對。</item>
/// </list>
/// 無任何自動接線——只有明確呼叫才執行。
/// </summary>
public static class FakeCapacityTestService
{
    public const string DangerNotice =
        "⚠ 危險操作：假容量驗證會對目標磁碟寫入大量資料（上限內寫滿為止），可能加劇瀕死媒體的損壞。" +
        "驗證完畢會刪除暫存檔；請確保目標磁碟有足夠可用空間且沒有需要保護的資料。";

    /// <summary>以 chunkIndex 混入種子填入可重現樣本。</summary>
    public static unsafe void FillChunk(Span<byte> chunk, int chunkIndex)
    {
        uint state = 0x9E3779B9u ^ (uint)(chunkIndex * 0x85EBCA6B);
        for (int i = 0; i < chunk.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            chunk[i] = (byte)(state >> 24);
        }
    }

    /// <summary>讀回驗證：回不符位元組數（0＝完好）。</summary>
    public static unsafe int VerifyChunk(ReadOnlySpan<byte> chunk, int chunkIndex)
    {
        uint state = 0x9E3779B9u ^ (uint)(chunkIndex * 0x85EBCA6B);
        int mismatches = 0;
        for (int i = 0; i < chunk.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            if (chunk[i] != (byte)(state >> 24)) mismatches++;
        }
        return mismatches;
    }

    /// <summary>執行假容量驗證。userConsent 必須明確 true。</summary>
    public static FakeCapacityResult RunConsented(bool userConsent, string driveRoot,
        long maxBytes = 1024L * 1024 * 1024, int chunkMiB = 16,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!userConsent)
            throw new InvalidOperationException("假容量驗證需要明確同意才會執行——會對目標磁碟大量寫入。" + DangerNotice);
        int chunkBytes = chunkMiB * 1024 * 1024;
        string tempPath = Path.Combine(driveRoot, $"XinSpect-假容量驗證-{Guid.NewGuid():N}.tmp");
        var sw = Stopwatch.StartNew();
        long written = 0;
        int chunkIndex = 0;
        try
        {
            var fillBuffer = new byte[chunkBytes];
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, chunkBytes, FileOptions.WriteThrough))
            {
                while (written < maxBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    int toWrite = (int)Math.Min(chunkBytes, maxBytes - written);
                    FillChunk(fillBuffer.AsSpan(0, toWrite), chunkIndex);
                    fs.Write(fillBuffer, 0, toWrite);
                    written += toWrite;
                    chunkIndex++;
                    progress?.Report((int)Math.Min(100, written * 50 / Math.Max(1, maxBytes)));
                }
                fs.Flush(flushToDisk: true);
            }

            bool fullReached = false;
            long verified = 0;
            int mismatches = 0;
            var readBuffer = new byte[chunkBytes];
            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, chunkBytes))
            {
                long available = fs.Length;
                chunkIndex = 0;
                while (verified < available)
                {
                    ct.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(chunkBytes, available - verified);
                    int read = fs.Read(readBuffer, 0, toRead);
                    if (read <= 0) { fullReached = true; break; }   // 讀不回＝容量灌不滿（假碟特徵）
                    mismatches += VerifyChunk(readBuffer.AsSpan(0, read), chunkIndex);
                    verified += read;
                    chunkIndex++;
                    progress?.Report(50 + (int)Math.Min(50, verified * 50 / Math.Max(1, maxBytes)));
                }
                if (available < written) fullReached = true;        // 寫得進去但讀不回全部
            }
            sw.Stop();
            return new FakeCapacityResult(written, verified, mismatches, fullReached, sw.ElapsedMilliseconds, tempPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 刪除失敗不遮蔽結果 */ }
        }
    }
}
