using System.Diagnostics;

// Isolated ETW workload, never a game executable or a distributed application component.
string path = Path.Combine(Path.GetTempPath(), "VpcCollectorWorkload-" + Guid.NewGuid().ToString("N") + ".dat");
byte[] bytes = new byte[1024 * 1024];
new Random(7123).NextBytes(bytes);
using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.WriteThrough);
var elapsed = Stopwatch.StartNew();
long work = 0;
while (elapsed.Elapsed < TimeSpan.FromMinutes(5))
{
    for (int i = 0; i < 300000; i++) work ^= (long)Math.Sqrt(i + Math.Abs(work % 100) + 100);
    file.Position = 0; file.Write(bytes); file.Flush(true);
    Thread.Sleep(5);
}
Console.WriteLine(work);
