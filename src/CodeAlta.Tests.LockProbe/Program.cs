using System.Globalization;
using System.Text;
using CodeAlta.Hosting;

// Linked-source qualification, not the shipped Hosting binary. Explicit-purpose, BCL-only helper.
// No application, test-assembly, profile or provider startup.
return LockProbe.Run(args);

internal static class LockProbe
{
    internal static int Run(string[] arguments)
    {
        Options options;
        try
        {
            options = Parse(arguments);
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("ERROR arguments");
            return 2;
        }

        // Covers blocking stdin, stdout and synchronous guard/file operations, not just async waits.
        // Hard exit deliberately leaves crash-style recovery to the OS. The parent also owns a deadline.
        using var watchdog = new Timer(static _ => Environment.Exit(124), null, options.DeadlineMs, Timeout.Infinite);
        try
        {
            ValidatePaths(options);
            Reply("READY", options.Nonce);
            Expect("BEGIN");
            ValidatePaths(options);

            var (lease, pid) = Acquire(options);
            using (lease)
            {
                Reply(lease is null ? "BUSY" : "ACQUIRED", options.Nonce, lease is null ? pid : null);
                Expect("RELEASE");
            }

            // Only acknowledge successful release, including a busy probe's empty ownership scope.
            Reply("RELEASED", options.Nonce);
            return lease is null ? 10 : 0;
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("ERROR arguments");
            return 2;
        }
        catch (InvalidDataException)
        {
            Console.Error.WriteLine("ERROR protocol");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("ERROR unexpected " + error.GetType().Name);
            return 1;
        }
    }

    private sealed record Options(string Root, string LockPath, string Mode, int DeadlineMs, string Nonce);

    private static Options Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments.Length != 10) throw new ArgumentException("Five named arguments are mandatory.");
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var key = arguments[index];
            if (key is not ("--task-root" or "--lock-path" or "--mode" or "--deadline-ms" or "--nonce") ||
                !values.TryAdd(key, arguments[index + 1]))
                throw new ArgumentException("Unknown or duplicate argument.");
        }

        var root = CanonicalAbsolutePath(values["--task-root"]);
        var path = CanonicalAbsolutePath(values["--lock-path"]);
        if (!string.Equals(path, Path.Combine(root, "alta.lock"), StringComparison.Ordinal))
            throw new ArgumentException("Lock must be the exact direct child alta.lock.");
        var mode = values["--mode"];
        if (mode is not ("guard" or "persistent-probe")) throw new ArgumentException("Invalid mode.");
        if (!int.TryParse(values["--deadline-ms"], NumberStyles.None, CultureInfo.InvariantCulture, out var deadline) ||
            deadline is < 1000 or > 30000)
            throw new ArgumentException("Deadline must be between 1000 and 30000 milliseconds.");
        var nonce = values["--nonce"];
        if (nonce.Length is < 16 or > 64 || nonce.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid nonce.");
        return new Options(root, path, mode, deadline, nonce);
    }

    private static string CanonicalAbsolutePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("An explicit local absolute path is required.");
        var full = Path.GetFullPath(value);
        if (!string.Equals(value, Path.TrimEndingDirectorySeparator(full), StringComparison.Ordinal))
            throw new ArgumentException("Noncanonical path.");
        var relative = full[Path.GetPathRoot(full)!.Length..];
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':'))
                throw new ArgumentException("Ambiguous path component.");
            if (OperatingSystem.IsWindows())
            {
                var stem = part.Split('.')[0].ToUpperInvariant();
                if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'))
                    throw new ArgumentException("Device path component.");
            }
        }

        return full;
    }

    private static void ValidatePaths(Options options)
    {
        var root = new DirectoryInfo(options.Root);
        if (!root.Exists || root.Parent is null) throw new ArgumentException("Root must already exist below a volume root.");
        for (DirectoryInfo? current = root; current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget is not null)
                throw new ArgumentException("Linked roots are not supported.");
        }

        try
        {
            var attributes = File.GetAttributes(options.LockPath);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || new FileInfo(options.LockPath).LinkTarget is not null)
                throw new ArgumentException("Lock path must not be a link or directory.");
        }
        catch (FileNotFoundException)
        {
            if (new FileInfo(options.LockPath).LinkTarget is not null) throw new ArgumentException("Dangling lock link.");
        }

        // These checks are not atomic with open. Hard links, aliases, external namespace replacement
        // and network filesystems are not qualified. Caller must exclusively own the scenario directory.
    }

    private static (IDisposable? Lease, string Pid) Acquire(Options options)
    {
        if (options.Mode == "guard")
        {
            // BUSY describes the linked guard's rejection, not proven live ownership. Its internal
            // fail-closed PID/read/delete suppressions remain unchanged and are not observable here.
            try
            {
                return (CodeAltaSingleInstanceGuard.Acquire(options.LockPath), "unknown");
            }
            catch (CodeAltaAlreadyRunningException error) when (error.InnerException is IOException io && IsContention(io, creatingNew: true))
            {
                return (null, error.ProcessId is > 0 ? error.ProcessId.Value.ToString(CultureInfo.InvariantCulture) : "unknown");
            }
        }

        // Experimental primitive only, NOT an adopted production algorithm. Never unlink this file.
        FileStream stream;
        try
        {
            stream = new FileStream(options.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when (IsContention(error, creatingNew: false))
        {
            return (null, "unknown");
        }

        try
        {
            stream.SetLength(0);
            stream.Write(Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n"));
            stream.Flush(flushToDisk: true);
            return (stream, "unknown");
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool IsContention(IOException error, bool creatingNew)
    {
        // Only known open/create contention codes. Permissions, disk errors and unrecognized runtime
        // encodings fail distinctly; do not classify every IOException (or every guard exception) BUSY.
        var code = error.HResult & 0xFFFF;
        // .NET can map Unix sharing conflicts to the Win32-shaped sharing-violation HRESULT too.
        return code == 32 || (OperatingSystem.IsWindows()
            ? code == 33 || (creatingNew && code is 80 or 183)
            : code == (OperatingSystem.IsMacOS() ? 35 : 11) || (creatingNew && code == 17));
    }

    private static void Expect(string command)
    {
        var line = new StringBuilder();
        while (true)
        {
            var character = Console.In.Read();
            if (character < 0) throw new InvalidDataException("Protocol EOF.");
            if (character == '\n') break;
            if (line.Length == 128) throw new InvalidDataException("Protocol line too long.");
            line.Append((char)character);
        }

        if (line.Length > 0 && line[^1] == '\r') line.Length--;
        if (!string.Equals(line.ToString(), command, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected command.");
    }

    private static void Reply(string status, string nonce, string? diagnostic = null)
    {
        Console.Out.WriteLine(status + " " + nonce + (diagnostic is null ? "" : " " + diagnostic));
        Console.Out.Flush();
    }
}
