using CodeAlta.Acp;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AcpCommandLocatorTests
{
    [TestMethod]
    public void FindCommandPath_OnWindows_SkipsTheShellScriptWithoutExtension()
    {
        // The folder of Node.js holds `npx` (a script for POSIX shells) beside `npx.cmd`: only the second one starts on Windows.
        var directory = Directory.CreateTempSubdirectory("codealta-acp-locator-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "npx"), "#!/bin/sh");
            File.WriteAllText(Path.Combine(directory, "npx.cmd"), "@echo off");

            Assert.AreEqual(Path.Combine(directory, "npx.cmd"), AcpCommandLocator.FindCommandPath("npx", directory, ".exe;.cmd", windows: true));
            Assert.AreEqual(Path.Combine(directory, "npx"), AcpCommandLocator.FindCommandPath("npx", directory, null, windows: false));
            Assert.AreEqual(Path.Combine(directory, "npx.cmd"), AcpCommandLocator.FindCommandPath("npx.cmd", directory, ".exe", windows: true));
            Assert.IsNull(AcpCommandLocator.FindCommandPath("uvx", directory, ".exe;.cmd", windows: true));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
