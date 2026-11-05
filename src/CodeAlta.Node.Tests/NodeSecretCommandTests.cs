using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Node.Tests;

/// <summary>
/// <c>altanode secrets</c>: the owner of a node sets, rotates, lists and deletes the secrets of its machine from a
/// terminal, the value read from the standard input or a prompt that does not show it, never from an argument.
/// </summary>
[TestClass]
public sealed class NodeSecretCommandTests
{
    private const string Value = "glsa_token_0123456789";

    [TestMethod]
    public async Task AValue_IsNeverTakenFromAnArgument()
    {
        using var root = new NodeTestRoot();
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await new NodeApplication(output, error) { InitializeLogging = false, ServiceEnvironment = static () => null }
            .RunAsync(["secrets", "set", "GRAFANA_TOKEN", Value, "--data-root", root.DataRoot]);

        Assert.AreEqual(NodeExitCodes.InvalidUsage, code);
        StringAssert.Contains(error.ToString(), "never given as an argument");
        Assert.AreEqual(0, new LocalSecretStore(root.DataRoot).ListSecrets().Count);
    }

    [TestMethod]
    public async Task Set_ReadsTheStandardInput_ListShowsNamesNeverValues_RotateAndDeleteFollow()
    {
        using var root = new NodeTestRoot();

        var (code, output, _) = await RunAsync(root, Value + "\n", "set", "GRAFANA_TOKEN");
        Assert.AreEqual(NodeExitCodes.Success, code);
        StringAssert.Contains(output, "Secret GRAFANA_TOKEN is set (version 1).");
        Assert.IsTrue(Store(root).TryGetSecret("GRAFANA_TOKEN", out var stored));
        Assert.AreEqual(Value, stored, "The line end of the pipe is not part of the value.");

        Assert.AreEqual(NodeExitCodes.Success, (await RunAsync(root, null, "set-var", "GRAFANA_URL", "https://grafana.example")).Code);
        (code, output, _) = await RunAsync(root, null, "list");
        Assert.AreEqual(NodeExitCodes.Success, code);
        StringAssert.Contains(output, "GRAFANA_TOKEN");
        StringAssert.Contains(output, "tester (altanode)");
        StringAssert.Contains(output, "https://grafana.example", "A variable is not secret: its value is listed.");
        Assert.IsFalse(output.Contains(Value, StringComparison.Ordinal), "A secret is never shown again.");

        (code, output, _) = await RunAsync(root, "rotated-value", "rotate", "GRAFANA_TOKEN");
        Assert.AreEqual(NodeExitCodes.Success, code);
        StringAssert.Contains(output, "(version 2)");
        Assert.IsTrue(Store(root).TryGetSecret("GRAFANA_TOKEN", out stored));
        Assert.AreEqual("rotated-value", stored);

        Assert.AreEqual(NodeExitCodes.Success, (await RunAsync(root, null, "delete", "GRAFANA_TOKEN")).Code);
        Assert.AreEqual(NodeExitCodes.Failure, (await RunAsync(root, null, "delete", "GRAFANA_TOKEN")).Code);
        Assert.AreEqual(NodeExitCodes.Success, (await RunAsync(root, null, "delete-var", "GRAFANA_URL")).Code);
        StringAssert.Contains((await RunAsync(root, null, "list")).Output, "No secret nor variable is set on this machine.");
    }

    [TestMethod]
    public async Task ATypedValue_IsNotShown_AndRotateNeedsASecretThatIsSet()
    {
        using var root = new NodeTestRoot();
        var output = new StringWriter();

        var (code, _, error) = await RunAsync(root, null, "rotate", "MISSING");
        Assert.AreEqual(NodeExitCodes.Failure, code);
        StringAssert.Contains(error, "altanode secrets set MISSING");

        var typed = new NodeSecretCommands(output, new StringWriter(), TextReader.Null)
        {
            InputRedirected = false, ReadHidden = static () => "typed-secret-value", SetBy = "tester (altanode)", Protector = FileProtector,
        };
        var options = new NodeSecretCommandOptions();
        options.Profile.DataRoot = root.DataRoot;
        options.Words.AddRange(["set", "TYPED"]);
        Assert.AreEqual(NodeExitCodes.Success, await typed.RunAsync(options));
        StringAssert.Contains(output.ToString(), "Value of TYPED (not shown): ");
        Assert.IsFalse(output.ToString().Contains("typed-secret-value", StringComparison.Ordinal));
        Assert.IsTrue(Store(root).TryGetSecret("TYPED", out var stored));
        Assert.AreEqual("typed-secret-value", stored);
        Assert.AreEqual(NodeExitCodes.InvalidUsage, (await RunAsync(root, null, "set", "bad-name")).Code);
    }

    private static LocalSecretStore Store(NodeTestRoot root) => new(root.DataRoot, FileProtector(root.DataRoot));

    private static ISecretProtector FileProtector(string globalRoot)
        => new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(globalRoot, "secrets", "key")));

    private static NodeSecretCommands Commands(TextWriter output, TextWriter error, string? input)
        => new(output, error, new StringReader(input ?? string.Empty)) { InputRedirected = true, SetBy = "tester (altanode)", Protector = FileProtector };

    private static async Task<(int Code, string Output, string Error)> RunAsync(NodeTestRoot root, string? input, params string[] words)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new NodeSecretCommandOptions();
        options.Profile.DataRoot = root.DataRoot;
        options.Words.AddRange(words);
        var code = await Commands(output, error, input).RunAsync(options);
        return (code, output.ToString(), error.ToString());
    }
}
