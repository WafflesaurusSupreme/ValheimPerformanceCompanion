namespace Companion.Core;

internal static class CollectorBudget
{
    internal const long MaxTraceBytes = 5L * 1024 * 1024 * 1024;
    internal const long MinimumFreeBytes = 3L * 1024 * 1024 * 1024;
    internal const long PreflightFreeBytes = 20L * 1024 * 1024 * 1024;

    internal static void ValidatePreflight(long freeBytes)
    {
        if (freeBytes < PreflightFreeBytes)
            throw new IOException("ETW requires at least 20 GiB free before recording for live traces, merging and local analysis. No collection was started.");
    }

    internal static string? ResourceStopReason(long traceBytes, long freeBytes)
    {
        if (traceBytes >= MaxTraceBytes) return "trace-byte-limit";
        if (freeBytes < MinimumFreeBytes) return "free-space-reserve";
        return null;
    }

    internal static string QualityMessage(string reason, long traceBytes, long freeBytes) =>
        $"ETW stopped early: {reason}; live ETL bytes={traceBytes}; free bytes={freeBytes}; trace limit={MaxTraceBytes}; free-space reserve={MinimumFreeBytes}. Earlier events were retained without circular overwrite; later frames may lack CPU/wait evidence. Repeat a shorter capture.";
}
