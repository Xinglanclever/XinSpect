namespace XinSpect;

/// <summary>Ensures nested implementation calls consume one explicit cat confirmation per user operation.</summary>
internal static class CatOperationScope
{
    private static readonly AsyncLocal<int> Depth = new();

    public static IDisposable? TryBegin(Func<bool> confirm)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        int prior = Depth.Value;
        if (prior == 0 && !confirm()) return null;
        Depth.Value = prior + 1;
        return new Scope(prior);
    }

    private sealed class Scope(int prior) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Depth.Value = prior;
        }
    }
}
