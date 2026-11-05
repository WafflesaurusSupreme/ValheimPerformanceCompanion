using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Companion.Collector;

internal sealed record ServiceRequest(int Protocol, string Command, string SessionId, int GamePid, int OwnerPid, int Seconds, string Mode);
internal sealed record ServiceReply(bool Success, string Message, string State, string? Directory = null);

internal static class CollectorProtocol
{
    internal const string PipeName = "ValheimPerformanceCompanion.Collector.v1";
    internal const string ServiceName = "ValheimPerformanceCompanionCollector";
    internal const int MaximumMessageBytes = 16384;
    internal static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static void Validate(ServiceRequest request)
    {
        if (request.Protocol != 1 || request.Command is not ("start" or "begin" or "stop" or "status" or "release")
            || !Guid.TryParseExact(request.SessionId, "N", out _) || request.GamePid <= 0 || request.OwnerPid <= 0
            || request.Seconds is < 5 or > 600 || request.Mode is not ("Standard" or "Deep"))
            throw new InvalidDataException("Invalid collector service request.");
    }

    internal static string SessionDirectory(string userSid, string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _) || !userSid.StartsWith("S-1-", StringComparison.Ordinal)
            || userSid.Any(c => c != 'S' && c != '-' && !char.IsAsciiDigit(c)))
            throw new InvalidDataException("Invalid collector session identity.");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ValheimPerformanceCompanion", "Collector", userSid, sessionId);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellation)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Collector message exceeds its size limit.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellation).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidDataException("Empty collector message.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellation)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (body.Length is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Collector message exceeds its size limit.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    internal static async Task<ServiceReply> ReadReplyAsync(Stream stream, CancellationToken cancellation)
    {
        ServiceReply reply = await ReadAsync<ServiceReply>(stream, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 6 }, cancellation).ConfigureAwait(false);
        return reply;
    }

    internal static async Task WriteReplyAsync(Stream stream, ServiceReply reply, CancellationToken cancellation)
    {
        await WriteAsync(stream, reply, cancellation).ConfigureAwait(false);
        // DisconnectNamedPipe discards unread outbound data. Acknowledge consumption before recycling the instance.
        byte[] acknowledgement = new byte[1];
        await stream.ReadExactlyAsync(acknowledgement, cancellation).ConfigureAwait(false);
        if (acknowledgement[0] != 6) throw new InvalidDataException("Invalid collector reply acknowledgement.");
    }
}
