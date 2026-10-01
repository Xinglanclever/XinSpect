namespace XinSpect;

public static class DeepBenchCrossDomainSynthesis
{
    public static IReadOnlyList<DeepBenchInsight> Summarize(DeepBenchRunRecord record) => Summarize(record.SessionId, record.Results);

    public static IReadOnlyList<DeepBenchInsight> Summarize(Guid sessionId, IReadOnlyList<DeepBenchTestResult> results)
    {
        List<DeepBenchInsight> insights = [];
        var sameSession = results.Where(result => result.SessionId == sessionId && result.FailureKind == DeepBenchFailureKind.None && result.Metrics.Count > 0).ToArray();

        if (sameSession.Length == 0)
        {
            return [new("沒有可聚合實測", "本場沒有成功測項，不生成總分或排名。", [])];
        }

        var domains = sameSession.Select(result => result.TestId.Split('.')[0]).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        string[] required = ["cpu", "topology", "memory", "gpu", "storage"];
        string[] missing = required.Where(domain => !domains.Contains(domain)).ToArray();
        if (missing.Length > 0)
        {
            insights.Add(new(
                "缺少量測域",
                $"缺少：{string.Join("、", missing.Select(NormalizeDomain))}。跨域歸因需要同場證據；缺域不補猜。",
                []));
        }

        if (domains.Contains("cpu") && domains.Contains("memory"))
        {
            insights.Add(new(
                "CPU 與記憶體對照",
                "CPU 微基準與記憶體頻寬/延遲來自同一 run session，可對照運算吞吐與資料供給邊界；不合成單一總分。",
                sameSession.Where(result => result.TestId.StartsWith("cpu.", StringComparison.Ordinal) || result.TestId.StartsWith("memory.", StringComparison.Ordinal)).Select(result => result.TestId).ToArray()));
        }

        DeepBenchTestResult? storage = sameSession.FirstOrDefault(result => result.TestId == "storage.qd-ladder");
        if (storage is not null)
        {
            // 比例只在「同一指標、同一區塊大小」內計算：跨 IOPS/MiB/s/µs 或跨 4K/128K 的除法是無意義數字。
            DeepBenchInsight? scalingInsight = BuildStorageScalingInsight(storage);
            if (scalingInsight is not null) insights.Add(scalingInsight);
        }

        return insights;
    }

    /// <summary>QD1 → 最佳 QD 的比例；只用同一 metric id 且同一 blockBytes 的樣本，缺同軸樣本就不給比例。</summary>
    private static DeepBenchInsight? BuildStorageScalingInsight(DeepBenchTestResult storage)
    {
        foreach (DeepBenchMetric metric in storage.Metrics)
        {
            if (metric.Points.Count == 0) continue;
            var byBlock = metric.Points
                .Where(point => point.Axes.ContainsKey("blockBytes") && point.Axes.ContainsKey("queueDepth"))
                .GroupBy(point => point.Axes["blockBytes"], StringComparer.Ordinal);
            foreach (var blockGroup in byBlock)
            {
                var qd1 = blockGroup.Where(point => point.Axes["queueDepth"] == "1").ToArray();
                if (qd1.Length == 0) continue;
                var best = blockGroup
                    .Where(point => point.Axes["queueDepth"] != "1")
                    .OrderByDescending(point => point.Value)
                    .FirstOrDefault();
                if (best is null) continue;
                double qd1Mean = qd1.Average(point => point.Value);
                if (qd1Mean == 0) continue;
                double scaling = best.Value / qd1Mean;
                int blockSize = int.TryParse(blockGroup.Key, out int parsed) ? parsed : 0;
                string blockText = blockSize > 0 ? $"{blockSize / 1024.0:0.#} KiB" : blockGroup.Key;
                return new(
                    "儲存佇列擴展性",
                    $"同場 {metric.Title}（{blockText}）QD1 到 QD{best.Axes["queueDepth"]} 的實測比例為 {scaling:0.##}×；僅描述觀察值，不代表理論上限。",
                    [storage.TestId]);
            }
        }
        return null;
    }

    private static string NormalizeDomain(string domain) => domain switch
    {
        "cpu" => "CPU",
        "topology" => "拓撲",
        "memory" => "記憶體",
        "gpu" => "GPU",
        "storage" => "儲存",
        _ => domain
    };
}
