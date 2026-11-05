using System.Text.Json;
using Companion.Core;

namespace Companion.Collector;

internal static class CollectorStaging
{
    internal static T ReadReceipt<T>(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Collector receipt is missing, linked or exceeds its size limit.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > 16384) throw new InvalidDataException("Collector receipt exceeds its size limit.");
        byte[] data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        return JsonSerializer.Deserialize<T>(data, CollectorProtocol.Json)
            ?? throw new InvalidDataException("Collector receipt is empty.");
    }

    internal static void ValidateBinding(CollectorRequest stored, CollectorRequest expected, string directory)
    {
        if (!Guid.TryParseExact(stored.SessionId, "N", out _) || stored.SessionId != expected.SessionId
            || stored.GamePid != expected.GamePid || stored.OwnerPid != expected.OwnerPid
            || stored.Seconds != expected.Seconds || stored.Mode != expected.Mode
            || stored.Mode is not ("Standard" or "Deep") || stored.Seconds is < 5 or > 600
            || stored.GamePid <= 0 || stored.OwnerPid <= 0
            || !string.Equals(stored.Directory, directory, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Retained collector data does not match the requested session.");
    }
}
