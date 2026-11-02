using CodeAlta.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Node.Tests;

/// <summary>
/// Temporary roots of a node: a profile directory, a home, a project and a folder of built-in skills, deleted with it.
/// The hosts it describes start no plugin, register no provider and stay off the network.
/// </summary>
internal sealed class NodeTestRoot : IDisposable
{
    internal NodeTestRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "CodeAlta-node-" + Guid.NewGuid().ToString("N"));
        DataRoot = Path.Combine(Root, "alta");
        Home = Path.Combine(Root, "home");
        ProjectRoot = Path.Combine(Root, "project");
        BuiltInSkillRoot = Path.Combine(Root, "builtin");
        foreach (var directory in new[] { DataRoot, Home, ProjectRoot, BuiltInSkillRoot }) Directory.CreateDirectory(directory);
        // The skill root is a git checkout of its own, as the shipped one is: discovery stops there.
        var git = Path.Combine(BuiltInSkillRoot, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "config"), "[core]\nignorecase = true\n");
    }

    internal string Root { get; }

    internal string DataRoot { get; }

    internal string Home { get; }

    internal string ProjectRoot { get; }

    internal string BuiltInSkillRoot { get; }

    internal CodeAltaInstanceProfile Profile(bool developer = false) => CodeAltaInstanceProfile.Create(DataRoot, developer);

    internal NodeHostOptions Options(bool developer = false) => Isolate(new NodeHostOptions { Profile = Profile(developer) });

    // What a test changes in the options of a node, whatever its profile.
    internal NodeHostOptions Isolate(NodeHostOptions options) => options with
    {
        CurrentProjectPath = ProjectRoot,
        StartPlugins = false,
        Offline = true,
        ConfigureModelProviders = static _ => { },
        DiscoveryScope = new SessionDiscoveryScope(Home, Root),
        BuiltInSkillRoot = BuiltInSkillRoot,
        DrainLimit = TimeSpan.FromSeconds(5),
    };

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
