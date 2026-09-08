namespace CodeAlta.Hosting.Tests;

/// <summary>Inert tests of the production-used synchronous startup admission operation.</summary>
[TestClass]
public sealed class CodeAltaStartupAdmissionTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(8)]
    [DataRow(15)]
    public void Run_ValidatesMandatoryOperationsBeforeInvocation(int nullMask)
    {
        var calls = new List<string>();
        IReadOnlyList<string>? arguments = (nullMask & 1) != 0 ? null : ["--help"];
        Func<string, int>? early = (nullMask & 2) != 0 ? null : _ => { calls.Add("early"); return 0; };
        Func<IDisposable>? acquire = (nullMask & 4) != 0 ? null : () => { calls.Add("acquire"); return new Lease(() => calls.Add("release")); };
        Func<int>? startup = (nullMask & 8) != 0 ? null : () => { calls.Add("startup"); return 0; };

        // Deliberately supply null inputs to the mandatory production validator.
        Assert.ThrowsExactly<ArgumentNullException>(() => CodeAltaStartupAdmission.Run(arguments!, early!, acquire!, startup!));
        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    [DataRow("--version")]
    public void Run_EarlyFlagsDoNotResolveRootAcquireOrStart(string argument)
    {
        var calls = new List<string>();
        var result = CodeAltaStartupAdmission.Run([argument], value =>
        {
            calls.Add(value);
            return 17;
        }, () => throw new AssertFailedException("The root/acquisition callback was entered."),
            () => throw new AssertFailedException("Mutable startup was entered."));

        Assert.AreEqual(17, result);
        CollectionAssert.AreEqual(new[] { argument }, calls);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("status")]
    [DataRow("plugin")]
    [DataRow("unknown")]
    [DataRow("mixed-help")]
    [DataRow("mixed-version")]
    public void Run_NonEarlyArgumentsAcquireBeforeStartup(string scenario)
    {
        string[] arguments = scenario switch
        {
            "empty" => [],
            "status" => ["--plugins-status"],
            "plugin" => ["plugin-command"],
            "unknown" => ["--unknown"],
            "mixed-help" => ["--help", "--no-plugins"],
            "mixed-version" => ["--version", "plugin-command"],
            _ => throw new AssertFailedException("Unknown row."),
        };
        var calls = new List<string>();
        var lease = new Lease(() => calls.Add("release"));
        var result = CodeAltaStartupAdmission.Run(arguments,
            _ => throw new AssertFailedException("Unexpected early dispatch."),
            () => { calls.Add("acquire"); return lease; },
            () => { calls.Add("startup"); return 23; });

        Assert.AreEqual(23, result);
        Assert.AreEqual(1, lease.Disposals);
        CollectionAssert.AreEqual(new[] { "acquire", "startup", "release" }, calls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Run_AcquisitionFailureDoesNotStartMutableWork(bool returnsNull)
    {
        var failure = new InvalidOperationException("acquisition");
        var calls = 0;
        var actual = Assert.ThrowsExactly<InvalidOperationException>(() => CodeAltaStartupAdmission.Run([], _ => 0,
            () =>
            {
                calls++;
                // A deliberately invalid acquisition result must never admit startup.
                return returnsNull ? null! : throw failure;
            }, () => throw new AssertFailedException("Unleased startup.")));

        Assert.AreEqual(1, calls);
        if (!returnsNull)
        {
            Assert.AreSame(failure, actual);
        }
        else
        {
            StringAssert.Contains(actual.Message, "lease");
        }
    }

    [TestMethod]
    public void Run_PreservesStartupExitCodeAndReleasesLease()
    {
        var lease = new Lease(static () => { });
        Assert.AreEqual(7, CodeAltaStartupAdmission.Run([], _ => 0, () => lease, () => 7));
        Assert.AreEqual(1, lease.Disposals);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Run_StartupFailureStillReleasesLease(bool releaseFails)
    {
        var startupFailure = new InvalidOperationException("startup");
        var releaseFailure = new InvalidOperationException("release");
        var lease = new Lease(() => { if (releaseFails) throw releaseFailure; });
        var actual = Assert.ThrowsExactly<InvalidOperationException>(() => CodeAltaStartupAdmission.Run([], _ => 0,
            () => lease, () => throw startupFailure));

        Assert.AreEqual(1, lease.Disposals);
        Assert.AreSame(releaseFails ? releaseFailure : startupFailure, actual);
    }

    [TestMethod]
    public void Run_KeepsLeaseThroughStartupCleanup()
    {
        var calls = new List<string>();
        var lease = new Lease(() => calls.Add("release"));
        CodeAltaStartupAdmission.Run([], _ => 0, () => lease, () =>
        {
            try
            {
                calls.Add("command");
                return 0;
            }
            finally
            {
                Assert.AreEqual(0, lease.Disposals);
                calls.Add("plugin/terminal cleanup");
                calls.Add("logging shutdown");
            }
        });

        CollectionAssert.AreEqual(new[] { "command", "plugin/terminal cleanup", "logging shutdown", "release" }, calls);
        Assert.AreEqual(1, lease.Disposals);
    }

    [TestMethod]
    public void Run_RemainsOnCallingThread()
    {
        var threadId = Environment.CurrentManagedThreadId;
        var lease = new Lease(() => Assert.AreEqual(threadId, Environment.CurrentManagedThreadId));
        Assert.AreEqual(0, CodeAltaStartupAdmission.Run([], _ => 0,
            () => { Assert.AreEqual(threadId, Environment.CurrentManagedThreadId); return lease; },
            () => { Assert.AreEqual(threadId, Environment.CurrentManagedThreadId); return 0; }));
        Assert.AreEqual(1, lease.Disposals);
    }

    private sealed class Lease(Action release) : IDisposable
    {
        public int Disposals { get; private set; }

        public void Dispose()
        {
            Disposals++;
            release();
        }
    }
}
