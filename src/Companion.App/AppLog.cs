using System.IO;

namespace Companion.App;

internal static class AppLog
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValheimPerformanceCompanion", "Logs");
    public static void State(string message)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(Path.Combine(DirectoryPath, $"startup-{Environment.ProcessId}.log"), $"{DateTimeOffset.UtcNow:O} {message}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine(ex.Message); }
    }
    public static void Write(string operation, Exception error)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            string path = Path.Combine(DirectoryPath, "error-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".txt");
            File.WriteAllText(path, $"{DateTimeOffset.UtcNow:O} · {operation}\n{error}");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.WriteLine("Could not save application diagnostic: " + logError.Message); }
    }
}
