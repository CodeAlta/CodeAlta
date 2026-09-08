using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Tests;

/// <summary>Source-only admission wiring and exact pre-admission original reconstruction.</summary>
/// <remarks>
/// Four methods make seven reads of four fixed source paths, with no discovery or production calls.
/// ReadSource alone reads files. All restoration operations used by older guards are text/byte-only.
/// Actual UTF-8/BOM/final newline is validated before canonicalization; historical bytes are explicit
/// renderings, never called checkout bytes. Source and literals accept LF, CRLF and mixed pairs equally.
/// No early renderer, guard, root resolver, runtime, logger or terminal is executed. Reads are nonzero I/O.
/// </remarks>
[TestClass]
public sealed class CodeAltaStartupAdmissionSourceTests
{
    [TestMethod]
    public void Program_AcquiresBeforeLoggingTerminalAndPluginStartup()
    {
        RequireProgramAdmission(ReadSource("CodeAlta.Tui/Program.cs"));
        var admission = ReadSource("CodeAlta.Hosting/CodeAltaStartupAdmission.cs");
        RequireOnce(admission, Literal(AdmissionMethod));
    }

    [TestMethod]
    public void Program_ReleasesAfterPluginCleanupAndLoggingShutdown()
    {
        // The entire moved admitted body, including both finally blocks, is a mandatory NEW map.
        RequireProgramAdmission(ReadSource("CodeAlta.Tui/Program.cs"));
    }

    [TestMethod]
    public void Program_EarlyOutputAndAdmissionFailureAvoidMutableStartup()
    {
        RequireProgramAdmission(ReadSource("CodeAlta.Tui/Program.cs"));
        var cli = ReadSource("CodeAlta.Tui/CodeAltaCliOptions.cs");
        AssertOriginal(Invert(cli, CliEdits()), 7943, 210, true,
            "E037B957FA19449A9F42F6405F7BAC46393F271EEBA85D02B655C00B4DA7DFE2");
    }

    [TestMethod]
    public void Program_RunAsyncDoesNotReacquireAdmission()
    {
        RequireProgramAdmission(ReadSource("CodeAlta.Tui/Program.cs"));
        var guard = ReadSource("CodeAlta.Hosting/CodeAltaSingleInstanceGuard.cs");
        AssertOriginal(Invert(guard, GuardEdits()), 6402, 199, true,
            "F6612E04363045B2035BB91FD31803FFA30AFB2DA810D14080192675D66AC18C");
    }

    internal static void RequireProgramAdmission(string program)
        => AssertOriginal(RestoreProgram(program), 21957, 500, true,
            "46C722923838E1D25BE806A1C39684F20FA53261A1DD744DB45707E17A9BD2D3");

    internal static string RestoreProgram(string program) => Invert(program, ProgramEdits());

    internal static string RestoreDeferredGuard(string source)
    {
        var restored = Invert(source,
        [
            new(Literal(OldDeferredChecks), Literal(NewDeferredChecks)),
            .. DeferredTextEdits(),
        ]);
        AssertOriginal(restored, 28073, 503, false,
            "FF1F2C40CA27A4E0462AE034817A70986A74742CF4A379E4D0BB5ABC8C8A6793");
        return restored;
    }

    internal static string RestoreCatalogRoute(string path, string source)
    {
        if (string.Equals(path, "CodeAlta.Tests/DeferredCodeAltaAppSourceTests.cs", StringComparison.Ordinal))
        {
            return RestoreDeferredGuard(source);
        }

        if (string.Equals(path, "CodeAlta.Tests/AltaReminderLifetimeSourceTests.cs", StringComparison.Ordinal))
        {
            var restored = Invert(source,
            [
                new(Literal(OldReminderDeferredCheck), Literal(NewReminderDeferredCheck)),
                new("            AssertBaseline(source, baseline);", Literal(NewReminderRouteCheck)),
                .. ReminderTextEdits(),
            ]);
            AssertOriginal(restored, 54692, 1202, false,
                "F65FA0B9652FC4F2BE92C89386660BEEF3E4FC1933E65162D823FCAF94B292FD");
            return restored;
        }

        return source;
    }

    private readonly record struct Edit(string Before, string After);

    private static Edit[] ProgramEdits() =>
    [
        new("using System.Diagnostics;\n", "using System.Diagnostics;\nusing CodeAlta.Hosting;\n"),
        new(Literal(OldTopLevel), Literal(NewTopLevel)),
        new("internal partial class Program\n{\n", "internal partial class Program\n{\n" +
            Literal(EarlyMethod) + "\n\n" + AdmittedMethod() + "\n\n"),
        new("        using var singleInstanceGuard = CodeAltaSingleInstanceGuard.Acquire();\n        var cancellationTokenSource = new CancellationTokenSource();",
            "        var cancellationTokenSource = new CancellationTokenSource();"),
    ];

    private const string OldTopLevel = """
    var mainThreadId = Environment.CurrentManagedThreadId;
    try
    {
        var homeRoot = Program.GetDefaultHomeRoot();
        CodeAltaLogging.Initialize(homeRoot);

        // Plugin runtime startup ordering: register MSBuild before any plugin build service, pipe-logger
        // event payload, or Microsoft.Build type can be touched. Safe-mode raw args/environment are
        // still read by host-owned code before dynamic plugins are built or loaded.
        // Disabled for now until https://github.com/dotnet/sdk/pull/54172 is merged
        // //CodeAltaPluginRuntimeStartup.RegisterMsBuildDefaults();
        using var session = Terminal.Open();

        _ = PluginRuntimeConfigResolver.IsSafeModeEnabled(args);
        var commandLinePluginRuntime = Program.StartPluginRuntimeForCommandLine(args, CancellationToken.None);
        try
        {
            var pluginCommandLineContributions = Program.GetPluginCommandLineContributions(commandLinePluginRuntime);
            var command = CodeAltaCliOptions.CreateCommandApp(
                options => Program.RunAsync(options, mainThreadId, commandLinePluginRuntime),
                pluginCommandLineContributions);
            return command.RunAsync(args).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            if (commandLinePluginRuntime is not null)
            {
                commandLinePluginRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }
    catch (CodeAltaAlreadyRunningException ex)
    {
        Terminal.WriteMarkupLine($"[bright-red]{AnsiMarkup.Escape(ex.Message)}[/]");
        return 1;
    }
    catch (Exception ex)
    {
        try
        {
            LogManager.GetLogger("CodeAlta.Program").Error(ex, "Top-level exception");
        }
        catch
        {
        }

        CodeAltaCrashReporter.ReportFatalException("Top-level exception", ex);
        Terminal.WriteLine(ex.ToString());
        return 1;
    }
    finally
    {
        LogManager.Shutdown();
    }
    """;

    private const string NewTopLevel = """
    var mainThreadId = Environment.CurrentManagedThreadId;
    try
    {
        return CodeAltaStartupAdmission.Run(
            args,
            Program.RunEarlyCommand,
            static () => CodeAltaSingleInstanceGuard.Acquire(),
            () => Program.RunAdmittedStartup(args, mainThreadId));
    }
    catch (Exception ex)
    {
        // Admission/early-output failures must not initialize logging, crash reporting or Terminal.
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    """;

    private const string EarlyMethod = """
        internal static int RunEarlyCommand(string argument)
        {
            var command = CodeAltaCliOptions.CreatePlainCommandApp(
                static _ => throw new InvalidOperationException("Early commands must not enter mutable startup."));
            return command.RunAsync([argument]).AsTask().GetAwaiter().GetResult();
        }
    """;

    private static string AdmittedMethod()
    {
        // This is frozen OLD data, not an interval extracted from a candidate source file.
        var original = Literal(OldTopLevel);
        const string firstLine = "var mainThreadId = Environment.CurrentManagedThreadId;\n";
        Assert.IsTrue(original.StartsWith(firstLine, StringComparison.Ordinal));
        return "    internal static int RunAdmittedStartup(string[] args, int mainThreadId)\n    {\n" +
            Indent(original[firstLine.Length..], 8) + "\n    }";
    }

    private static Edit[] CliEdits() =>
    [
        new("    private static CommandApp CreateCommandAppCore(\n", Literal(PlainCommandFactory) + "\n\n    private static CommandApp CreateCommandAppCore(\n"),
        new("        ParseState state,\n        Func<CodeAltaCliOptions, ValueTask<int>> execute,\n        IReadOnlyList<CommandNode>? pluginCommandLineContributions = null)",
            "        ParseState state,\n        Func<CodeAltaCliOptions, ValueTask<int>> execute,\n        IReadOnlyList<CommandNode>? pluginCommandLineContributions = null,\n        bool plainOutput = false)"),
        new(Literal(OldOutputFactory), Literal(NewOutputFactory)),
    ];

    private const string PlainCommandFactory = """
        internal static CommandApp CreatePlainCommandApp(Func<CodeAltaCliOptions, ValueTask<int>> execute)
        {
            ArgumentNullException.ThrowIfNull(execute);
            return CreateCommandAppCore(new ParseState(), execute, plainOutput: true);
        }
    """;

    private const string OldOutputFactory = """
                    OutputFactory = static _ => new TerminalVisualCommandOutput(new TerminalVisualOutputOptions
                    {
                        UseTableForOptions = true,
                        SectionGroupMinWidth = 70,
                        ErrorGroupMinWidth = 70,
                    }),
    """;

    private const string NewOutputFactory = """
                    OutputFactory = plainOutput
                        ? static _ => DefaultCommandOutput.Instance
                        : static _ => new TerminalVisualCommandOutput(new TerminalVisualOutputOptions
                        {
                            UseTableForOptions = true,
                            SectionGroupMinWidth = 70,
                            ErrorGroupMinWidth = 70,
                        }),
    """;

    private static Edit[] GuardEdits() =>
    [
        new("namespace CodeAlta.Tui;", "namespace CodeAlta.Hosting;"),
        new("internal sealed class CodeAltaSingleInstanceGuard : IDisposable", Literal(GuardDeclaration)),
        new("    public string LockFilePath { get; }", "    /// <summary>Gets the absolute path of the acquired lock file.</summary>\n    public string LockFilePath { get; }"),
        new("    public static CodeAltaSingleInstanceGuard Acquire()", Literal(DefaultAcquireDeclaration)),
        new("    public static CodeAltaSingleInstanceGuard Acquire(string lockFilePath)", Literal(ExplicitAcquireDeclaration)),
        new("    public void Dispose()", "    /// <summary>Releases this guard and attempts to remove its lock file; repeated calls are ignored.</summary>\n    /// <exception cref=\"IOException\">The underlying stream could not be disposed.</exception>\n    public void Dispose()"),
        new("internal sealed class CodeAltaAlreadyRunningException : Exception", "/// <summary>Reports that the existing shared-state lock could not be acquired.</summary>\npublic sealed class CodeAltaAlreadyRunningException : Exception"),
        new("    public CodeAltaAlreadyRunningException(int? processId, Exception? innerException)",
            "    /// <summary>Initializes a lock-acquisition failure with the observed owner information.</summary>\n    /// <param name=\"processId\">The recorded process ID, or null when it could not be read.</param>\n    /// <param name=\"innerException\">The underlying acquisition failure.</param>\n    public CodeAltaAlreadyRunningException(int? processId, Exception? innerException)"),
        new("    public int? ProcessId { get; }", "    /// <summary>Gets the recorded process ID, or null when it could not be read.</summary>\n    public int? ProcessId { get; }"),
        // Deliberate liveness changes follow extraction; invert these first, never rebase the original.
        new(Literal(GuardDeclaration), Literal(FailClosedGuardDeclaration)),
        new(Literal(OldProcessInspection), Literal(FailClosedProcessInspection)),
    ];

    private const string GuardDeclaration = """
    /// <summary>Acquires the existing alta.lock admission guard for shared application state.</summary>
    /// <remarks>
    /// This extraction preserves the existing PID/stale-file algorithm, including its unqualified
    /// process-inspection and deletion races. It does not establish cross-process or cross-head safety.
    /// </remarks>
    public sealed class CodeAltaSingleInstanceGuard : IDisposable
    """;

    private const string FailClosedGuardDeclaration = """
    /// <summary>Guards shared application state with conservative process-liveness inspection.</summary>
    /// <remarks>
    /// Unknown process inspection fails closed. PID/stale-file deletion and release races remain
    /// unqualified; this does not establish cross-process or cross-head safety.
    /// </remarks>
    public sealed class CodeAltaSingleInstanceGuard : IDisposable
    """;

    private const string OldProcessInspection = """
        private static bool IsProcessRunning(int processId)
            => IsProcessRunning(processId, Process.GetProcessById, static process => process.HasExited);

        internal static bool IsProcessRunning(
            int processId,
            Func<int, Process> getProcessById,
            Func<Process, bool> hasExited)
        {
            ArgumentNullException.ThrowIfNull(getProcessById);
            ArgumentNullException.ThrowIfNull(hasExited);

            try
            {
                using var process = getProcessById(processId);
                return !hasExited(process);
            }
            catch
            {
                // Process.HasExited can throw (for example, access denied while opening the process).
                return false;
            }
        }
    """;

    private const string FailClosedProcessInspection = """
        private static bool IsProcessRunning(int processId)
            => IsProcessRunning<Process>(processId, Process.GetProcessById, static process => process.HasExited);

        /// <summary>Returns false only for lookup absence or observed exit followed by successful release.</summary>
        /// <remarks>
        /// For a positive PID, the .NET GetProcessById adapter reports a missing process via ArgumentException.
        /// Only that lookup-stage exception establishes absence; inspection and release errors fail closed.
        /// Invalid PIDs and null resources are unknown. Every acquired resource is released once, without retry.
        /// Observed process absence is not ownership evidence or authority over the current lock pathname.
        /// </remarks>
        internal static bool IsProcessRunning<TProcess>(
            int processId,
            Func<int, TProcess?> getProcessById,
            Func<TProcess, bool> hasExited)
            where TProcess : class, IDisposable
        {
            ArgumentNullException.ThrowIfNull(getProcessById);
            ArgumentNullException.ThrowIfNull(hasExited);

            if (processId <= 0)
            {
                return true;
            }

            TProcess? process;
            try
            {
                process = getProcessById(processId);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch
            {
                return true;
            }

            if (process is null)
            {
                return true;
            }

            try
            {
                using (process)
                {
                    return !hasExited(process);
                }
            }
            catch
            {
                // An inspection or release failure must never authorize stale-file reclamation.
                return true;
            }
        }
    """;

    private const string DefaultAcquireDeclaration = """
        /// <summary>Acquires the existing guard under the default user-profile .alta directory.</summary>
        /// <returns>The caller-owned guard.</returns>
        /// <exception cref="InvalidOperationException">The user profile directory is unavailable.</exception>
        /// <exception cref="CodeAltaAlreadyRunningException">The lock file could not be acquired.</exception>
        /// <exception cref="UnauthorizedAccessException">Access to the lock directory or file is denied.</exception>
        /// <exception cref="IOException">The directory or PID write fails.</exception>
        public static CodeAltaSingleInstanceGuard Acquire()
    """;

    private const string ExplicitAcquireDeclaration = """
        /// <summary>Acquires the existing guard at an explicitly supplied lock-file path.</summary>
        /// <param name="lockFilePath">The lock-file path; automation must supply a task-owned path.</param>
        /// <returns>The caller-owned guard.</returns>
        /// <exception cref="ArgumentNullException">The path is null.</exception>
        /// <exception cref="ArgumentException">The path is empty, whitespace or invalid.</exception>
        /// <exception cref="CodeAltaAlreadyRunningException">The lock file could not be acquired.</exception>
        /// <exception cref="UnauthorizedAccessException">Access to the lock directory or file is denied.</exception>
        /// <exception cref="IOException">The directory or PID write fails.</exception>
        public static CodeAltaSingleInstanceGuard Acquire(string lockFilePath)
    """;

    private const string AdmissionMethod = """
        public static int Run(
            IReadOnlyList<string> arguments,
            Func<string, int> runEarlyCommand,
            Func<IDisposable> acquireLease,
            Func<int> runAdmittedStartup)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            ArgumentNullException.ThrowIfNull(runEarlyCommand);
            ArgumentNullException.ThrowIfNull(acquireLease);
            ArgumentNullException.ThrowIfNull(runAdmittedStartup);

            if (arguments.Count == 1 && arguments[0] is "--help" or "-h" or "--version")
            {
                return runEarlyCommand(arguments[0]);
            }

            using var lease = acquireLease() ?? throw new InvalidOperationException("Startup admission did not return a lease.");
            return runAdmittedStartup();
        }
    """;

    private const string OldDeferredChecks = """"
            // This does not fix early logging/plugin admission or Program's CTS lifetime.
            var program = ReadSource("CodeAlta.Tui/Program.cs");
            var programRun = Scope(program, "    internal static async ValueTask<int> RunAsync(", "\n    }\n");
            RequireOrdered(programRun,
                "using var singleInstanceGuard = CodeAltaSingleInstanceGuard.Acquire();",
                "var cancellationTokenSource = new CancellationTokenSource();",
                "await using var app = new DeferredCodeAltaApp(prestartedPluginRuntime);",
                "Program.ThrowIfCurrentThreadIsNotMainThread(mainThreadId);",
                "await app.RunAsync(cancellationTokenSource.Token);",
                "PrintUpdateAvailableMessage(app.UpdateCheckSnapshot);",
                "return 0;");
            var commandLine = Scope(program, "    var commandLinePluginRuntime = Program.StartPluginRuntimeForCommandLine(args, CancellationToken.None);", "\ncatch (CodeAltaAlreadyRunningException ex)");
    """";

    private const string NewDeferredChecks = """"
            // Admission is checked separately; Program's CTS lifetime remains unchanged.
            var program = ReadSource("CodeAlta.Tui/Program.cs");
            CodeAltaStartupAdmissionSourceTests.RequireProgramAdmission(program);
            var programRun = Scope(program, "    internal static async ValueTask<int> RunAsync(", "\n    }\n");
            RequireOrdered(programRun,
                "var cancellationTokenSource = new CancellationTokenSource();",
                "await using var app = new DeferredCodeAltaApp(prestartedPluginRuntime);",
                "Program.ThrowIfCurrentThreadIsNotMainThread(mainThreadId);",
                "await app.RunAsync(cancellationTokenSource.Token);",
                "PrintUpdateAvailableMessage(app.UpdateCheckSnapshot);",
                "return 0;");
            var commandLine = Scope(program, "            var commandLinePluginRuntime = Program.StartPluginRuntimeForCommandLine(args, CancellationToken.None);", "\n        catch (CodeAltaAlreadyRunningException ex)");
    """";

    private const string OldReminderDeferredCheck = """
            AssertBaseline(Invert(ReadSource(DeferredGuard), DeferredGuardEdits()), DeferredGuard);
    """;

    private const string NewReminderDeferredCheck = """
            AssertBaseline(Invert(CodeAltaStartupAdmissionSourceTests.RestoreDeferredGuard(ReadSource(DeferredGuard)), DeferredGuardEdits()), DeferredGuard);
    """;

    private const string NewReminderRouteCheck = """
                AssertBaseline(string.Equals(baseline.Path, "CodeAlta.Tui/Program.cs", StringComparison.Ordinal)
                    ? CodeAltaStartupAdmissionSourceTests.RestoreProgram(source)
                    : source, baseline);
    """;

    // These extra edits restore the entire pre-compatibility fixtures, before their older inverses run.
    private static Edit[] DeferredTextEdits() =>
    [
        new("        => File.ReadAllText(Path.Combine(SourceRoot(), relativePath))\n" +
            "            .Replace(\"\\r\\n\", \"\\n\", StringComparison.Ordinal);",
            "        => SourceTestText.DecodeSource(File.ReadAllBytes(Path.Combine(SourceRoot(), relativePath)));"),
        InsertAfterSignature("string Scope(string source, string startAnchor, string endAnchor)",
            "        source = SourceTestText.Canonicalize(source);\n" +
            "        startAnchor = SourceTestText.Canonicalize(startAnchor);\n" +
            "        endAnchor = SourceTestText.Canonicalize(endAnchor);"),
        InsertAfterSignature("void RequireOnce(string source, string expected)",
            "        source = SourceTestText.Canonicalize(source);\n" +
            "        expected = SourceTestText.Canonicalize(expected);"),
        InsertAfterSignature("void RequireOrdered(string source, params string[] expected)",
            "        source = SourceTestText.Canonicalize(source);\n" +
            "        expected = Array.ConvertAll(expected, SourceTestText.Canonicalize);"),
        InsertAfterSignature("void Reject(string source, params string[] forbidden)",
            "        source = SourceTestText.Canonicalize(source);\n" +
            "        forbidden = Array.ConvertAll(forbidden, SourceTestText.Canonicalize);"),
    ];

    private static Edit[] ReminderTextEdits() =>
    [
        new(Literal(OldReminderReader),
            "        // Validate actual UTF-8/BOM/final newline; LF, CRLF and mixed pairs are equivalent content.\n" +
            "        return SourceTestText.DecodeSource(bytes);"),
        InsertAfterSignature("byte[] Encode(string normalized, Baseline baseline)",
            "        // Historical rendering, not actual checkout bytes.\n" +
            "        normalized = SourceTestText.Canonicalize(normalized);"),
        InsertAfterSignature("string Invert(string future, Edit[] edits)",
            "        future = SourceTestText.Canonicalize(future);"),
        new("            var edit = edits[index];", Literal(NewReminderCanonicalEdit)),
        InsertAfterSignature("void RequireCount(string source, string expected, int count)",
            "        source = SourceTestText.Canonicalize(source);\n" +
            "        expected = SourceTestText.Canonicalize(expected);"),
        InsertAfterSignature("string Indent(string text, int spaces)",
            "        text = SourceTestText.Canonicalize(text);"),
    ];

    private static Edit InsertAfterSignature(string signature, string additions)
    {
        // Both sides are frozen inputs, never a source-derived interval or an optional replacement.
        var before = "    private static " + signature + "\n    {\n";
        return new(before, before + Literal(additions) + "\n");
    }

    private const string OldReminderReader = """
            var text = new UTF8Encoding(false, true).GetString(bytes);
            Assert.IsFalse(text.StartsWith("\uFEFF", StringComparison.Ordinal), baseline.Path + ": BOM");
            Assert.IsTrue(text.EndsWith("\n", StringComparison.Ordinal), baseline.Path + ": final newline");
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.IsFalse(normalized.Contains('\r'), baseline.Path + ": lone CR");
            Assert.IsTrue(string.Equals(text,
                baseline.CrLf ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal) : normalized,
                StringComparison.Ordinal), baseline.Path + ": mixed or changed line endings");
            return normalized;
    """;

    private const string NewReminderCanonicalEdit = """
                var edit = edits[index] with
                {
                    Before = SourceTestText.Canonicalize(edits[index].Before),
                    After = SourceTestText.Canonicalize(edits[index].After),
                };
    """;

    private static string ReadSource(string path, [CallerFilePath] string sourceFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile) ?? throw new AssertFailedException("Missing fixture directory."), ".."));
        var bytes = File.ReadAllBytes(Path.Combine(root, path));
        return SourceTestText.DecodeSource(bytes);
    }

    private static string Literal(string value)
    {
        var lf = SourceTestText.Canonicalize(value);
        Assert.IsFalse(lf.EndsWith('\n'), "Frozen literals exclude the final newline.");
        return lf;
    }

    private static byte[] Encode(string text) => new UTF8Encoding(false, true).GetBytes(text);

    private static void AssertOriginal(string lf, int bytes, int lines, bool crLf, string hash)
    {
        lf = SourceTestText.Canonicalize(lf);
        Assert.IsFalse(lf.StartsWith('\uFEFF'));
        Assert.IsFalse(lf.Contains('\r'));
        Assert.IsTrue(lf.EndsWith('\n'));
        var historical = Encode(crLf ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf);
        Assert.AreEqual(bytes, historical.Length, "Complete historical byte count");
        Assert.AreEqual(lines, lf.Count(static c => c == '\n'), "Complete original line count");
        Assert.AreEqual(hash, Convert.ToHexString(SHA256.HashData(historical)), "Complete historical hash");
    }

    private static string Invert(string candidate, Edit[] edits)
    {
        candidate = SourceTestText.Canonicalize(candidate);
        edits = Array.ConvertAll(edits, static edit => new Edit(
            SourceTestText.Canonicalize(edit.Before), SourceTestText.Canonicalize(edit.After)));
        var restored = candidate;
        for (var index = edits.Length - 1; index >= 0; index--)
        {
            RequireOnce(restored, edits[index].After);
            restored = restored.Replace(edits[index].After, edits[index].Before, StringComparison.Ordinal);
        }

        var forward = restored;
        foreach (var edit in edits)
        {
            RequireOnce(forward, edit.Before);
            forward = forward.Replace(edit.Before, edit.After, StringComparison.Ordinal);
        }

        Assert.AreEqual(candidate, forward, "Complete ordinal forward reconstruction");
        CollectionAssert.AreEqual(Encode(candidate), Encode(forward), "Every reconstructed candidate byte");
        return restored;
    }

    private static void RequireOnce(string source, string expected)
    {
        source = SourceTestText.Canonicalize(source);
        expected = SourceTestText.Canonicalize(expected);
        Assert.IsTrue(expected.Length > 0);
        var first = source.IndexOf(expected, StringComparison.Ordinal);
        Assert.IsTrue(first >= 0, "Missing entire expected source: " + expected);
        Assert.AreEqual(first, source.LastIndexOf(expected, StringComparison.Ordinal), "Expected one ordinal occurrence");
    }

    private static string Indent(string text, int spaces)
    {
        text = SourceTestText.Canonicalize(text);
        var padding = new string(' ', spaces);
        return string.Join("\n", text.Split('\n').Select(line => line.Length == 0 ? "" : padding + line));
    }
}
