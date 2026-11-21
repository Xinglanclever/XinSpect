using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace XinSpect.Tests;

public sealed class DeepBenchRunStoreStructureTests : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "XinSpect-history-structure-" + Guid.NewGuid().ToString("N"));
    private string HistoryPath => System.IO.Path.Combine(_directory, "history.json");
    public DeepBenchRunStoreStructureTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private static DeepBenchRunRecord Record() => new(Guid.NewGuid(), DeepBenchRunProfile.Quick,
        DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(1), DeepBenchRunState.Completed,
        [new DeepBenchTestResult("test", Guid.Empty, DeepBenchRunProfile.Quick,
            DateTime.UnixEpoch, DateTime.UnixEpoch, "", [new DeepBenchMetric("m", "", "", true,
                "", [1, 2], [new DeepBenchMetricPoint(1, new Dictionary<string, string> { ["axis"] = "" }, [1])])],
            [""], [""], DeepBenchFailureKind.None, null)],
        [new DeepBenchInsight("", "", [""])]);

    [Theory]
    [InlineData("[{}]")]
    [InlineData("[{\"results\":[]}]")]
    [InlineData("[{\"insights\":[]}]")]
    public void MissingCollectionsAreRejectedBeforeHistoryRowConsumption(string json)
    {
        File.WriteAllText(HistoryPath, json);
        var store = new DeepBenchRunStore(HistoryPath);
        var vm = new DeepBenchViewModel(new CacheBenchService(), new MemBandwidthService(),
            new CoreLatencyService(), store);
        Assert.Empty(vm.History);
        Assert.NotEmpty(vm.Errors);
        Assert.NotNull(store.LastLoadError);
        var error = Assert.Throws<IOException>(() => store.Save(Record()));
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
        Assert.Equal(json, File.ReadAllText(HistoryPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    public static IEnumerable<object[]> NullMembers => new[]
    {
        "results", "insights", "results/0", "results/0/testId", "results/0/configuration",
        "results/0/metrics", "results/0/conditions", "results/0/conditions/0",
        "results/0/limitations", "results/0/limitations/0", "results/0/metrics/0",
        "results/0/metrics/0/id", "results/0/metrics/0/title", "results/0/metrics/0/unit",
        "results/0/metrics/0/configuration", "results/0/metrics/0/samples", "results/0/metrics/0/points",
        "results/0/metrics/0/points/0", "results/0/metrics/0/points/0/axes",
        "results/0/metrics/0/points/0/axes/axis", "results/0/metrics/0/points/0/samples",
        "insights/0", "insights/0/title", "insights/0/text", "insights/0/evidenceTestIds", "insights/0/evidenceTestIds/0"
    }.Select(member => new object[] { member });

    [Theory]
    [MemberData(nameof(NullMembers))]
    public void NullModelMembersCannotBeLoadedOrOverwritten(string member)
    {
        JsonNode node = JsonSerializer.SerializeToNode(new[] { Record() }, Options)!;
        SetNull(node[0]!, member);
        string json = node.ToJsonString(Options);
        File.WriteAllText(HistoryPath, json);
        byte[] before = File.ReadAllBytes(HistoryPath);
        var store = new DeepBenchRunStore(HistoryPath);
        Assert.Empty(store.LoadRecent());
        Assert.Contains("non-null", store.LastLoadError, StringComparison.Ordinal);
        var error = Assert.Throws<IOException>(() => store.Save(Record()));
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
        Assert.Equal(before, File.ReadAllBytes(HistoryPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void InvalidNewRecordCannotReplaceAnExistingValidHistory()
    {
        var store = new DeepBenchRunStore(HistoryPath);
        store.Save(Record());
        byte[] before = File.ReadAllBytes(HistoryPath);
        Assert.Throws<JsonException>(() => store.Save(Record() with { Results = null! }));
        Assert.Equal(before, File.ReadAllBytes(HistoryPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void MissingCollectionsBeyondDisplayWindowAlsoPreventOverwrite()
    {
        JsonArray node = (JsonArray)JsonSerializer.SerializeToNode(
            Enumerable.Range(0, 25).Select(_ => Record()).ToArray(), Options)!;
        node.Add(new JsonObject());
        string json = node.ToJsonString(Options);
        File.WriteAllText(HistoryPath, json);
        var store = new DeepBenchRunStore(HistoryPath);
        Assert.Empty(store.LoadRecent(20));
        Assert.Throws<IOException>(() => store.Save(Record()));
        Assert.Equal(json, File.ReadAllText(HistoryPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void EmptyFailureCancelledUnknownFieldsAndNumericEnumsRemainCompatible()
    {
        var empty = Record() with { SessionId = Guid.Empty, StartedUtc = default, EndedUtc = default,
            State = (DeepBenchRunState)93, Profile = (DeepBenchRunProfile)94, Results = [], Insights = [] };
        var failed = Record() with { State = DeepBenchRunState.Cancelled,
            Results = [Record().Results[0] with { FailureKind = DeepBenchFailureKind.Cancelled,
                Metrics = [], Conditions = [], Limitations = [], Error = null }], Insights = [] };
        JsonNode node = JsonSerializer.SerializeToNode(new[] { empty, failed }, Options)!;
        node[0]!["futureProperty"] = new JsonObject { ["unknown"] = true };
        File.WriteAllText(HistoryPath, node.ToJsonString(Options));
        var store = new DeepBenchRunStore(HistoryPath);
        var loaded = store.LoadRecent();
        Assert.Equal(2, loaded.Count);
        Assert.Equal((DeepBenchRunState)93, loaded[0].State);
        Assert.Equal((DeepBenchRunProfile)94, loaded[0].Profile);
        Assert.Empty(loaded[0].Results);
        Assert.Equal(DeepBenchRunState.Cancelled, loaded[1].State);
        Assert.Empty(loaded[1].Results[0].Metrics);
        Assert.Null(store.LastLoadError);
        store.Save(Record());
        Assert.Equal(3, store.LoadRecent().Count);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public void ExistingBomEncodedHistoryRemainsReadable(string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf8" => new UTF8Encoding(true),
            "utf16le" => new UnicodeEncoding(false, true),
            "utf16be" => new UnicodeEncoding(true, true),
            "utf32le" => new UTF32Encoding(false, true),
            _ => new UTF32Encoding(true, true)
        };
        var record = Record();
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(new[] { record }, Options), encoding);
        var store = new DeepBenchRunStore(HistoryPath);
        Assert.Equal(record.SessionId, Assert.Single(store.LoadRecent()).SessionId);
        Assert.Null(store.LastLoadError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualBytesEnforceBudgetWithoutLengthOrSeek(bool overflow)
    {
        const int budget = 64;
        byte[] payload = Encoding.UTF8.GetBytes("[]" + new string(' ', budget - 2 + (overflow ? 1 : 0)));
        var opened = new List<ShortReadStream>();
        var store = new DeepBenchRunStore(HistoryPath, _ =>
        {
            var source = new ShortReadStream(payload);
            opened.Add(source);
            return source;
        }, budget);
        Assert.Empty(store.LoadRecent());
        Assert.Equal(overflow, store.LastLoadError is not null);
        Assert.Equal(budget + (overflow ? 1 : 0), Assert.Single(opened).BytesRead);
        Assert.True(opened[0].Disposed);
        if (overflow)
        {
            File.WriteAllText(HistoryPath, "original bytes");
            var error = Assert.Throws<IOException>(() => store.Save(Record()));
            Assert.IsType<InvalidDataException>(error.InnerException);
            Assert.Equal("original bytes", File.ReadAllText(HistoryPath));
            Assert.Single(Directory.GetFiles(_directory));
        }
    }

    [Fact]
    public void OutputBudgetRefusesBeforeCreatingDirectoryOrTouchingHistory()
    {
        string path = System.IO.Path.Combine(_directory, "never-created", "history.json");
        var store = new DeepBenchRunStore(path, _ => throw new FileNotFoundException(), 64);
        Assert.Throws<InvalidDataException>(() => store.Save(Record()));
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(path)));
        File.WriteAllText(HistoryPath, "[]");
        var existing = new DeepBenchRunStore(HistoryPath, _ => new MemoryStream(Encoding.UTF8.GetBytes("[]")), 64);
        Assert.Throws<InvalidDataException>(() => existing.Save(Record()));
        Assert.Equal("[]", File.ReadAllText(HistoryPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputBudgetUsesExactUtf8BytesAndAcceptsTheBoundary(bool overflow)
    {
        var record = Record();
        record = record with { Results = [record.Results[0] with { Configuration = "中文😀" }] };
        string expected = JsonSerializer.Serialize(new[] { record }, Options);
        int byteCount = Encoding.UTF8.GetByteCount(expected);
        File.WriteAllText(HistoryPath, "[]");
        var store = new DeepBenchRunStore(HistoryPath,
            file => new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1),
            byteCount - (overflow ? 1 : 0));
        if (overflow)
        {
            Assert.Throws<InvalidDataException>(() => store.Save(record));
            Assert.Equal("[]", File.ReadAllText(HistoryPath));
        }
        else
        {
            store.Save(record);
            Assert.Equal(byteCount, File.ReadAllBytes(HistoryPath).Length);
            Assert.Equal(record.SessionId, Assert.Single(store.LoadRecent()).SessionId);
        }
        Assert.Single(Directory.GetFiles(_directory));
    }

    private static void SetNull(JsonNode root, string member)
    {
        string[] parts = member.Split('/');
        JsonNode node = root;
        foreach (string part in parts[..^1])
            node = node is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)]! : node[part]!;
        if (node is JsonArray parent) parent[int.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture)] = null;
        else node[parts[^1]] = null;
    }

    private sealed class ShortReadStream(byte[] payload) : Stream
    {
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("Length must not be consulted.");
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int actual = Math.Min(Math.Min(count, 3), payload.Length - BytesRead);
            Array.Copy(payload, BytesRead, buffer, offset, actual);
            BytesRead += actual;
            return actual;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
