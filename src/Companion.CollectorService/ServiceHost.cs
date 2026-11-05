using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Companion.Collector;

internal static class ServiceHost
{
    private delegate void ServiceMain(int count, nint arguments);
    private delegate uint Handler(uint control, uint type, nint data, nint context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry { [MarshalAs(UnmanagedType.LPWStr)] internal string? Name; internal ServiceMain? Main; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    { internal uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    private static readonly CancellationTokenSource stopping = new();
    private static readonly Handler handler = Control;
    private static nint handle;
    private static int result;
    private static Status status = new() { Type = 0x10, State = 2, WaitHint = 180000 };

    internal static int Run(Func<CancellationToken, Task> run)
    {
        ServiceMain main = (_, _) =>
        {
            handle = RegisterServiceCtrlHandlerEx(CollectorProtocol.ServiceName, handler, 0);
            if (handle == 0) { result = Marshal.GetLastWin32Error(); return; }
            try
            {
                Report(2, 0);
                Task work = run(stopping.Token);
                if (work.IsCompleted) work.GetAwaiter().GetResult();
                Report(4, 1 | 4);
                // SCM's native callback is synchronous; async work itself never blocks the desktop.
                while (!work.IsCompleted)
                {
                    if (stopping.IsCancellationRequested) Report(3, 0);
                    Thread.Sleep(1000);
                }
                work.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception error)
            {
                result = 1;
                // SCM receives a failure exit code; do not display a Session 0 dialog.
                ReportError(error);
            }
            finally
            {
                status.Win32Exit = (uint)result;
                try { Report(1, 0); }
                catch (Win32Exception error) { result = error.NativeErrorCode; ReportError(error); }
            }
        };
        var table = new[] { new Entry { Name = CollectorProtocol.ServiceName, Main = main }, new Entry() };
        if (!StartServiceCtrlDispatcher(table)) return Marshal.GetLastWin32Error();
        GC.KeepAlive(main); GC.KeepAlive(handler);
        return result;
    }

    private static void ReportError(Exception error)
    {
        nint source = RegisterEventSource(null, "Valheim Performance Companion Collector");
        if (source == 0) { System.Diagnostics.Trace.TraceError(error.ToString()); return; }
        try
        {
            if (!ReportEvent(source, 1, 0, 1001, 0, 1, 0, new[] { error.ToString() }, 0))
                System.Diagnostics.Trace.TraceError(error.ToString());
        }
        finally { DeregisterEventSource(source); }
    }

    private static uint Control(uint control, uint type, nint data, nint context)
    {
        if (control is 1 or 5) stopping.Cancel();
        return 0;
    }
    private static void Report(uint state, uint accepted)
    {
        status.State = state; status.Accepted = accepted;
        status.Checkpoint = state is 2 or 3 ? status.Checkpoint + 1 : 0;
        status.WaitHint = state is 2 or 3 ? 180000u : 0;
        if (!SetServiceStatus(handle, ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcher([In] Entry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint RegisterServiceCtrlHandlerEx(string name, Handler callback, nint context);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceStatus(nint handle, ref Status status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint RegisterEventSource(string? server, string source);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ReportEvent(nint source, ushort type, ushort category, uint eventId, nint sid, ushort strings, uint dataSize, string[] text, nint data);
    [DllImport("advapi32.dll")] private static extern bool DeregisterEventSource(nint source);
}
