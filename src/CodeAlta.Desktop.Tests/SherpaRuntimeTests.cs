using CodeAlta.Desktop.Speech;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SherpaRuntimeTests
{
    [TestMethod]
    public void Resolve_MovesWhatSpeechDownloadedBefore_IntoTheSharedFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = SpeechModelCatalog.FindEngine("osx-arm64")!;
            var speech = Path.Combine(root, "cache", "speech");
            var runtime = Path.Combine(root, "cache", "runtime");
            var shared = SherpaRuntime.DirectoryOf(runtime, engine);

            Assert.AreEqual(shared, SherpaRuntime.Resolve(runtime, speech, engine), "with nothing installed, the shared folder is where it will go");

            var legacy = SherpaRuntime.LegacyDirectoryOf(speech, engine);
            Directory.CreateDirectory(legacy);
            foreach (var library in engine.Libraries) File.WriteAllText(Path.Combine(legacy, library), "lib");
            File.WriteAllText(Path.Combine(legacy, SherpaRuntime.Marker), "{}");

            Assert.AreEqual(shared, SherpaRuntime.Resolve(runtime, speech, engine));
            Assert.IsTrue(SherpaRuntime.IsInstalled(shared, engine));
            Assert.IsFalse(Directory.Exists(legacy));
            Assert.AreEqual(Path.Combine(shared, "libonnxruntime.dylib"), SherpaRuntime.OnnxRuntimePathOf(shared, engine));
            Assert.AreEqual(shared, SherpaRuntime.Resolve(runtime, speech, engine), "once moved, it stays where it is");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
