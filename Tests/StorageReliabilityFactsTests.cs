using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 儲存可靠性計數器事實（<see cref="StorageReliabilityFactsService"/>）。
///
/// 這一組測的是<b>誠實邊界</b>而不是數值本身：0 是值、空是空、讀不到要說得出原因。
/// 假的來源注入，不碰 WMI——這條接縫的用途正是如此。
/// </summary>
public class StorageReliabilityFactsTests
{
    /// <summary>全部欄位皆未提供的空殼；測試用 <c>with</c> 只覆蓋關心的欄位。</summary>
    private static readonly StorageReliabilityRaw Blank = new(
        DeviceId: 0, Wear: null, Temperature: null, TemperatureMax: null, PowerOnHours: null,
        StartStopCycleCount: null, StartStopCycleCountMax: null, LoadUnloadCycleCount: null,
        LoadUnloadCycleCountMax: null, ReadErrorsTotal: null, ReadErrorsCorrected: null,
        ReadErrorsUncorrected: null, WriteErrorsTotal: null, WriteErrorsCorrected: null,
        WriteErrorsUncorrected: null, ReadLatencyMax: null, WriteLatencyMax: null,
        FlushLatencyMax: null, ManufactureDate: null);

    private sealed class FakeSource : IStorageReliabilitySource
    {
        public bool Available { get; init; } = true;
        public string? UnavailableReason { get; init; }
        public IReadOnlyList<StorageReliabilityRaw> Rows { get; init; } = [];
        public IReadOnlyList<StorageReliabilityRaw> Read() => Rows;
    }

    private static readonly DateTimeOffset At = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 值為零必須是讀到而不是沒有()
    {
        // 磨損 0%、延遲 0 ms 都是合法值，是這條路上最容易踩的坑（把 0 當成缺）。
        var source = new FakeSource
        {
            Rows = [Blank with { DeviceId = 4, Wear = 0, ReadLatencyMax = 0, FlushLatencyMax = 0 }],
        };

        var facts = StorageReliabilityFactsService.Collect(At, source);

        foreach (string key in new[]
                 {
                     "storage.reliability.4.wear",
                     "storage.reliability.4.read_latency_max",
                     "storage.reliability.4.flush_latency_max",
                 })
        {
            var f = Assert.Single(facts, x => x.Key == key);
            Assert.Equal(FactAvailability.Present, f.Availability);
            Assert.Equal("0", f.Value);
            Assert.Equal(0d, f.NumericValue);
            Assert.Null(f.UnavailableReason);
        }
    }

    [Fact]
    public void 未提供的欄位不得生出事實只在收錄情形裡被數出來()
    {
        var source = new FakeSource { Rows = [Blank with { DeviceId = 4, Wear = 0, Temperature = 29 }] };

        var facts = StorageReliabilityFactsService.Collect(At, source);

        // 未提供 ≠ 0：這些鍵根本不該存在。
        Assert.DoesNotContain(facts, f => f.Key == "storage.reliability.4.power_on_hours");
        Assert.DoesNotContain(facts, f => f.Key == "storage.reliability.4.read_errors_total");

        // 但缺了什麼要被數出來、具名列出，而且說得出「這不是讀取失敗」。
        var coverage = Assert.Single(facts, f => f.Key == "storage.reliability.4.coverage");
        Assert.Contains("未提供：", coverage.Value);
        Assert.Contains("通電時數", coverage.Value);
        Assert.Equal(FactAvailability.NotApplicable, coverage.Availability);
        Assert.Contains("提供者沒有提供", coverage.UnavailableReason);
    }

    [Fact]
    public void 提供者全數提供時收錄情形不必掛不可用原因()
    {
        var full = Blank with
        {
            DeviceId = 1, Wear = 33, Temperature = 29, TemperatureMax = 40, PowerOnHours = 12000,
            StartStopCycleCount = 5, StartStopCycleCountMax = 100, LoadUnloadCycleCount = 7,
            LoadUnloadCycleCountMax = 200, ReadErrorsTotal = 0, ReadErrorsCorrected = 0,
            ReadErrorsUncorrected = 0, WriteErrorsTotal = 0, WriteErrorsCorrected = 0,
            WriteErrorsUncorrected = 0, ReadLatencyMax = 3, WriteLatencyMax = 4,
            FlushLatencyMax = 0, ManufactureDate = "2023-08",
        };

        var facts = StorageReliabilityFactsService.Collect(At, new FakeSource { Rows = [full] });

        var coverage = Assert.Single(facts, f => f.Key == "storage.reliability.1.coverage");
        Assert.Equal(FactAvailability.Present, coverage.Availability);
        Assert.Null(coverage.UnavailableReason);
        Assert.Contains("全數提供", coverage.Value);
        Assert.Contains(facts, f => f.Key == "storage.reliability.1.manufacture_date" && f.Value == "2023-08");
    }

    [Fact]
    public void 來源不可用時要說得出原因而不是安靜地什麼都沒有()
    {
        var facts = StorageReliabilityFactsService.Collect(At,
            new FakeSource { Available = false, UnavailableReason = "ManagementException：提供者載入失敗" });

        var f = Assert.Single(facts);
        Assert.Equal("storage.reliability.available", f.Key);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("提供者載入失敗", f.UnavailableReason);
    }

    [Fact]
    public void 來源可用但沒有任何計數器是環境狀態不是錯誤()
    {
        var facts = StorageReliabilityFactsService.Collect(At, new FakeSource { Rows = [] });

        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不是錯誤", f.UnavailableReason);
    }

    [Fact]
    public void 多顆碟各自成組且覆蓋數等於碟數()
    {
        var source = new FakeSource
        {
            Rows = [Blank with { DeviceId = 0, Wear = 0 }, Blank with { DeviceId = 1, Wear = 33 }],
        };

        var facts = StorageReliabilityFactsService.Collect(At, source);

        var count = Assert.Single(facts, f => f.Key == "storage.reliability.count");
        Assert.Equal("2", count.Value);
        Assert.Equal(2d, count.NumericValue);
        Assert.Contains(facts, f => f.Key == "storage.reliability.0.wear");
        Assert.Contains(facts, f => f.Key == "storage.reliability.1.wear");
    }

    /// <summary>
    /// 行為跟真實 WMI 來源一樣的假來源：<b>查詢之前 <see cref="Available"/> 是 true</b>
    /// （還沒試過，無從得知），失敗之後才翻成 false 並帶上原因。
    /// <para>
    /// 這一條是先前實作缺陷的迴歸測試：<c>Collect</c> 原本<b>先看 Available 再 Read</b>，
    /// 於是查詢失敗會走進「可用但 0 筆」那一支，把一次失敗寫成
    /// 「讀到了、只是沒有計數器——不是錯誤」。而舊的假來源測試沒抓到，因為它們的
    /// <c>Available</c> 是固定的、<c>Read()</c> 不會改它。
    /// </para>
    /// </summary>
    private sealed class FailsOnReadSource : IStorageReliabilitySource
    {
        public bool Available { get; private set; } = true;   // 還沒讀過＝還算能用
        public string? UnavailableReason { get; private set; }
        public IReadOnlyList<StorageReliabilityRaw> Read()
        {
            Available = false;
            UnavailableReason = "ManagementException：提供者載入失敗";
            return [];
        }
    }

    [Fact]
    public void 讀取當下才失敗的來源要如實說出原因不得掉進不是錯誤那一支()
    {
        var facts = StorageReliabilityFactsService.Collect(At, new FailsOnReadSource());

        var f = Assert.Single(facts);
        Assert.Equal("storage.reliability.available", f.Key);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("提供者載入失敗", f.UnavailableReason);
        Assert.DoesNotContain("不是錯誤", f.Value);
        Assert.DoesNotContain("不是錯誤", f.UnavailableReason);
    }

    [Fact]
    [Trait("Category", "InjectedComposition")]
    public void 共用組裝點注入其餘來源仍把可靠性接進事實清單與渲染列()
    {
        // 先前這一組事實接進了 AllFacts 與報告匯出，卻沒有任何地方呼叫載入——
        // 服務層測試全綠，真實 App 裡卻永遠是空的。這一條釘住「有人接線」。
        var svc = new EvidenceLabService();
        var source = new FakeSource { Rows = [Blank with { DeviceId = 0, Wear = 12 }] };

        // 本條釘的是可靠性來源的組裝與呈現；其餘來源注入為不收集的合成階段，
        // 不觸發全機 WMI／EventLog／磁碟探測。完整來源整合另由 RealHardwareFactSnapshot 保留。
        int otherSourceCollections = 0;
        EvidenceCollection.LoadUsermodeFacts(svc, source, other =>
        {
            Assert.Same(svc, other);
            otherSourceCollections++;
        });

        Assert.Equal(1, otherSourceCollections);
        Assert.Equal("LoadStorageReliability", Assert.Single(svc.Steps).Name);
        Assert.NotEmpty(svc.StorageReliabilityFacts);
        Assert.Contains(svc.StorageReliabilityFacts, f => f.Key == "storage.reliability.0.wear" && f.Value == "12");
        Assert.Contains(svc.AllFacts, f => f.Key == "storage.reliability.0.wear");
        // 渲染列（頁面用的那一份）沒有 Key 欄，只有顯示文字——所以用顯示名對。
        // 渲染列的 Value 已含單位（"12 %"），所以比對前綴而不是全等。
        Assert.Contains(svc.FirmwareSecurityRows, r => r.Name.Contains("磨損程度") && r.Value.StartsWith("12", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "InjectedComposition")]
    public void 共用組裝點注入其餘來源時可靠性例外仍留下錯誤且繼續收集()
    {
        // 附加功能：來源爆炸（注入一個會丟的假來源）不能讓啟動或 CLI 整批失敗。
        var svc = new EvidenceLabService();

        // 注入其餘來源，並驗證可靠性來源失敗後仍到達下一收集階段。
        int otherSourceCollections = 0;
        EvidenceCollection.LoadUsermodeFacts(svc, new ThrowingSource(), other =>
        {
            Assert.Same(svc, other);
            otherSourceCollections++;
        });

        Assert.Equal(1, otherSourceCollections);
        Assert.Equal("LoadStorageReliability", Assert.Single(svc.Steps).Name);
        // 三態要留下痕跡，不是靜默什麼都沒有——空集合讀起來像「沒問題」。
        var f = Assert.Single(svc.StorageReliabilityFacts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("WMI 整個炸掉", f.UnavailableReason);
    }

    private sealed class ThrowingSource : IStorageReliabilitySource
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<StorageReliabilityRaw> Read() => throw new InvalidOperationException("WMI 整個炸掉");
    }

    [Fact]
    public void 逾時包裝不能把來源例外吞成沒有計數器()
    {
        var wrapped = new StorageReliabilityTimeoutSource(new ThrowingSource(), 5000);

        var f = Assert.Single(StorageReliabilityFactsService.Collect(At, wrapped));

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("InvalidOperationException", f.UnavailableReason);
        Assert.Contains("WMI 整個炸掉", f.UnavailableReason);
        Assert.DoesNotContain("不是錯誤", f.UnavailableReason);
    }

    private sealed class ThrowingStatusSource(string property) : IStorageReliabilitySource
    {
        public bool Available => property == nameof(Available)
            ? throw new InvalidOperationException("Available getter 失敗") : true;
        public string? UnavailableReason => throw new InvalidOperationException("UnavailableReason getter 失敗");
        public IReadOnlyList<StorageReliabilityRaw> Read() => [];
    }

    [Theory]
    [InlineData("Available")]
    [InlineData("UnavailableReason")]
    public void 逾時包裝的來源狀態getter例外也必須成為可見失敗(string property)
    {
        var wrapped = new StorageReliabilityTimeoutSource(new ThrowingStatusSource(property), 5000);

        var f = Assert.Single(StorageReliabilityFactsService.Collect(At, wrapped));

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains(property + " getter 失敗", f.UnavailableReason);
        Assert.Contains("InvalidOperationException", f.UnavailableReason);
    }

    [Fact]
    public void 逾時包裝保留零值及來源自行回報的失敗()
    {
        var good = new StorageReliabilityTimeoutSource(
            new FakeSource { Rows = [Blank with { DeviceId = 4, Wear = 0 }] }, 5000);
        var wear = Assert.Single(StorageReliabilityFactsService.Collect(At, good),
            f => f.Key == "storage.reliability.4.wear");
        Assert.Equal(FactAvailability.Present, wear.Availability);
        Assert.Equal(0d, wear.NumericValue);

        var bad = new StorageReliabilityTimeoutSource(new FailsOnReadSource(), 5000);
        var failure = Assert.Single(StorageReliabilityFactsService.Collect(At, bad));
        Assert.Equal(FactAvailability.ReadError, failure.Availability);
        Assert.Contains("提供者載入失敗", failure.UnavailableReason);
    }

    /// <summary>同步來源由測試 finally 明確釋放；沒有真 WMI，也不留永久卡住的 worker。</summary>
    private sealed class ControlledSource : IStorageReliabilitySource
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public bool Available => true;
        public string? UnavailableReason => null;
        public void Release() => _release.TrySetResult();
        public IReadOnlyList<StorageReliabilityRaw> Read()
        {
            Interlocked.Increment(ref _reads);
            Entered.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
            return [Blank with { Wear = 0 }];
        }
    }

    [Fact]
    public async Task 逾時後同一來源換包裝器也不能重開且晚到結果不得覆蓋失敗()
    {
        var source = new ControlledSource();
        var first = new StorageReliabilityTimeoutSource(source, 0);
        try
        {
            var timeout = Assert.Single(StorageReliabilityFactsService.Collect(At, first));
            Assert.Equal(FactAvailability.ReadError, timeout.Availability);
            Assert.Contains("逾時", timeout.UnavailableReason);
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var again = Assert.Single(StorageReliabilityFactsService.Collect(At, first));
            var otherWrapper = new StorageReliabilityTimeoutSource(source, 5000);
            var other = Assert.Single(StorageReliabilityFactsService.Collect(At, otherWrapper));
            Assert.Equal(FactAvailability.ReadError, again.Availability);
            Assert.Equal(FactAvailability.ReadError, other.Availability);
            Assert.Contains("先前讀取仍在執行", again.UnavailableReason);
            Assert.Contains("先前讀取仍在執行", other.UnavailableReason);
            Assert.Equal(1, source.Reads);
        }
        finally
        {
            source.Release();
            await first.ReadCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(first.Available); // 背景完成不得把已返回的失敗改成成功。
        Assert.Contains("先前讀取仍在執行", first.UnavailableReason);
        var recovered = new StorageReliabilityTimeoutSource(source, 5000);
        Assert.Contains(StorageReliabilityFactsService.Collect(At, recovered),
            f => f.Key == "storage.reliability.0.wear" && f.Availability == FactAvailability.Present);
        Assert.Equal(2, source.Reads); // 確實結束後才可再次讀；不是永久封鎖來源。
    }

    [Fact]
    public async Task 尚未逾時的讀取也必須排除另一個包裝器同時讀相同來源()
    {
        var source = new ControlledSource();
        var first = new StorageReliabilityTimeoutSource(source, 5000);
        var pending = Task.Run(() => StorageReliabilityFactsService.Collect(At, first));
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = new StorageReliabilityTimeoutSource(source, 5000);
            var rejected = Assert.Single(StorageReliabilityFactsService.Collect(At, second));
            Assert.Equal(FactAvailability.ReadError, rejected.Availability);
            Assert.Contains("先前讀取仍在執行", rejected.UnavailableReason);
            Assert.Equal(1, source.Reads);
        }
        finally
        {
            source.Release();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await first.ReadCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(first.Available);
        Assert.Equal(1, source.Reads);
    }

    private sealed class ControlledWmiQuery : StorageReliabilityWmiSource.IQuery
    {
        public readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _throwRead;
        private readonly bool _throwDispose;
        private int _disposeCalls;
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public ControlledWmiQuery(bool blockRead = false, bool blockDispose = false,
            bool throwRead = false, bool throwDispose = false)
        {
            _throwRead = throwRead;
            _throwDispose = throwDispose;
            if (!blockRead) _readRelease.TrySetResult();
            if (!blockDispose) _disposeRelease.TrySetResult();
        }

        public IReadOnlyList<StorageReliabilityRaw> Read()
        {
            ReadEntered.TrySetResult();
            _readRelease.Task.GetAwaiter().GetResult();
            if (_throwRead) throw new InvalidOperationException("合成 WMI 遲到讀取失敗");
            return [Blank with { Wear = 23 }];
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            DisposeEntered.TrySetResult();
            _disposeRelease.Task.GetAwaiter().GetResult();
            if (_throwDispose) throw new InvalidOperationException("合成 WMI 遲到清理失敗");
        }

        public void Release()
        {
            _readRelease.TrySetResult();
            _disposeRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task StorageWMI逾時後不同source實例不得重發且不能提早清理()
    {
        string identity = "test-storage-provider-" + Guid.NewGuid().ToString("N");
        var query = new ControlledWmiQuery(blockRead: true);
        var firstSource = new StorageReliabilityWmiSource(() => query, identity);
        var first = new StorageReliabilityTimeoutSource(firstSource, 0);
        int otherQueries = 0;
        try
        {
            var timeout = Assert.Single(StorageReliabilityFactsService.Collect(At, first));
            Assert.Equal(FactAvailability.ReadError, timeout.Availability);
            Assert.Contains("逾時", timeout.UnavailableReason);
            await query.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var secondSource = new StorageReliabilityWmiSource(() =>
            {
                Interlocked.Increment(ref otherQueries);
                return new ControlledWmiQuery();
            }, identity.ToUpperInvariant());
            var second = new StorageReliabilityTimeoutSource(secondSource, 5000);
            var denied = Assert.Single(StorageReliabilityFactsService.Collect(At, second));
            Assert.Equal(FactAvailability.ReadError, denied.Availability);
            Assert.Contains("先前可靠性查詢或清理仍在執行", denied.UnavailableReason);
            Assert.Equal(0, otherQueries);
            Assert.Equal(0, query.DisposeCalls);
        }
        finally
        {
            query.Release();
            await first.ReadCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, query.DisposeCalls);
        Assert.False(first.Available);
        Assert.Contains("逾時", first.UnavailableReason);
        var recovered = new StorageReliabilityWmiSource(() => new ControlledWmiQuery(), identity);
        Assert.Contains(StorageReliabilityFactsService.Collect(At,
            new StorageReliabilityTimeoutSource(recovered, 5000)),
            f => f.Key == "storage.reliability.0.wear" && f.Value == "23");
    }

    [Fact]
    public async Task StorageWMI不同provider身份不應互相封鎖()
    {
        string identity = "test-storage-provider-" + Guid.NewGuid().ToString("N");
        var query = new ControlledWmiQuery(blockRead: true);
        var first = new StorageReliabilityTimeoutSource(
            new StorageReliabilityWmiSource(() => query, identity), 0);
        try
        {
            Assert.Equal(FactAvailability.ReadError,
                Assert.Single(StorageReliabilityFactsService.Collect(At, first)).Availability);
            await query.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var independent = new StorageReliabilityWmiSource(() => new ControlledWmiQuery(), identity + "-other");
            Assert.Contains(StorageReliabilityFactsService.Collect(At,
                new StorageReliabilityTimeoutSource(independent, 5000)),
                f => f.Key == "storage.reliability.0.wear" && f.Availability == FactAvailability.Present);
            Assert.Equal(0, query.DisposeCalls);
        }
        finally
        {
            query.Release();
            await first.ReadCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StorageWMI讀完但Dispose尚未結束仍不得重發()
    {
        string identity = "test-storage-provider-" + Guid.NewGuid().ToString("N");
        var query = new ControlledWmiQuery(blockDispose: true);
        var first = new StorageReliabilityTimeoutSource(
            new StorageReliabilityWmiSource(() => query, identity), 0);
        int otherQueries = 0;
        try
        {
            Assert.Equal(FactAvailability.ReadError,
                Assert.Single(StorageReliabilityFactsService.Collect(At, first)).Availability);
            await query.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = new StorageReliabilityWmiSource(() =>
            {
                Interlocked.Increment(ref otherQueries);
                return new ControlledWmiQuery();
            }, identity);
            var denied = Assert.Single(StorageReliabilityFactsService.Collect(At,
                new StorageReliabilityTimeoutSource(second, 5000)));
            Assert.Equal(FactAvailability.ReadError, denied.Availability);
            Assert.Contains("清理仍在執行", denied.UnavailableReason);
            Assert.Equal(0, otherQueries);
        }
        finally
        {
            query.Release();
            await first.ReadCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, query.DisposeCalls);
        Assert.False(first.Available);
    }

    [Fact]
    public void StorageWMI讀取與Dispose同時失敗必須保留兩個原因並在完整清理後釋放閘門()
    {
        string identity = "test-storage-provider-" + Guid.NewGuid().ToString("N");
        var query = new ControlledWmiQuery(throwRead: true, throwDispose: true);
        var source = new StorageReliabilityWmiSource(() => query, identity);

        var failure = Assert.Single(StorageReliabilityFactsService.Collect(At,
            new StorageReliabilityTimeoutSource(source, 5000)));

        Assert.Equal(FactAvailability.ReadError, failure.Availability);
        Assert.Contains("讀取失敗：InvalidOperationException：合成 WMI 遲到讀取失敗", failure.UnavailableReason);
        Assert.Contains("清理失敗：InvalidOperationException：合成 WMI 遲到清理失敗", failure.UnavailableReason);
        Assert.Equal(1, query.DisposeCalls);
        var next = new StorageReliabilityWmiSource(() => new ControlledWmiQuery(), identity);
        Assert.Contains(StorageReliabilityFactsService.Collect(At,
            new StorageReliabilityTimeoutSource(next, 5000)),
            f => f.Key == "storage.reliability.0.wear" && f.Availability == FactAvailability.Present);
    }

    [Theory]
    [InlineData(true, false, "讀取失敗")]
    [InlineData(false, true, "清理失敗")]
    [InlineData(true, true, "讀取失敗")]
    public async Task StorageWMI遲到讀取或清理例外不得覆寫逾時且結束後應釋放閘門(
        bool throwRead, bool throwDispose, string reason)
    {
        string identity = "test-storage-provider-" + Guid.NewGuid().ToString("N");
        var query = new ControlledWmiQuery(blockRead: true, throwRead: throwRead, throwDispose: throwDispose);
        var source = new StorageReliabilityWmiSource(() => query, identity);
        var first = new StorageReliabilityTimeoutSource(source, 0);
        try
        {
            var timeout = Assert.Single(StorageReliabilityFactsService.Collect(At, first));
            Assert.Equal(FactAvailability.ReadError, timeout.Availability);
            Assert.Contains("逾時", timeout.UnavailableReason);
            await query.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            query.Release();
            await first.ReadCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(source.Available);
        Assert.Contains(reason, source.UnavailableReason);
        Assert.Contains("InvalidOperationException", source.UnavailableReason);
        if (throwRead) Assert.Contains("合成 WMI 遲到讀取失敗", source.UnavailableReason);
        if (throwDispose) Assert.Contains("合成 WMI 遲到清理失敗", source.UnavailableReason);
        Assert.Equal(1, query.DisposeCalls);
        Assert.False(first.Available);
        Assert.Contains("逾時", first.UnavailableReason);
        var next = new StorageReliabilityWmiSource(() => new ControlledWmiQuery(), identity);
        Assert.Contains(StorageReliabilityFactsService.Collect(At,
            new StorageReliabilityTimeoutSource(next, 5000)),
            f => f.Key == "storage.reliability.0.wear" && f.Availability == FactAvailability.Present);
    }

    private sealed class CleanupResource(string name, List<string> disposed, bool fail = false) : IDisposable
    {
        public void Dispose()
        {
            disposed.Add(name);
            if (fail) throw new InvalidOperationException("cleanup-" + name);
        }
    }

    [Fact]
    public void 正式WmiQuery收尾路徑保留原讀取與多層清理失敗且繼續處置外層()
    {
        string identity = "test-storage-provider-" + Guid.NewGuid().ToString("N");
        var disposed = new List<string>();
        var query = new StorageReliabilityWmiSource.WmiQuery(cleanup =>
            cleanup.Use(new CleanupResource("searcher", disposed, true), "searcher", outerResource =>
                cleanup.Use<CleanupResource, IReadOnlyList<StorageReliabilityRaw>>(
                    new CleanupResource("related", disposed, true), "related", innerResource =>
                        throw new InvalidOperationException("original-counter-read"))));
        var source = new StorageReliabilityWmiSource(() => query, identity);
        var failure = Assert.Single(StorageReliabilityFactsService.Collect(At,
            new StorageReliabilityTimeoutSource(source, 5000)));

        Assert.Equal(FactAvailability.ReadError, failure.Availability);
        Assert.Contains("original-counter-read", failure.UnavailableReason);
        Assert.Contains("cleanup-related", failure.UnavailableReason);
        Assert.Contains("cleanup-searcher", failure.UnavailableReason);
        Assert.Equal(new[] { "related", "searcher" }, disposed);
        var next = new StorageReliabilityWmiSource(() => new ControlledWmiQuery(), identity);
        Assert.Contains(StorageReliabilityFactsService.Collect(At,
            new StorageReliabilityTimeoutSource(next, 5000)),
            f => f.Key == "storage.reliability.0.wear" && f.Availability == FactAvailability.Present);
    }

    [Fact]
    public void 正式WmiQuery清理失敗的診斷有界但每個已取得資源仍被處置()
    {
        var disposed = new List<string>();
        var originalReadFailure = new InvalidOperationException("original-read-" + new string('x', 20000));
        IReadOnlyList<StorageReliabilityRaw> Nest(StorageReliabilityWmiCleanup.Scope cleanup, int depth)
        {
            if (depth == 0) throw originalReadFailure;
            string name = "resource-" + depth;
            return cleanup.Use(new CleanupResource(name, disposed, true), name, _ => Nest(cleanup, depth - 1));
        }
        var query = new StorageReliabilityWmiSource.WmiQuery(cleanup => Nest(cleanup, 12));

        var error = Assert.Throws<InvalidOperationException>(() => query.Read());

        Assert.Equal(12, disposed.Count);
        Assert.Equal(12, disposed.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("original-read-", error.Message);
        Assert.Contains("另有 5 項清理失敗未列", error.Message);
        Assert.True(error.Message.Length <= StorageReliabilityWmiCleanup.MaximumDiagnosticCharacters);
        Assert.Same(originalReadFailure, error.InnerException);
    }

    [Fact]
    public void 正式WmiQuery讀取成功但清理失敗不得回Present或繼續下一交易()
    {
        string identity = "test-storage-provider-" + Guid.NewGuid().ToString("N");
        var disposed = new List<string>();
        bool nextTransaction = false;
        var query = new StorageReliabilityWmiSource.WmiQuery(cleanup =>
        {
            cleanup.Use(new CleanupResource("counter", disposed, true), "counter", _ => true);
            nextTransaction = true;
            return [Blank with { Wear = 23 }];
        });
        var source = new StorageReliabilityWmiSource(() => query, identity);
        var failure = Assert.Single(StorageReliabilityFactsService.Collect(At,
            new StorageReliabilityTimeoutSource(source, 5000)));

        Assert.Equal(FactAvailability.ReadError, failure.Availability);
        Assert.Contains("cleanup-counter", failure.UnavailableReason);
        Assert.DoesNotContain("原始讀取失敗", failure.UnavailableReason);
        Assert.False(nextTransaction);
        Assert.Single(disposed);
    }

    [Fact]
    public void 兩條入口都要呼叫共用組裝點不能有一條漏掉()
    {
        // 這一條不是行為測試，是「接線還在不在」的守門，理由就是先前那次失敗：
        // 服務有、測試有、事實鍵也接進了 AllFacts——就是**沒有任何地方呼叫載入**，
        // 所以真實 App 與 CLI 裡永遠是空的。單元測試測不到「有沒有人接線」。
        // 原始碼層的守門是唯一能在不載入驅動的前提下檢查啟動路徑的辦法。
        string root = FindRepoRoot();
        string app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        string startup = File.ReadAllText(Path.Combine(root, "ViewModels", "StartupSequence.cs"));
        string collection = File.ReadAllText(Path.Combine(root, "Services", "EvidenceCollection.cs"));

        Assert.Contains("EvidenceCollection.CollectForCli", app);
        Assert.Contains("EvidenceCollection.LoadUsermodeFactsDeferred", startup);
        Assert.Contains("LoadUsermodeFactsWithPlatform", collection);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到專案根目錄（XinSpect.csproj）");
    }

    [Fact]
    public void 鍵不得重複且每一筆都帶得出處()
    {
        var source = new FakeSource
        {
            Rows = [Blank with { DeviceId = 0, Wear = 0, Temperature = 29, ReadLatencyMax = 7 }],
        };

        var facts = StorageReliabilityFactsService.Collect(At, source);

        Assert.Equal(facts.Count, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(facts, f => Assert.Equal(StorageReliabilityFactsService.Source, f.Source));
        Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Category)));
        Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Name)));
    }
}
