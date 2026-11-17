using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Offline synthetic files/streams only; no network, native calls, real-file races or timers.</summary>
public class RedfishBoundedReadTests
{
    private const string RootUri = "/redfish/v1";
    private const string ManagerUri = "/redfish/v1/Managers/A";
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-17T08:00:00Z");

    private sealed class ScriptedStream(byte[] bytes) : Stream
    {
        private int _offset;
        public int MaxChunk { get; init; } = int.MaxValue;
        public long? ReportedLength { get; init; }
        public bool Seekable { get; init; }
        public int? ThrowAfterBytes { get; init; }
        public int? FirstReadVisibleBytes { get; init; }
        public int BytesReturned { get; private set; }
        public int DisposeCount { get; private set; }
        public int LengthReads { get; private set; }
        public int ReadCalls { get; private set; }
        public List<int> RequestedCounts { get; } = [];
        public override bool CanRead => DisposeCount == 0;
        public override bool CanSeek => Seekable;
        public override bool CanWrite => false;
        public override long Length
        {
            get
            {
                LengthReads++;
                return ReportedLength ?? throw new NotSupportedException("Injected length is unavailable");
            }
        }
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
            RequestedCounts.Add(count);
            if (ThrowAfterBytes is { } failAt && _offset >= failAt) throw new IOException("PRIVATE-IO-DETAIL");
            int visible = ReadCalls++ == 0 && FirstReadVisibleBytes is { } first ? Math.Min(first, bytes.Length) : bytes.Length;
            int read = Math.Min(Math.Min(count, MaxChunk), visible - _offset);
            if (ThrowAfterBytes is { } limit) read = Math.Min(read, limit - _offset);
            if (read <= 0) return 0;
            bytes.AsSpan(_offset, read).CopyTo(buffer.AsSpan(offset, read));
            _offset += read;
            BytesReturned += read;
            return read;
        }
        protected override void Dispose(bool disposing) { DisposeCount++; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Bundle : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "xinspect-redfish-budget-" + Guid.NewGuid().ToString("N"));
        public Dictionary<string, Func<ScriptedStream>> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Opened { get; } = [];
        public List<ScriptedStream> Streams { get; } = [];
        public Bundle() => Put(RootUri, "{}");
        public string PathFor(string uri) => Path.Combine(Root, uri.Trim('/').Replace('/', Path.DirectorySeparatorChar), "index.json");
        public void Put(string uri, string json) => PutBytes(uri, Encoding.UTF8.GetBytes(json));
        public void PutBytes(string uri, byte[] bytes)
        {
            string path = PathFor(uri);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        public void Inject(string uri, Func<ScriptedStream> stream)
        {
            Put(uri, "{}"); // Mapping still checks a real, ordinary local path before invoking the seam.
            Overrides[PathFor(uri)] = stream;
        }
        public void ManagerLinks(IEnumerable<string> uris, bool timestamp = true)
            => Put(RootUri, JsonSerializer.Serialize(new
            {
                GeneratedAtUtc = timestamp ? "2026-11-17T07:00:00Z" : null,
                Managers = new { Members = uris.Select(uri => new Dictionary<string, string> { ["@odata.id"] = uri }) },
            }));
        public RedfishInventory Read(int fileBytes = 4096, long totalBytes = 65536, int attempts = 128)
            => RedfishBundleService.Read(Root, new(fileBytes, totalBytes, attempts), path =>
            {
                Opened.Add(path);
                var stream = Overrides.TryGetValue(path, out var factory) ? factory() : new ScriptedStream(File.ReadAllBytes(path));
                Streams.Add(stream);
                return stream;
            });
        public int BytesReturned => Streams.Sum(s => s.BytesReturned);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private static byte[] Padded(string json, int bytes)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(json);
        Assert.True(prefix.Length <= bytes);
        byte[] padded = Enumerable.Repeat((byte)' ', bytes).ToArray();
        prefix.CopyTo(padded, 0);
        return padded;
    }
    private static HardwareFact Summary(RedfishInventory inventory)
        => RedfishFactsService.Collect(inventory, At).Single(f => f.Key == RedfishFactsService.SummaryKey);
    private static void AssertReleased(Bundle bundle)
    {
        Assert.NotEmpty(bundle.Streams);
        Assert.All(bundle.Streams, stream => Assert.Equal(1, stream.DisposeCount));
        Assert.All(bundle.Streams, stream => Assert.Equal(0, stream.LengthReads));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void 虛報Length不影響同handle實讀超限判定(long reportedLength)
    {
        using var bundle = new Bundle();
        bundle.ManagerLinks([ManagerUri]);
        bundle.Inject(ManagerUri, () => new ScriptedStream(Padded("{\"Id\":\"A\"}", 1200)) { ReportedLength = reportedLength });
        var inventory = bundle.Read(fileBytes: 1024);
        Assert.Empty(inventory.Managers);
        var source = bundle.Streams[1];
        Assert.Equal(1025, source.BytesReturned);
        Assert.Contains(inventory.Warnings, w => w.Contains("FileByteBudgetExceeded", StringComparison.Ordinal));
        Assert.True(inventory.ReadIncomplete);
        Assert.Equal(FactAvailability.ReadError, Summary(inventory).Availability);
        AssertReleased(bundle);
    }

    [Fact]
    public void 首段之後才顯露增長尾部仍只多讀一byte()
    {
        using var bundle = new Bundle();
        bundle.ManagerLinks([ManagerUri]);
        bundle.Inject(ManagerUri, () => new ScriptedStream(Padded("{\"Id\":\"A\"}", 1300))
        { ReportedLength = 16, FirstReadVisibleBytes = 16 });
        var inventory = bundle.Read(fileBytes: 1024);
        Assert.Empty(inventory.Managers);
        Assert.Equal(1025, bundle.Streams[1].BytesReturned);
        Assert.True(bundle.Streams[1].ReadCalls > 1);
        AssertReleased(bundle);
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void 單檔精確邊界驗EOF而不截斷冒充完整JSON(int length, bool accepted)
    {
        using var bundle = new Bundle();
        bundle.ManagerLinks([ManagerUri]);
        bundle.Inject(ManagerUri, () => new ScriptedStream(Padded("{\"Id\":\"A\"}", length)) { MaxChunk = 7 });
        var inventory = bundle.Read(fileBytes: 1024);
        Assert.Equal(accepted ? 1 : 0, inventory.Managers.Count);
        Assert.Equal(length, bundle.Streams[1].BytesReturned);
        Assert.Equal(!accepted, inventory.ReadIncomplete);
        AssertReleased(bundle);
    }

    [Fact]
    public void 不可seek且Length會throw的短讀來源可正常解析並在釋放後消費()
    {
        using var bundle = new Bundle();
        bundle.ManagerLinks([ManagerUri]);
        bundle.Inject(ManagerUri, () => new ScriptedStream(Encoding.UTF8.GetBytes("{\"Id\":\"A\",\"Model\":\"中文機型\"}")) { MaxChunk = 1 });
        var inventory = bundle.Read();
        Assert.Equal("中文機型", Assert.Single(inventory.Managers).Model);
        Assert.Equal(FactAvailability.Present, Summary(inventory).Availability);
        Assert.False(inventory.ReadIncomplete);
        AssertReleased(bundle);
        Assert.Contains(RedfishFactsService.Collect(inventory, At), f => f.Value.Contains("中文機型", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public void 保留ReadAllText預設與全部既有BOM辨識(string kind)
    {
        Encoding encoding = kind switch
        {
            "utf8" => new UTF8Encoding(false), "utf8-bom" => new UTF8Encoding(true),
            "utf16-le" => new UnicodeEncoding(false, true), "utf16-be" => new UnicodeEncoding(true, true),
            "utf32-le" => new UTF32Encoding(false, true), "utf32-be" => new UTF32Encoding(true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using var bundle = new Bundle();
        bundle.ManagerLinks([ManagerUri]);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("{\"Id\":\"A\",\"Model\":\"中文機型\"}")];
        bundle.Inject(ManagerUri, () => new ScriptedStream(bytes) { MaxChunk = 1 });
        var inventory = bundle.Read();
        Assert.Equal("中文機型", Assert.Single(inventory.Managers).Model);
        Assert.Equal(bytes.Length, bundle.Streams[1].BytesReturned);
        AssertReleased(bundle);
    }

    [Fact]
    public void BOM本身亦計入來源byte上限()
    {
        using var bundle = new Bundle();
        bundle.ManagerLinks([ManagerUri]);
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Padded("{\"Id\":\"A\"}", 1024)];
        bundle.Inject(ManagerUri, () => new ScriptedStream(bytes) { MaxChunk = 2 });
        var inventory = bundle.Read(fileBytes: 1024);
        Assert.Empty(inventory.Managers);
        Assert.Equal(1025, bundle.Streams[1].BytesReturned);
        Assert.Contains(inventory.Warnings, w => w.Contains("FileByteBudgetExceeded", StringComparison.Ordinal));
        AssertReleased(bundle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 全包預算計入壞JSON或重複來源且停止後續open(bool malformed)
    {
        using var bundle = new Bundle();
        const string second = "/redfish/v1/Managers/B";
        const string last = "/redfish/v1/Managers/C";
        bundle.ManagerLinks([ManagerUri, ManagerUri, second, last]);
        bundle.Inject(ManagerUri, () => new ScriptedStream(Padded(malformed ? "{" : "{\"Id\":\"A\"}", 128)) { MaxChunk = 7 });
        bundle.Inject(second, () => new ScriptedStream(Padded("{\"Id\":\"B\"}", 128)));
        bundle.Put(last, "{\"Id\":\"C\"}");
        int rootBytes = File.ReadAllBytes(bundle.PathFor(RootUri)).Length;
        long budget = rootBytes + 256 + 20;
        var inventory = bundle.Read(totalBytes: budget);
        Assert.Equal(budget + 1, bundle.BytesReturned);
        Assert.Equal(4, bundle.Opened.Count); // root, A twice, then rejected B; C is never opened.
        Assert.DoesNotContain(bundle.PathFor(last), bundle.Opened);
        Assert.Equal(malformed ? 0 : 2, inventory.Managers.Count);
        Assert.Contains(inventory.Warnings, w => w.Contains("AggregateByteBudgetExceeded", StringComparison.Ordinal));
        Assert.False(inventory.PowerControlsComplete);
        AssertReleased(bundle);
    }

    [Fact]
    public void 整包恰好用滿保留目前成功項而不再開下一來源()
    {
        using var bundle = new Bundle();
        const string last = "/redfish/v1/Managers/LAST";
        bundle.ManagerLinks([ManagerUri, last]);
        bundle.Inject(ManagerUri, () => new ScriptedStream(Padded("{\"Id\":\"A\"}", 128)));
        bundle.Put(last, "{\"Id\":\"LAST\"}");
        int budget = File.ReadAllBytes(bundle.PathFor(RootUri)).Length + 128;
        var inventory = bundle.Read(totalBytes: budget);
        Assert.Equal("A", Assert.Single(inventory.Managers).Id);
        Assert.Equal(budget, bundle.BytesReturned);
        Assert.Equal(2, bundle.Opened.Count);
        Assert.Equal(FactAvailability.Unknown, Summary(inventory).Availability);
        Assert.Contains("已讀部分", Summary(inventory).Value);
        AssertReleased(bundle);
    }

    [Fact]
    public void 單檔超限仍可讀後項但不退還超限已讀bytes()
    {
        using var bundle = new Bundle();
        const string good = "/redfish/v1/Managers/GOOD";
        bundle.ManagerLinks([ManagerUri, good]);
        bundle.Inject(ManagerUri, () => new ScriptedStream(Padded("{\"Id\":\"A\"}", 1500)));
        bundle.Put(good, "{\"Id\":\"GOOD\"}");
        int rootBytes = File.ReadAllBytes(bundle.PathFor(RootUri)).Length;
        var inventory = bundle.Read(fileBytes: 1024);
        Assert.Equal("GOOD", Assert.Single(inventory.Managers).Id);
        Assert.Equal(rootBytes + 1025 + Encoding.UTF8.GetByteCount("{\"Id\":\"GOOD\"}"), bundle.BytesReturned);
        Assert.True(inventory.ReadIncomplete);
        Assert.Equal(FactAvailability.Unknown, Summary(inventory).Availability);
        AssertReleased(bundle);
    }

    [Fact]
    public void IO例外前返回的bytes不退帳且不重試()
    {
        using var bundle = new Bundle();
        const string later = "/redfish/v1/Managers/LATER";
        bundle.ManagerLinks([ManagerUri, later]);
        bundle.Inject(ManagerUri, () => new ScriptedStream(Padded("{\"Id\":\"A\"}", 128)) { ThrowAfterBytes = 40, MaxChunk = 10 });
        bundle.Inject(later, () => new ScriptedStream(Padded("{\"Id\":\"LATER\"}", 128)));
        int budget = File.ReadAllBytes(bundle.PathFor(RootUri)).Length + 40 + 20;
        var inventory = bundle.Read(totalBytes: budget);
        Assert.Empty(inventory.Managers);
        Assert.Equal(budget + 1, bundle.BytesReturned);
        Assert.Equal(3, bundle.Opened.Count);
        Assert.Contains(inventory.Warnings, w => w.Contains("IOException", StringComparison.Ordinal));
        Assert.DoesNotContain(inventory.Warnings, w => w.Contains("PRIVATE", StringComparison.Ordinal));
        AssertReleased(bundle);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("external")]
    public void 載入嘗試包含root及零byte或解析失敗而不只成功計數(string kind)
    {
        using var bundle = new Bundle();
        const string tail = "/redfish/v1/Managers/TAIL";
        string bad = kind == "external" ? "https://NEVER-FOLLOW.invalid/URI-SECRET" : "/redfish/v1/Managers/MISSING-SECRET";
        bundle.ManagerLinks([bad, bad, bad, tail]);
        if (kind == "malformed") bundle.Inject(bad, () => new ScriptedStream(Encoding.UTF8.GetBytes("{")));
        bundle.Put(tail, "{\"Id\":\"TAIL\"}");
        var inventory = bundle.Read(attempts: 4);
        Assert.Empty(inventory.Managers);
        Assert.DoesNotContain(bundle.PathFor(tail), bundle.Opened);
        Assert.Equal(kind == "malformed" ? 4 : 1, bundle.Opened.Count);
        Assert.Contains(inventory.Warnings, w => w.Contains("LoadAttemptBudgetExceeded", StringComparison.Ordinal));
        Assert.All(inventory.Warnings, w => Assert.DoesNotContain("SECRET", w));
        AssertReleased(bundle);
    }

    [Fact]
    public void 嘗試上限當次成功不被提前拒絕()
    {
        using var bundle = new Bundle();
        bundle.ManagerLinks(["/redfish/v1/Managers/MISSING", ManagerUri]);
        bundle.Put(ManagerUri, "{\"Id\":\"A\"}");
        var inventory = bundle.Read(attempts: 3);
        Assert.Equal("A", Assert.Single(inventory.Managers).Id);
        Assert.DoesNotContain(inventory.Warnings, w => w.Contains("LoadAttemptBudgetExceeded", StringComparison.Ordinal));
        AssertReleased(bundle);
    }

    [Fact]
    public void 正式128次load上限亦計大量缺檔而停止尾端可讀來源()
    {
        using var bundle = new Bundle();
        bundle.ManagerLinks(Enumerable.Repeat("/redfish/v1/Managers/MISSING", 128).Append(ManagerUri));
        bundle.Put(ManagerUri, "{\"Id\":\"A\"}");
        var inventory = bundle.Read(fileBytes: 8192);
        Assert.Empty(inventory.Managers);
        Assert.Single(bundle.Opened);
        Assert.InRange(inventory.Warnings.Count, 1, 64);
        Assert.Contains("LoadAttemptBudgetExceeded", inventory.Warnings[0]);
        var warning = RedfishFactsService.Collect(inventory, At).Single(f => f.Key == "rf.bundle.warnings");
        Assert.Contains("LoadAttemptBudgetExceeded", warning.Value);
        Assert.True(warning.Sensitive);
        AssertReleased(bundle);
    }

    [Fact]
    public void inline成員不冒稱load128上限且不另外開檔()
    {
        using var bundle = new Bundle();
        bundle.Put(RootUri, JsonSerializer.Serialize(new
        { Managers = new { Members = Enumerable.Range(0, 130).Select(i => new { Id = i.ToString() }) } }));
        var inventory = bundle.Read(attempts: 1);
        Assert.Equal(130, inventory.Managers.Count);
        Assert.Single(bundle.Opened);
        Assert.False(inventory.ReadIncomplete);
        AssertReleased(bundle);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(64)]
    public void 普通警告累積後單檔超限原因仍可見且後項繼續讀取(int warningCount)
    {
        using var bundle = new Bundle();
        const string good = "/redfish/v1/Managers/GOOD";
        bundle.ManagerLinks(Enumerable.Repeat("/redfish/v1/MISS-SECRET", warningCount).Append(ManagerUri).Append(good));
        bundle.Inject(ManagerUri, () => new ScriptedStream(Padded("{\"Id\":\"REJECTED\"}", 5000)));
        bundle.Put(good, "{\"Id\":\"GOOD\"}");
        var inventory = bundle.Read();
        Assert.Equal("GOOD", Assert.Single(inventory.Managers).Id);
        Assert.InRange(inventory.Warnings.Count, 1, 64);
        Assert.Contains("FileByteBudgetExceeded", inventory.Warnings[0]);
        Assert.All(inventory.Warnings, w => Assert.DoesNotContain("SECRET", w));
        var warning = RedfishFactsService.Collect(inventory, At).Single(f => f.Key == "rf.bundle.warnings");
        Assert.Contains("FileByteBudgetExceeded", warning.Value);
        Assert.True(warning.Sensitive);
        Assert.Equal(4097, bundle.Streams[1].BytesReturned);
        AssertReleased(bundle);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    public void ServiceRoot失敗保留具體診斷且不被缺時間降成普通Unknown(string json)
    {
        using var bundle = new Bundle();
        bundle.Put(RootUri, json);
        var inventory = bundle.Read();
        Assert.Empty(inventory.Managers);
        Assert.True(inventory.ReadIncomplete);
        Assert.Equal(FactAvailability.ReadError, Summary(inventory).Availability);
        Assert.Contains("讀取失敗", Summary(inventory).UnavailableReason);
        Assert.Contains("來源採集時間未知", Summary(inventory).UnavailableReason);
        string expectedDiagnostic = json == "{"
            ? Assert.ThrowsAny<JsonException>(() => { using var document = JsonDocument.Parse(json); }).GetType().Name
            : "根不是物件";
        Assert.Contains(inventory.Warnings, w => w.Contains(expectedDiagnostic, StringComparison.Ordinal));
        Assert.Contains(RedfishFactsService.Collect(inventory, At), f => f.Key == "rf.bundle.warnings" && f.Sensitive);
        AssertReleased(bundle);
    }

    [Fact]
    public void 明確讀取權限拒絕仍是InsufficientPrivilege而不是不存在BMC()
    {
        using var bundle = new Bundle();
        var inventory = RedfishBundleService.Read(bundle.Root, new(), _ => throw new UnauthorizedAccessException("PATH-SECRET"));
        Assert.Equal(FactAvailability.InsufficientPrivilege, Summary(inventory).Availability);
        Assert.True(inventory.ReadIncomplete);
        Assert.All(inventory.Warnings, w => Assert.DoesNotContain("SECRET", w));
        Assert.Contains(inventory.Warnings, w => w.Contains("UnauthorizedAccessException", StringComparison.Ordinal));
    }

    [Fact]
    public void 已讀項保留來源時間與Reported而只有整體摘要標為部分()
    {
        using var bundle = new Bundle();
        bundle.ManagerLinks([ManagerUri, "/redfish/v1/Managers/MISSING"]);
        bundle.Put(ManagerUri, "{\"Id\":\"A\",\"Model\":\"GOOD\"}");
        var inventory = bundle.Read();
        var facts = RedfishFactsService.Collect(inventory, At);
        var item = facts.Single(f => f.Key.StartsWith("rf.item.manager.", StringComparison.Ordinal));
        Assert.Equal(FactAvailability.Present, item.Availability);
        Assert.Equal(FactTrustLevel.Reported, item.Trust);
        Assert.Contains("2026-11-17T07:00:00.0000000+00:00", item.Source);
        Assert.Equal(FactAvailability.Unknown, Summary(inventory).Availability);
        Assert.Contains("已讀部分", Summary(inventory).Value);
        Assert.Null(Summary(inventory).NumericValue);
        AssertReleased(bundle);
    }

    [Fact]
    public void 手工legacy空inventory不因新reader狀態變成ReadError()
    {
        var inventory = new RedfishInventory("legacy", null, [], [], [], [], [], [], null, ["舊呼叫者警告"]);
        Assert.False(inventory.ReadIncomplete);
        Assert.Equal(FactAvailability.Unknown, Summary(inventory).Availability);
        Assert.Contains(RedfishFactsService.Collect(inventory, At), f => f.Key == "rf.bundle.warnings" && f.Sensitive);
    }

    [Theory]
    [InlineData("https://never-follow.invalid/URI-SECRET")]
    [InlineData("/redfish/v1/../PATH-SECRET")]
    [InlineData("/redfish/v1/Managers/A?token=TOKEN-SECRET")]
    public void 注入seam仍在URI與路徑檢查之後(string bad)
    {
        using var bundle = new Bundle();
        bundle.ManagerLinks([bad]);
        var inventory = bundle.Read();
        Assert.Single(bundle.Opened);
        Assert.Empty(inventory.Managers);
        Assert.True(inventory.ReadIncomplete);
        Assert.All(inventory.Warnings, w => Assert.DoesNotContain("SECRET", w));
        AssertReleased(bundle);
    }

    [Fact]
    public void 全球停止遍歷保留逐筆功耗但不能提升不完整scope的scalar()
    {
        using var bundle = new Bundle();
        bundle.Put(RootUri, """
        {"GeneratedAtUtc":"2026-11-17T07:00:00Z","Chassis":{"Members":[
          {"Id":"A","Power":{"PowerControl":[{"MemberId":"0","PowerConsumedWatts":20}]}},
          {"@odata.id":"/redfish/v1/Chassis/MISSING"}]}}
        """);
        var inventory = bundle.Read(attempts: 1);
        Assert.Single(inventory.PowerControls);
        Assert.False(inventory.PowerControlsComplete);
        Assert.Null(inventory.PowerConsumedWatts);
        Assert.Null(RedfishBundleService.SelectPowerConsumed(inventory).Watts);
        var facts = RedfishFactsService.Collect(inventory, At);
        Assert.Equal(20, facts.Single(f => f.Key.StartsWith("rf.item.power.", StringComparison.Ordinal)).NumericValue);
        Assert.Null(facts.Single(f => f.Key == "rf.power.consumed").NumericValue);
        AssertReleased(bundle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 機箱電力已完整後Systems才超限仍保留正確唯一scalar(bool aggregate)
    {
        using var bundle = new Bundle();
        const string system = "/redfish/v1/Systems/A";
        bundle.Put(RootUri, """
        {"GeneratedAtUtc":"2026-11-17T07:00:00Z","Chassis":{"Members":[
          {"Id":"A","Power":{"PowerControl":[{"MemberId":"0","PowerConsumedWatts":20}]}}]},
          "Systems":{"Members":[{"@odata.id":"/redfish/v1/Systems/A"}]}}
        """);
        bundle.Inject(system, () => new ScriptedStream(Padded("{\"Id\":\"SYS\"}", 128)));
        int rootBytes = File.ReadAllBytes(bundle.PathFor(RootUri)).Length;
        var inventory = aggregate ? bundle.Read(totalBytes: rootBytes + 16) : bundle.Read(attempts: 1);
        Assert.Empty(inventory.Systems);
        Assert.True(inventory.ReadIncomplete);
        Assert.True(inventory.PowerControlsComplete);
        Assert.Equal(20, inventory.PowerConsumedWatts);
        Assert.Equal(20, RedfishBundleService.SelectPowerConsumed(inventory).Watts);
        Assert.Equal(FactAvailability.Unknown, Summary(inventory).Availability);
        var scalar = RedfishFactsService.Collect(inventory, At).Single(f => f.Key == "rf.power.consumed");
        Assert.Equal(20, scalar.NumericValue);
        Assert.Equal(FactAvailability.Present, scalar.Availability);
        Assert.Contains(inventory.Warnings, warning => warning.Contains(aggregate ? "AggregateByteBudgetExceeded" : "LoadAttemptBudgetExceeded", StringComparison.Ordinal));
        AssertReleased(bundle);
    }

    [Fact]
    public void 公開CLI真實8MiB上限接線且保留成功項與隱私()
    {
        using var bundle = new Bundle();
        const string large = "/redfish/v1/Managers/URI-SECRET";
        bundle.ManagerLinks([ManagerUri, large]);
        bundle.Put(ManagerUri, "{\"Id\":\"A\",\"Model\":\"GOOD\"}");
        bundle.PutBytes(large, Padded("{\"Id\":\"REJECTED\"}", RedfishBundleService.MaxFileBytes + 1));
        var stdout = new StringWriter();
        Assert.Equal(CliService.ExitOk, CliService.Run(["--redfish", bundle.Root], () => [], stdout, new StringWriter()));
        string json = stdout.ToString();
        Assert.DoesNotContain("URI-SECRET", json);
        Assert.DoesNotContain(bundle.Root, json);
        Assert.DoesNotContain("REJECTED", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("GOOD", document.RootElement.GetProperty("managers")[0].GetProperty("model").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("counts").GetProperty("managers").GetInt32());
        Assert.Contains(document.RootElement.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("FileByteBudgetExceeded", StringComparison.Ordinal));
        var summary = document.RootElement.GetProperty("facts").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "rf.summary");
        Assert.Equal("Unknown", summary.GetProperty("availability").GetString());
        Assert.Contains("已讀部分", summary.GetProperty("value").GetString());
    }
}
