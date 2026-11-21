using System.IO;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Deterministic stream growth regressions without filesystem races or hardware calls.</summary>
public sealed class HardwareSnapshotBoundedLoadTests
{
    private static byte[] ValidSnapshotJson()
    {
        var at = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        var snapshot = HardwareSnapshotService.Create("fixture", [new HardwareFact("fixture.bound", "fixture",
            "fixture", "trusted-value", "", "fixture-source", FactTrustLevel.Reported, false, at)], false);
        string path = Path.Combine(Path.GetTempPath(), "XinSpect-bound-" + Guid.NewGuid().ToString("N") + ".xinsnapshot");
        try
        {
            HardwareSnapshotService.Save(path, snapshot);
            return File.ReadAllBytes(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GrowingInputBeyondCap_IsRejectedAfterExactlyOneOverflowByte()
    {
        using var stream = new GrowingStream(ValidSnapshotJson(), HardwareSnapshotService.MaxFileBytes + 1_000);
        Assert.Throws<InvalidDataException>(() => HardwareSnapshotService.LoadFromStream(stream));
        Assert.Equal(HardwareSnapshotService.MaxFileBytes + 1L, stream.BytesRead);
        Assert.Equal(0, stream.LengthQueries);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void ExactlyMaximumBytes_WithValidJsonAndWhitespace_StillUsesTheOriginalValidator()
    {
        using var stream = new GrowingStream(ValidSnapshotJson(), HardwareSnapshotService.MaxFileBytes);
        var loaded = HardwareSnapshotService.LoadFromStream(stream);
        Assert.Equal("trusted-value", Assert.Single(loaded.Facts).Value);
        Assert.Equal(HardwareSnapshotService.MaxFileBytes, stream.BytesRead);
        Assert.Equal(0, stream.LengthQueries);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void NonSeekableShortReads_DoNotChangeHashOrUnitSemantics()
    {
        byte[] json = ValidSnapshotJson();
        using var stream = new GrowingStream(json, json.Length, maximumChunk: 13);
        var result = HardwareSnapshotService.LoadFromStream(stream);
        Assert.Equal("", Assert.Single(result.Facts).Unit);
        Assert.Equal("SHA-256", result.Integrity.Algorithm);
        Assert.Equal(json.Length, stream.BytesRead);
        Assert.Equal(0, stream.LengthQueries);
    }

    [Fact]
    public void BoundedStreamLoad_RejectsTamperingAndMalformedJsonThroughSharedValidation()
    {
        string json = Encoding.UTF8.GetString(ValidSnapshotJson());
        using var tampered = new MemoryStream(Encoding.UTF8.GetBytes(json.Replace("trusted-value", "modified-value", StringComparison.Ordinal)));
        var error = Assert.Throws<InvalidDataException>(() => HardwareSnapshotService.LoadFromStream(tampered));
        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.Contains("不是簽章", error.Message, StringComparison.Ordinal);
        using var malformed = new MemoryStream(Encoding.UTF8.GetBytes("{"));
        Assert.Throws<InvalidDataException>(() => HardwareSnapshotService.LoadFromStream(malformed));
        using var empty = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => HardwareSnapshotService.LoadFromStream(empty));
    }

    private sealed class GrowingStream(byte[] prefix, long totalBytes, int maximumChunk = int.MaxValue) : Stream
    {
        public long BytesRead { get; private set; }
        public int LengthQueries { get; private set; }
        private bool _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get { LengthQueries++; throw new NotSupportedException("Length is not reliable input evidence"); } }
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytes = (int)Math.Min(Math.Min(totalBytes - BytesRead, count), maximumChunk);
            if (bytes <= 0) return 0;
            Array.Fill(buffer, (byte)' ', offset, bytes);
            if (BytesRead < prefix.Length)
            {
                int copied = Math.Min(bytes, prefix.Length - (int)BytesRead);
                Array.Copy(prefix, (int)BytesRead, buffer, offset, copied);
            }
            BytesRead += bytes;
            return bytes;
        }
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
