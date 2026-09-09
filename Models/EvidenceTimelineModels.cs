namespace XinSpect;

/// <summary>證據來源對讀值的直接程度；不代表數值必然正確。</summary>
public enum EvidenceTrustLevel
{
    Unknown = 0,
    Derived = 1,
    OsReported = 2,
    DeviceReported = 3,
    DirectHardware = 4,
}

/// <summary>數值的時間語義，供差值與中斷辨識使用。</summary>
public enum EvidenceMetricSemantics
{
    Gauge = 0,
    MonotonicCounter = 1,
    Ratio = 2,
    ResidencyPercent = 3,
    LinkWidth = 4,
    LinkSpeed = 5,
}

/// <summary>讀值扮演的角色；限制與能力不得冒充實際量測。</summary>
public enum EvidenceValueRole
{
    Observed = 0,
    DerivedMeasurement = 1,
    ConfiguredLimit = 2,
    AdvertisedCapability = 3,
}

public enum EvidenceDiscontinuityKind
{
    Gap,
    CounterReset,
    CounterWrap,
}

public enum EvidenceEventKind
{
    LinkValueDecreased,
    CounterIncreased,
    PercentageIncreased,
    EffectiveRatioObserved,
    ResidencyObserved,
}

/// <summary>一筆不可變的客觀樣本；資料來源只需提供這個契約。</summary>
public sealed record EvidenceSample
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Category { get; init; }
    public required string DeviceKey { get; init; }
    public required string Metric { get; init; }
    public required decimal Value { get; init; }
    public required string Unit { get; init; }
    public required string Source { get; init; }
    public EvidenceTrustLevel Trust { get; init; }
    public required DateTimeOffset TimeUtc { get; init; }
    public EvidenceMetricSemantics Semantics { get; init; }
    public EvidenceValueRole Role { get; init; }
    public int? CounterBits { get; init; }
    public Dictionary<string, string> Context { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>相鄰樣本間可證明的中斷；reset 與 wrap 分開報，不猜測淨增量。</summary>
public sealed record EvidenceDiscontinuity
{
    public required EvidenceDiscontinuityKind Kind { get; init; }
    public required DateTimeOffset FromUtc { get; init; }
    public required DateTimeOffset ToUtc { get; init; }
    public required decimal PreviousValue { get; init; }
    public required decimal CurrentValue { get; init; }
    public TimeSpan? ExpectedInterval { get; init; }
}

/// <summary>首末值與保守差值。跨 reset、wrap 或缺口時 Delta 為 null。</summary>
public sealed record EvidenceDelta
{
    public required EvidenceSample First { get; init; }
    public required EvidenceSample Last { get; init; }
    public decimal? Delta { get; init; }
    public IReadOnlyList<EvidenceDiscontinuity> Discontinuities { get; init; } = [];
    public bool IsContinuous => Discontinuities.Count == 0;
}

/// <summary>桶化結果保留首末、最小、最大與平均，不把尖峰抹掉。</summary>
public sealed record EvidenceBucket
{
    public required DateTimeOffset FromUtc { get; init; }
    public required DateTimeOffset ToUtc { get; init; }
    public required int SampleCount { get; init; }
    public required decimal First { get; init; }
    public required decimal Last { get; init; }
    public required decimal Min { get; init; }
    public required decimal Max { get; init; }
    public required decimal Average { get; init; }
}

/// <summary>由相鄰客觀樣本產生的事件；不含壽命預測或健康評分。</summary>
public sealed record EvidenceEvent
{
    public required EvidenceEventKind Kind { get; init; }
    public required string Category { get; init; }
    public required string DeviceKey { get; init; }
    public required string Metric { get; init; }
    public required DateTimeOffset TimeUtc { get; init; }
    public required decimal PreviousValue { get; init; }
    public required decimal CurrentValue { get; init; }
    public decimal? Delta { get; init; }
    public required string Unit { get; init; }
    public required string Source { get; init; }
    public required EvidenceTrustLevel Trust { get; init; }
}

public sealed record EvidenceQuery
{
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public string? Category { get; init; }
    public string? DeviceKey { get; init; }
    public string? Metric { get; init; }
    public int? MaxPoints { get; init; }
}
