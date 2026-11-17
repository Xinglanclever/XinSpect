namespace XinSpect;

/// <summary>Pure projection only: the caller owns the single event read and existing publication lease.</summary>
public static class WheaErrorFactsService
{
    public static IReadOnlyList<HardwareFact> Collect(IReadOnlyList<WheaMemoryRecord> records, DateTimeOffset at) =>
        Array.AsReadOnly(WheaMemoryFactsService.Collect(records, at).Concat(WheaPcieFactsService.Collect(records, at)).Concat(WheaProcessorGenericFactsService.Collect(records, at)).Concat(WheaXpfProcessorFactsService.Collect(records, at)).ToArray());
}
