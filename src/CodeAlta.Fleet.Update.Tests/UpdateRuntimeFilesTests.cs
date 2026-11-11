namespace CodeAlta.Fleet.Update.Tests;

/// <summary>
/// The files a running node or server loads its code from (<see cref="UpdateRuntimeFiles"/>): a package manager that removes
/// the folder of its .NET runtime, or <c>dotnet tool update</c> that removes its application, is seen, the runtime first.
/// </summary>
[TestClass]
public sealed class UpdateRuntimeFilesTests
{
    [TestMethod]
    public void AGoneRuntimeOrApplicationIsSeen_TheRuntimeFirst()
    {
        var root = Directory.CreateTempSubdirectory("codealta-runtime-files-");
        try
        {
            var runtime = Directory.CreateDirectory(Path.Combine(root.FullName, "shared", "Microsoft.NETCore.App", "10.0.14")).FullName;
            File.WriteAllText(Path.Combine(runtime, "System.Private.CoreLib.dll"), "");
            var application = Directory.CreateDirectory(Path.Combine(root.FullName, "tools", "4.2.0")).FullName;
            var files = new UpdateRuntimeFiles(runtime + Path.DirectorySeparatorChar, application);
            Assert.IsNull(files.Missing(), "both are there");

            Directory.Delete(application);
            Assert.AreEqual(application, files.Missing(), "dotnet tool update removed the previous version");

            // apt installs 10.0.15 beside it and removes 10.0.14: a folder left without its core library is gone as well.
            File.Delete(Path.Combine(runtime, "System.Private.CoreLib.dll"));
            Assert.AreEqual(runtime, files.Missing());
            Directory.Delete(runtime);
            Assert.AreEqual(runtime, files.Missing(), "the runtime is said first");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void ThisProcessHasItsFiles() => Assert.IsNull(UpdateRuntimeFiles.OfThisProcess().Missing());
}
