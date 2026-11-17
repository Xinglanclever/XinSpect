using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

public sealed class DeepBenchRunStorePreservationTests : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "XinSpect-history-preservation-" + Guid.NewGuid().ToString("N"));
    private string HistoryPath => System.IO.Path.Combine(_directory, "history.json");
    public DeepBenchRunStorePreservationTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private static DeepBenchRunRecord Record(Guid? session = null) => new(session ?? Guid.NewGuid(),
        DeepBenchRunProfile.Quick, DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(1),
        DeepBenchRunState.Completed, [], []);

    [Theory]
    [InlineData("{corrupt")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    public void InvalidExistingHistoryCannotBecomeAnEmptyWriteBase(string json)
    {
        File.WriteAllText(HistoryPath, json);
        byte[] before = File.ReadAllBytes(HistoryPath);
        var store = new DeepBenchRunStore(HistoryPath);
        Assert.Empty(store.LoadRecent());
        Assert.Contains("JSON", store.LastLoadError, StringComparison.Ordinal);
        var failure = Assert.Throws<IOException>(() => store.Save(Record()));
        Assert.IsAssignableFrom<JsonException>(failure.InnerException);
        Assert.Equal(before, File.ReadAllBytes(HistoryPath));
        Assert.NotNull(store.LastLoadError);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadFailurePreservesExistingBytesAndOriginalCause(bool denied)
    {
        File.WriteAllText(HistoryPath, "existing history must survive");
        byte[] before = File.ReadAllBytes(HistoryPath);
        Exception original = denied ? new UnauthorizedAccessException("fixture denied")
            : new IOException("fixture temporarily unreadable");
        var store = new DeepBenchRunStore(HistoryPath, _ => throw original);
        var failure = Assert.Throws<IOException>(() => store.Save(Record()));
        Assert.Same(original, failure.InnerException);
        Assert.Equal(before, File.ReadAllBytes(HistoryPath));
        Assert.Contains(original.Message, store.LastLoadError, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void MissingParentIsFirstUseAndValidHistoryStillAppendsAndReplacesSession()
    {
        string path = System.IO.Path.Combine(_directory, "first-use", "history.json");
        var store = new DeepBenchRunStore(path);
        Assert.Empty(store.LoadRecent());
        Assert.Null(store.LastLoadError);
        var first = Record();
        var second = Record();
        store.Save(first);
        store.Save(second);
        store.Save(first with { State = DeepBenchRunState.Cancelled });
        var records = store.LoadRecent();
        Assert.Equal(2, records.Count);
        Assert.Equal(first.SessionId, records[0].SessionId);
        Assert.Equal(DeepBenchRunState.Cancelled, records[0].State);
        Assert.Equal(second.SessionId, records[1].SessionId);
        Assert.Null(store.LastLoadError);
    }

    [Fact]
    public void InvalidTailBeyondDisplayWindowIsNotSilentlyDiscardedOnSave()
    {
        string prefix = JsonSerializer.Serialize(Enumerable.Range(0, 25).Select(_ => Record()).ToArray(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        string json = prefix[..^1] + ",null]";
        File.WriteAllText(HistoryPath, json);
        var store = new DeepBenchRunStore(HistoryPath);
        Assert.Throws<IOException>(() => store.Save(Record()));
        Assert.Equal(json, File.ReadAllText(HistoryPath));
    }

    [Fact]
    public void OverBudgetExistingHistoryIsPreservedRatherThanTruncatedBeforeValidation()
    {
        string json = "[" + string.Join(",", Enumerable.Repeat("null", 101)) + "]";
        File.WriteAllText(HistoryPath, json);
        var store = new DeepBenchRunStore(HistoryPath);
        Assert.Empty(store.LoadRecent());
        Assert.Contains("100-record", store.LastLoadError, StringComparison.Ordinal);
        var error = Assert.Throws<IOException>(() => store.Save(Record()));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(json, File.ReadAllText(HistoryPath));
    }

    [Fact]
    public void ValidHundredRecordHistoryRetainsExistingRetentionPolicy()
    {
        var records = Enumerable.Range(0, 100).Select(_ => Record()).ToArray();
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(records,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var store = new DeepBenchRunStore(HistoryPath);
        var newest = Record();
        store.Save(newest);
        var loaded = store.LoadRecent(100);
        Assert.Equal(100, loaded.Count);
        Assert.Equal(newest.SessionId, loaded[0].SessionId);
        Assert.Equal(records[..99].Select(r => r.SessionId), loaded.Skip(1).Select(r => r.SessionId));
        Assert.DoesNotContain(loaded, r => r.SessionId == records[99].SessionId);
    }
}
