using XinSpect;

namespace XinSpect.Tests;

/// <summary>ACPI source fake that can fail immediately after a successful initial availability check.</summary>
internal sealed class ChangingAcpiTableSource(string reason, bool unavailableAfterRead, params byte[][] tables)
    : IAcpiTableSource
{
    private bool _available = true;

    public bool Available => _available;
    public string? UnavailableReason => reason;

    public IReadOnlyList<byte[]> ReadAll()
    {
        if (unavailableAfterRead) _available = false;
        return tables;
    }
}
