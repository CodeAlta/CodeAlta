using System.ComponentModel;
using System.Diagnostics;
using CodeAlta.Catalog.Storage;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Catalog.Tests;

// A repository in a temporary folder, with a bare repository beside it as its remote `origin`.
internal sealed class Checkout : IDisposable
{
    private Checkout(string home, string name)
    {
        Home = home;
        Root = Path.Combine(home, name);
    }

    public string Home { get; }
    public string Root { get; }

    /// <summary>
    /// The variables that keep the machine and the user out of git: no system or global configuration, no ignore file of
    /// the developer (~/.config/git/ignore, which git reads without any configuration), no repository above.
    /// </summary>
    public IReadOnlyDictionary<string, string> Isolation => new Dictionary<string, string>
    {
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_CONFIG_GLOBAL"] = Path.Combine(Home, "no-global-config"),
        ["HOME"] = Path.Combine(Home, "home"),
        ["XDG_CONFIG_HOME"] = Path.Combine(Home, "home", ".config"),
        // The submodules of these tests are folders: git clones them only when that is allowed.
        ["GIT_CONFIG_COUNT"] = "1",
        ["GIT_CONFIG_KEY_0"] = "protocol.file.allow",
        ["GIT_CONFIG_VALUE_0"] = "always",
        ["LC_ALL"] = "C",
        ["GIT_CEILING_DIRECTORIES"] = Path.GetDirectoryName(Home)!,
    };

    /// <summary>The ignore file of the user in this isolation: what core.excludesFile is by default.</summary>
    public string UserIgnoreFile => Path.Combine(Home, "home", ".config", "git", "ignore");

    /// <summary>Creates a repository named <paramref name="name"/> in a new folder of <paramref name="parent"/>, the temporary folder by default.</summary>
    public static Checkout Create(bool remote = true, bool init = true, string name = "repository", string? parent = null)
    {
        var home = Directory.CreateDirectory(Path.Combine(parent ?? Path.GetTempPath(), "CodeAlta-checkout-" + Guid.NewGuid().ToString("N"))).FullName;
        var checkout = new Checkout(home, name);
        if (!init) return checkout;
        Directory.CreateDirectory(checkout.Root);
        try
        {
            checkout.Git("init", "-q", "-b", "main");
        }
        catch (Win32Exception)
        {
            checkout.Dispose();
            Assert.Inconclusive("git is not installed.");
        }

        checkout.Configure(checkout.Root);
        checkout.Write("readme.md", "one");
        checkout.Commit("one");
        if (remote)
        {
            var bare = Path.Combine(checkout.Home, "remote.git");
            checkout.GitIn(checkout.Home, "init", "-q", "--bare", "-b", "main", bare);
            checkout.Git("remote", "add", "origin", bare.Replace('\\', '/'));
            checkout.Git("push", "-q", "-u", "origin", "main");
        }

        return checkout;
    }

    public Task<GitCheckoutState?> ReadAsync(string? folder = null, GitCheckoutScope scope = GitCheckoutScope.Repository)
        => new GitCheckoutStateReader(RunAsync).ReadAsync(folder ?? Root, scope, CancellationToken.None);

    public Task<CheckoutUsage> ScanAsync(string? folder = null)
        => new CheckoutUsageScanner(RunAsync).ScanAsync(folder ?? Root, CancellationToken.None);

    public string At(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(At(relative))!);
        File.WriteAllText(At(relative), content);
    }

    public void Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    /// <summary>Commits a submodule at <paramref name="path"/>, cloned from a repository beside this one, with one file.</summary>
    public void AddSubmodule(string path)
    {
        var source = CreateSource(path);
        Git("submodule", "add", "-q", source.Replace('\\', '/'), path);
        Configure(At(path));
        Commit("submodule " + path);
    }

    /// <summary>A repository beside this one, with one file named as the last segment of <paramref name="path"/>: what a submodule clones.</summary>
    public string CreateSource(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var source = Directory.CreateDirectory(Path.Combine(Home, path.Replace('/', '-') + "-source")).FullName;
        GitIn(source, "init", "-q", "-b", "main");
        Configure(source);
        File.WriteAllText(Path.Combine(source, name + ".txt"), name);
        GitIn(source, "add", "-A");
        GitIn(source, "commit", "-q", "-m", name);
        return source;
    }

    // Who commits, and nothing of the machine that changes files or signs.
    public void Configure(string folder)
    {
        GitIn(folder, "config", "user.name", "Test");
        GitIn(folder, "config", "user.email", "test@example.invalid");
        GitIn(folder, "config", "core.autocrlf", "false");
        GitIn(folder, "config", "commit.gpgsign", "false");
    }

    public string Git(params string[] arguments) => GitIn(Root, arguments);

    public string GitIn(string folder, params string[] arguments)
    {
        using var process = Process.Start(Start(folder, arguments))!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, $"git {string.Join(' ', arguments)}: {error.Result}");
        return output;
    }

    // What the scanner and the reader run, with nothing of the machine's or the user's configuration deciding the outcome. As
    // the runner of the application, it stops git after its timeout and never throws for a failure of git.
    public async Task<GitRun> RunAsync(string folder, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.Start(Start(folder, arguments))!;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return new(GitRun.NotStarted, string.Empty, exception.Message);
        }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                if (cancellationToken.IsCancellationRequested) throw;
                return new(GitRun.TimedOut, string.Empty, "git did not answer in time.");
            }

            return new(process.ExitCode, await output, await error);
        }
    }

    private ProcessStartInfo Start(string folder, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (name, value) in Isolation) start.Environment[name] = value;
        foreach (var inherited in (ReadOnlySpan<string>)["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_LITERAL_PATHSPECS",
                     "GIT_GLOB_PATHSPECS", "GIT_NOGLOB_PATHSPECS", "GIT_ICASE_PATHSPECS"])
            start.Environment.Remove(inherited);
        return start;
    }

    public void Dispose()
    {
        try
        {
            // Git marks its objects read-only. A link is removed without what it points to.
            foreach (var file in Directory.EnumerateFiles(Home, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Home, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temporary directory.
        }
    }
}
