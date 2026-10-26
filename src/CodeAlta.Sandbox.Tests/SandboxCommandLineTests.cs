namespace CodeAlta.Sandbox.Tests;

[TestClass]
public sealed class SandboxCommandLineTests
{
    [TestMethod]
    public void OnWindows_EachArgumentReachesTheProgramAsItIs()
    {
        Assert.AreEqual("\"C:\\Program Files\\claude.exe\" -p plain \"two words\" \"\"",
            SandboxCommandLine.CreateForWindows("C:\\Program Files\\claude.exe", ["-p", "plain", "two words", ""]));
        // Quotes are escaped, and the backslashes before them doubled; other backslashes stay as they are.
        Assert.AreEqual("\"x\" \"{\\\"a\\\":\\\"b\\\"}\" C:\\dir\\ \"C:\\my dir\\\\\" \"a\\\\\\\"b\"",
            SandboxCommandLine.CreateForWindows("x", ["{\"a\":\"b\"}", "C:\\dir\\", "C:\\my dir\\", "a\\\"b"]));
    }

    [TestMethod]
    public void ForAShell_EachArgumentIsQuotedWhole()
    {
        Assert.AreEqual("'/usr/bin/claude' '--mcp-config' '{\"a\":\"$HOME\"}' 'it'\\''s'",
            SandboxCommandLine.CreateForShell("/usr/bin/claude", ["--mcp-config", "{\"a\":\"$HOME\"}", "it's"]));
    }

    [TestMethod]
    public void OnWindows_TheLineSplitsBackIntoTheSameArguments()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("The rules of CommandLineToArgvW are those of Windows.");
        string[] arguments = ["--mcp-config", "{\"mcpServers\":{\"codealta\":{\"type\":\"sdk\",\"name\":\"codealta\"}}}", "C:\\a b\\", "x\\\"y", "", "plain"];

        var split = Split(SandboxCommandLine.CreateForWindows("C:\\tools\\claude.exe", arguments));

        CollectionAssert.AreEqual(new[] { "C:\\tools\\claude.exe" }.Concat(arguments).ToArray(), split);
    }

    private static string[] Split(string line)
    {
        var pointer = CommandLineToArgvW(line, out var count);
        try
        {
            var result = new string[count];
            for (var index = 0; index < count; index++)
                result[index] = System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
