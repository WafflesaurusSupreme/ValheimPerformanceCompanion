using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Companion.Transport;

namespace Companion.Core;

public sealed class RecorderStatus
{
    public int Protocol { get; set; }
    public int ProcessId { get; set; }
    public string GameDirectory { get; set; } = "";
    public string BepInExRoot { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Directory { get; set; } = "";
    public string State { get; set; } = "";
    public string Error { get; set; } = "";
    public string LastError { get; set; } = "";
    public long Frames { get; set; }
    public long DroppedFrames { get; set; }
    public double LastMs { get; set; }
    public double ElapsedSeconds { get; set; }
    public bool LegacyRecorder { get; set; }
    public bool SupportsPreparation { get; set; }
}
public static class RecorderClient
{
    public static async Task<RecorderStatus> SendAsync(int pid, string command, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var pipe = new NamedPipeClientStream(".", "ValheimPerformanceCompanion.Recorder." + pid, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500, timeout.Token).ConfigureAwait(false);
        WindowsPipe.VerifyServer(pipe, pid);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
        // Pipes are unbuffered. Avoid a writer's disposal-time flush masking a lost reply
        // with a second broken-pipe exception after the peer has already disconnected.
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(command + Environment.NewLine), timeout.Token).ConfigureAwait(false);
        string response = await ReadBoundedAsync(reader, 16384, timeout.Token).ConfigureAwait(false);
        RecorderStatus status = JsonSerializer.Deserialize<RecorderStatus>(response) ?? throw new InvalidDataException("Recorder sent an empty response.");
        if (status.Protocol != 1 || status.ProcessId != pid) throw new InvalidDataException("Recorder protocol or process identity does not match.");
        if (status.Error.Length > 0) throw new InvalidOperationException(status.Error);
        return status;
    }
    public static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellation)
    {
        var text = new StringBuilder(); var character = new char[1];
        while (text.Length < limit)
        {
            int count = await reader.ReadAsync(character.AsMemory(), cancellation).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("The peer closed before sending a complete response.");
            if (character[0] == '\n') return text.ToString().TrimEnd('\r');
            text.Append(character[0]);
        }
        throw new InvalidDataException("Control message exceeds the protocol limit.");
    }
}
