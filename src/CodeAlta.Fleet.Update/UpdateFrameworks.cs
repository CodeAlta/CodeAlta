using System.Text.Json;

namespace CodeAlta.Fleet.Update;

/// <summary>
/// Whether the shared frameworks a staged version declares in its <c>runtimeconfig.json</c> are installed on the machine it will
/// run on. The packages are framework-dependent: a version that needs a framework the machine lacks (CodeAlta Node 4.5 and
/// <c>Microsoft.AspNetCore.App</c>, for its phone page) is stopped by the .NET host before any of its code runs. The launcher would
/// then see a version that never confirmed, roll it back and mark it bad, and the owner would learn nothing of why. Checked at
/// staging, from the running version, which can still say what to install.
/// </summary>
internal static class UpdateFrameworks
{
    /// <summary>
    /// Gets the first framework of a staged application that the .NET installation lacks, as <c>Microsoft.AspNetCore.App 10.0.0</c>;
    /// null when all are installed, or when nothing can be checked (no <c>runtimeconfig.json</c>, a process that does not run on a
    /// shared runtime). A framework is installed when a version of the same major, not older, is: the roll-forward of the host.
    /// </summary>
    /// <param name="applicationDirectory">The folder of the staged application.</param>
    /// <param name="product">The product, whose <c>runtimeconfig.json</c> is read.</param>
    /// <param name="dotnetRoot">The .NET installation the version runs on; null for none known.</param>
    /// <returns>The framework that is missing, or null.</returns>
    internal static string? FirstMissing(string applicationDirectory, UpdateProduct product, string? dotnetRoot)
    {
        var path = Path.Combine(applicationDirectory, product.AssemblyName + ".runtimeconfig.json");
        if (dotnetRoot is null || !File.Exists(path)) return null;
        foreach (var (name, version) in Declared(path))
        {
            if (!Version.TryParse(Core(version), out var required)) continue;
            var installed = Path.Combine(dotnetRoot, "shared", name);
            var found = Directory.Exists(installed) && Directory.EnumerateDirectories(installed).Select(static folder => Path.GetFileName(folder))
                .Any(candidate => Version.TryParse(Core(candidate), out var have) && have.Major == required.Major && have >= required);
            if (!found) return $"{name} {version}";
        }

        return null;
    }

    private static IEnumerable<(string Name, string Version)> Declared(string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("runtimeOptions", out var options) || options.ValueKind != JsonValueKind.Object) yield break;
            // A single "framework", or the list "frameworks" a project with several shared frameworks gets.
            var frameworks = new List<JsonElement>();
            if (options.TryGetProperty("frameworks", out var list) && list.ValueKind == JsonValueKind.Array) frameworks.AddRange(list.EnumerateArray());
            if (options.TryGetProperty("framework", out var single) && single.ValueKind == JsonValueKind.Object) frameworks.Add(single);
            foreach (var framework in frameworks)
            {
                if (framework.ValueKind == JsonValueKind.Object && framework.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    && framework.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } text && version.GetString() is { Length: > 0 } number)
                {
                    yield return (text, number);
                }
            }
        }
    }

    // "10.0.0-rc.2.25502.107" is 10.0.0: a preview of a version counts for it.
    private static string Core(string version) => version.Split('-', '+')[0];
}
