using CodeAlta.CodexSdk.Generator;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodexSdkGeneratorTests
{
    [TestMethod]
    public async Task OutputDirectoryCleaner_CanDeleteReadOnlyFiles()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"CodeAlta.Tests.{Guid.NewGuid():N}");
        var output = Path.Combine(root, "generated");
        Directory.CreateDirectory(output);

        var filePath = Path.Combine(output, "read-only.gen.cs");
        await File.WriteAllTextAsync(filePath, "// test").ConfigureAwait(false);
        File.SetAttributes(filePath, FileAttributes.ReadOnly);

        try
        {
            await OutputDirectoryCleaner.CleanAsync(output).ConfigureAwait(false);
            Assert.IsFalse(Directory.Exists(output));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}

