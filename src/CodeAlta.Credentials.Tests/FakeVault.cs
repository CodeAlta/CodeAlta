using System.Text.Json.Nodes;

namespace CodeAlta.Credentials.Tests;

/// <summary>A folder with the fake command line of a vault under the name of the program it plays, its state, and a profile root.</summary>
internal sealed class FakeVault : IDisposable
{
    private const string Fake = "CodeAlta.Tests.FakeVaultCli";

    public FakeVault(string program = "op")
    {
        Root = Path.Combine(Path.GetTempPath(),"codealta-credentials-" + Guid.NewGuid().ToString("N"));
        Bin = Path.Combine(Root, "bin");
        Profile = Path.Combine(Root, "profile");
        Directory.CreateDirectory(Bin);
        Directory.CreateDirectory(Profile);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, Fake + ".*"))
        {
            File.Copy(file, Path.Combine(Bin, Path.GetFileName(file)));
        }

        var apphost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? Fake + ".exe" : Fake);
        if (!File.Exists(apphost)) Assert.Inconclusive($"The fake vault program is missing: {apphost}");
        ProgramPath = Path.Combine(Bin, OperatingSystem.IsWindows() ? program + ".exe" : program);
        File.Copy(apphost, ProgramPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ProgramPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        State = new JsonObject
        {
            ["accounts"] = true,
            ["signedIn"] = false,
            ["approve"] = true,
            ["vaults"] = new JsonArray(new JsonObject
            {
                ["id"] = "v1",
                ["name"] = "Private",
                ["items"] = new JsonArray(new JsonObject
                {
                    ["id"] = "i1",
                    ["title"] = "Anthropic",
                    ["category"] = "API_CREDENTIAL",
                    ["fields"] = new JsonArray(
                        new JsonObject { ["id"] = "credential", ["label"] = "credential", ["type"] = "CONCEALED", ["value"] = "sk-ant-fake-value-1234" },
                        new JsonObject { ["id"] = "username", ["label"] = "username", ["type"] = "STRING", ["value"] = "alice" }),
                }),
            }),
        };
        Save();
    }

    public string Root { get; }

    public string Bin { get; }

    public string Profile { get; }

    public string ProgramPath { get; }

    public JsonObject State { get; }

    public string ConfigPath => Path.Combine(Profile, "config.toml");

    public void Save() => File.WriteAllText(Path.Combine(Bin, "fake-vault.json"), State.ToJsonString());

    public void Reload()
    {
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(Bin, "fake-vault.json")))!.AsObject();
        foreach (var (key, value) in saved) State[key] = value?.DeepClone();
    }

    public IReadOnlyList<string> Calls()
    {
        var log = Path.Combine(Bin, "fake-vault.log");
        return File.Exists(log) ? File.ReadAllLines(log) : [];
    }

    public void WriteConfig(string toml) => File.WriteAllText(ConfigPath, toml);

    public string TomlPath => ProgramPath.Replace("\\", "\\\\", StringComparison.Ordinal);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A program still runs: the folder of the system cleans it later.
        }
    }
}
