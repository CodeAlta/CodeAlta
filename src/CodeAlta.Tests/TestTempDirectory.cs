namespace CodeAlta.Tests;

internal sealed class TestTempDirectory(string path) : IDisposable
{
    // A process seen exited can still hold the folder as its working directory until Windows finishes tearing it down
    // (more than 100 ms for a killed shell on a loaded machine); a folder still held after this long is a leak, and the
    // deletion fails with its error.
    private static readonly TimeSpan ReleaseDeadline = TimeSpan.FromSeconds(10);

    public string Path { get; } = path;

    public static TestTempDirectory Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codealta-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new TestTempDirectory(path);
    }

    public void Dispose()
    {
        var deadline = Environment.TickCount64 + (long)ReleaseDeadline.TotalMilliseconds;
        while (true)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }
}
