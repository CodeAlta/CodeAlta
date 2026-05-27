using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Agent.LocalRuntime.Tools;
using Microsoft.Extensions.AI;

namespace CodeAlta.Tests;

[TestClass]
public sealed class LocalAgentToolsTests
{
    [TestMethod]
    public async Task ReadFileTool_ReadsLineWindowFromWorkingDirectory()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllLinesAsync(filePath, ["alpha", "beta", "gamma"]).ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "read_file");
        using var args = JsonDocument.Parse("""{"path":"sample.txt","offset":2,"limit":2}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "    2: beta");
        StringAssert.Contains(output, "    3: gamma");
    }

    [TestMethod]
    public async Task ReadFileTool_SupportsNegativeOffsetFromEnd()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllLinesAsync(filePath, ["alpha", "beta", "gamma", "delta"]).ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "read_file");
        using var args = JsonDocument.Parse("""{"path":"sample.txt","offset":-2,"limit":2}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        Assert.AreEqual("    3: gamma" + Environment.NewLine + "    4: delta", output);
    }

    [TestMethod]
    public async Task ReadFileTool_ReturnsFailureForOffsetZero()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllLinesAsync(filePath, ["alpha", "beta"]).ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "read_file");
        using var args = JsonDocument.Parse("""{"path":"sample.txt","offset":0}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "0 is not allowed");
    }

    [TestMethod]
    public async Task ReadFileTool_AcceptsAbsolutePath()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllLinesAsync(filePath, ["alpha", "beta"]).ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "read_file");
        using var args = JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(filePath)}}}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "    1: alpha");
        StringAssert.Contains(output, "    2: beta");
    }

    [TestMethod]
    public async Task ReadFileTool_ReturnsEmptyTextWhenOffsetIsPastEndOfFile()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllLinesAsync(filePath, ["alpha", "beta"]).ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "read_file");
        using var args = JsonDocument.Parse("""{"path":"sample.txt","offset":10}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(string.Empty, Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value);
    }

    [TestMethod]
    public async Task GrepTool_UsesXenoAtomGlobPatternMatching()
    {
        using var temp = TestTempDirectory.Create();
        await File.WriteAllLinesAsync(Path.Combine(temp.Path, "file7.txt"), ["match"]).ConfigureAwait(false);
        await File.WriteAllLinesAsync(Path.Combine(temp.Path, "filex.txt"), ["match"]).ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "grep");
        using var args = JsonDocument.Parse("""{"pattern":"match","glob":"file[0-9].txt"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "file7.txt:1: match");
        Assert.IsFalse(output.Contains("filex.txt:1: match", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GrepTool_SearchesMultiplePathsAndGlobs()
    {
        using var temp = TestTempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "src");
        var docsPath = Path.Combine(temp.Path, "docs");
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(docsPath);
        await File.WriteAllLinesAsync(Path.Combine(sourcePath, "alpha.cs"), ["match from cs"]).ConfigureAwait(false);
        await File.WriteAllLinesAsync(Path.Combine(docsPath, "guide.md"), ["match from md"]).ConfigureAwait(false);
        await File.WriteAllLinesAsync(Path.Combine(docsPath, "notes.txt"), ["match from txt"]).ConfigureAwait(false);
        await File.WriteAllLinesAsync(Path.Combine(temp.Path, "outside.cs"), ["match outside requested paths"]).ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "grep");
        using var args = JsonDocument.Parse("""{"path":["src","docs"],"pattern":"match","glob":["*.cs","*.md"]}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "alpha.cs:1: match from cs");
        StringAssert.Contains(output, "guide.md:1: match from md");
        Assert.IsFalse(output.Contains("notes.txt:1: match from txt", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("outside.cs:1: match outside requested paths", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ListDirTool_AcceptsAbsolutePath()
    {
        using var temp = TestTempDirectory.Create();
        var folderPath = Path.Combine(temp.Path, "nested");
        Directory.CreateDirectory(folderPath);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "sample.txt"), "hello").ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "list_dir");
        using var args = JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(folderPath)}}}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "[file] sample.txt");
    }

    [TestMethod]
    public async Task ListDirTool_ReturnsEmptyDirectoryMarker()
    {
        using var temp = TestTempDirectory.Create();
        var folderPath = Path.Combine(temp.Path, "empty");
        Directory.CreateDirectory(folderPath);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "list_dir");
        using var args = JsonDocument.Parse("""{"path":"empty"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("(empty directory)", Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value);
    }

    [TestMethod]
    public async Task ListDirTool_ReturnsFailureWhenPathIsFile()
    {
        using var temp = TestTempDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "sample.txt"), "hello").ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "list_dir");
        using var args = JsonDocument.Parse("""{"path":"sample.txt"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "is not a directory");
    }

    [TestMethod]
    public async Task GrepTool_HonorsGitIgnoreWhenSearchingInsideRepository()
    {
        using var temp = TestTempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".git"));
        await File.WriteAllTextAsync(
                Path.Combine(temp.Path, ".git", "config"),
                """
                [core]
                    repositoryformatversion = 0
                    filemode = false
                    bare = false
                """)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".gitignore"), "ignored.txt" + Environment.NewLine).ConfigureAwait(false);
        await File.WriteAllLinesAsync(Path.Combine(temp.Path, "tracked.txt"), ["match"]).ConfigureAwait(false);
        await File.WriteAllLinesAsync(Path.Combine(temp.Path, "ignored.txt"), ["match"]).ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "grep");
        using var args = JsonDocument.Parse("""{"pattern":"match","glob":"*.txt"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "tracked.txt:1: match");
        Assert.IsFalse(output.Contains("ignored.txt:1: match", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GrepTool_SearchesSingleFileUsingRegularExpressions()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllLinesAsync(filePath, ["Alpha", "Beta42", "Beta"]).ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "grep");
        using var args = JsonDocument.Parse("""{"path":"sample.txt","pattern":"^Beta\\d+$"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        Assert.AreEqual("sample.txt:2: Beta42", output);
    }

    [TestMethod]
    public async Task GrepTool_AcceptsAbsolutePath()
    {
        using var temp = TestTempDirectory.Create();
        var filePath = Path.Combine(temp.Path, "sample.txt");
        await File.WriteAllLinesAsync(filePath, ["Alpha", "Beta42"]).ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "grep");
        using var args = JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(filePath)}},"pattern":"^Beta\\d+$"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        Assert.AreEqual("sample.txt:2: Beta42", output);
    }

    [TestMethod]
    public async Task GrepTool_ReturnsNoMatchesSentinelWhenNothingMatches()
    {
        using var temp = TestTempDirectory.Create();
        await File.WriteAllLinesAsync(Path.Combine(temp.Path, "sample.txt"), ["alpha", "beta"]).ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "grep");
        using var args = JsonDocument.Parse("""{"pattern":"gamma"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("(no matches)", Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value);
    }

    [TestMethod]
    public async Task WebGetTool_FetchesAndSimplifiesHtml()
    {
        using var htmlContent = new StringContent("<html><body><h1>Hello</h1><p>world</p></body></html>");
        htmlContent.Headers.ContentType = new("text/html");
        using var handler = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = htmlContent,
            });
        using var httpClient = new HttpClient(handler);
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(Environment.CurrentDirectory, httpClient: httpClient));
        var tool = tools.Single(static tool => tool.Spec.Name == "webget");
        using var args = JsonDocument.Parse("""{"url":"https://example.test/docs"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        Assert.AreEqual("Hello world", output);
    }

    [TestMethod]
    public async Task WebGetTool_CanReturnRawHtmlWhenRequested()
    {
        using var htmlContent = new StringContent("<html><body><h1>Hello</h1><p>world</p></body></html>");
        htmlContent.Headers.ContentType = new("text/html");
        using var handler = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = htmlContent,
            });
        using var httpClient = new HttpClient(handler);
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(Environment.CurrentDirectory, httpClient: httpClient));
        var tool = tools.Single(static tool => tool.Spec.Name == "webget");
        using var args = JsonDocument.Parse("""{"url":"https://example.test/docs","rawHtml":true}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        Assert.AreEqual("<html><body><h1>Hello</h1><p>world</p></body></html>", output);
    }

    [TestMethod]
    public async Task WebGetTool_CanIncludeHttpStatusMetadataWhenRequested()
    {
        using var content = new StringContent("""{"ok":true}""");
        content.Headers.ContentType = new("application/json");
        using var handler = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                ReasonPhrase = "Accepted",
                Content = content,
            });
        using var httpClient = new HttpClient(handler);
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(Environment.CurrentDirectory, httpClient: httpClient));
        var tool = tools.Single(static tool => tool.Spec.Name == "webget");
        using var args = JsonDocument.Parse("""{"url":"https://example.test/jobs/42","includeHttpStatus":true}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "HTTP 202 Accepted");
        StringAssert.Contains(output, "content-type: application/json");
        StringAssert.Contains(output, "{\"ok\":true}");
    }

    [TestMethod]
    public async Task WebGetTool_ReturnsFailureForHttpStatusErrors()
    {
        using var handler = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                ReasonPhrase = "Not Found",
                Content = new StringContent("missing"),
            });
        using var httpClient = new HttpClient(handler);
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(Environment.CurrentDirectory, httpClient: httpClient));
        var tool = tools.Single(static tool => tool.Spec.Name == "webget");
        using var args = JsonDocument.Parse("""{"url":"https://example.test/missing"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(result.Error, Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value);
        StringAssert.Contains(result.Error, "HTTP 404");
        StringAssert.Contains(result.Error, "https://example.test/missing");
    }

    [TestMethod]
    public async Task WebGetTool_ReturnsFailureWhenRequestTimesOut()
    {
        using var handler = new StubHttpMessageHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("late response"),
                };
            });
        using var httpClient = new HttpClient(handler);
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(
            Environment.CurrentDirectory,
            httpClient: httpClient,
            webGetTimeout: TimeSpan.FromMilliseconds(50)));
        var tool = tools.Single(static tool => tool.Spec.Name == "webget");
        using var args = JsonDocument.Parse("""{"url":"https://example.test/slow"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "timed out");
    }

    [TestMethod]
    public async Task WebGetTool_RejectsUnsupportedSchemes()
    {
        using var toolsTemp = TestTempDirectory.Create();
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(toolsTemp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "webget");
        using var args = JsonDocument.Parse("""{"url":"file:///tmp/example.txt"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "scheme is not supported");
        StringAssert.Contains(result.Error, "http or https");
    }

    [TestMethod]
    public async Task ReadFileTool_ReturnsFailureForBinaryFiles()
    {
        using var temp = TestTempDirectory.Create();
        var binaryPath = Path.Combine(temp.Path, "sample.bin");
        await File.WriteAllBytesAsync(binaryPath, [0x41, 0x00, 0x42]).ConfigureAwait(false);

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "read_file");
        using var args = JsonDocument.Parse("""{"path":"sample.bin"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "binary file");
        StringAssert.Contains(result.Error, "text files");
    }

    [TestMethod]
    public async Task ShellCommandTool_UsesPlatformShellAndCapturesOutput()
    {
        using var temp = TestTempDirectory.Create();
        AgentPermissionRequest? observedRequest = null;
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(
            temp.Path,
            onPermissionRequest: (request, _) =>
            {
                observedRequest = request;
                return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));
            }));
        var tool = tools.Single(static tool => tool.Spec.Name == "shell_command");
        var command = OperatingSystem.IsWindows()
            ? "Write-Output 'hello from pwsh'"
            : "printf 'hello from sh\\n'";
        using var args = JsonDocument.Parse($$"""{"command":{{JsonSerializer.Serialize(command)}}}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(observedRequest);
        Assert.AreEqual("commandExecution", observedRequest.Kind);
        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "exit_code: 0");
        StringAssert.Contains(output, "stdout:");
        StringAssert.Contains(output, "hello from");
        StringAssert.Contains(output, temp.Path);
    }

    [TestMethod]
    public async Task ShellCommandTool_PreservesFinalExternalExitCodeOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TestTempDirectory.Create();
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "shell_command");
        using var args = JsonDocument.Parse("""{"command":"cmd /c \"exit 7\""}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("shell_command exited with code 7.", result.Error);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "exit_code: 7");
    }

    [TestMethod]
    public async Task ShellCommandTool_StreamsProgressWhileCommandRuns()
    {
        using var temp = TestTempDirectory.Create();
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "shell_command");
        var command = OperatingSystem.IsWindows()
            ? "$stdout = [Console]::Out; $stderr = [Console]::Error; $stdout.WriteLine('step 1'); Start-Sleep -Milliseconds 50; $stderr.WriteLine('step 2');"
            : "printf 'step 1\\n'; sleep 0.05; printf 'step 2\\n' >&2";
        using var args = JsonDocument.Parse($$"""{"command":{{JsonSerializer.Serialize(command)}}}""");
        var progress = new List<AgentToolProgressUpdate>();

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone(),
                    (update, _) =>
                    {
                        progress.Add(update);
                        return ValueTask.CompletedTask;
                    }),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(progress.Count >= 2);
        Assert.IsTrue(progress.Any(static update => update.Details?.GetProperty("stream").GetString() == "stdout" && update.Delta.Contains("step 1", StringComparison.Ordinal)));
        Assert.IsTrue(progress.Any(static update => update.Details?.GetProperty("stream").GetString() == "stderr" && update.Delta.Contains("step 2", StringComparison.Ordinal)));

        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "step 1");
        StringAssert.Contains(output, "step 2");
    }

    [TestMethod]
    public async Task ShellCommandTool_TreatsNullOptionalArgumentsAsMissing()
    {
        using var temp = TestTempDirectory.Create();
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "shell_command");
        var command = OperatingSystem.IsWindows()
            ? "Write-Output 'null option handling'"
            : "printf 'null option handling\\n'";
        using var args = JsonDocument.Parse(
            $$"""
            {
              "command": {{JsonSerializer.Serialize(command)}},
              "workdir": null,
              "timeoutMs": null,
              "login": null
            }
            """);

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "exit_code: 0");
        StringAssert.Contains(output, "null option handling");
        StringAssert.Contains(output, temp.Path);
    }

    [TestMethod]
    public async Task ShellCommandTool_RespectsDeniedPermissionDecision()
    {
        using var temp = TestTempDirectory.Create();
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(
            temp.Path,
            onPermissionRequest: static (_, _) =>
                Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny))));
        var tool = tools.Single(static tool => tool.Spec.Name == "shell_command");
        using var args = JsonDocument.Parse("""{"command":"Write-Output 'should not run'"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("shell_command was denied by the host.", result.Error);
    }

    [TestMethod]
    public void ShellCommandProcessSpec_UsesPredictableProfileBehaviorPerPlatform()
    {
        using var temp = TestTempDirectory.Create();
        const string command = "echo hello";

        if (OperatingSystem.IsWindows())
        {
            var normal = InvokeCreateShellProcessSpec(command, temp.Path, login: false);
            var login = InvokeCreateShellProcessSpec(command, temp.Path, login: true);

            var normalArgs = normal.ArgumentList.Cast<string>().ToArray();
            var loginArgs = login.ArgumentList.Cast<string>().ToArray();

            CollectionAssert.AreEqual(new[] { "-NoProfile", "-Command" }, normalArgs.Take(2).ToArray());
            CollectionAssert.AreEqual(new[] { "-NoProfile", "-Command" }, loginArgs.Take(2).ToArray());
            StringAssert.Contains(normalArgs[2], command);
            StringAssert.Contains(normalArgs[2], "exit $LASTEXITCODE");
            StringAssert.Contains(loginArgs[2], command);
            StringAssert.Contains(loginArgs[2], "exit $LASTEXITCODE");
            return;
        }

        var originalShell = Environment.GetEnvironmentVariable("SHELL");
        try
        {
            Environment.SetEnvironmentVariable("SHELL", "/bin/bash");

            var normal = InvokeCreateShellProcessSpec(command, temp.Path, login: false);
            var login = InvokeCreateShellProcessSpec(command, temp.Path, login: true);

            CollectionAssert.AreEqual(new[] { "-c", command }, normal.ArgumentList.Cast<string>().ToArray());
            CollectionAssert.AreEqual(new[] { "-lc", command }, login.ArgumentList.Cast<string>().ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHELL", originalShell);
        }
    }

    [TestMethod]
    public void LocalAgentToolBridge_CreateDeclarations_PreservesSchemasAndUniqueNames()
    {
        AgentToolDefinition[] tools =
        [
            new(
                new AgentToolSpec("read_file", "Read a file", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
                (_, _) => Task.FromResult(new AgentToolResult(true, []))),
            new(
                new AgentToolSpec("read_file", "Read another file", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
                (_, _) => Task.FromResult(new AgentToolResult(true, []))),
        ];

        var declarations = LocalAgentToolBridge.CreateDeclarations(tools);
        var map = LocalAgentToolBridge.CreateDefinitionMap(tools);

        Assert.AreEqual(2, declarations.Count);
        CollectionAssert.AreEquivalent(new[] { "read_file", "read_file_2" }, map.Keys.ToArray());
        Assert.IsTrue(declarations.All(static declaration => declaration is not null));
    }

    [TestMethod]
    public void AgentToolSpec_RejectsInvalidToolNames()
    {
        using var schema = JsonDocument.Parse("""{"type":"object"}""");

        var dotted = Assert.ThrowsExactly<ArgumentException>(() => new AgentToolSpec("bad.tool", "Bad", schema.RootElement.Clone()));
        var spaced = Assert.ThrowsExactly<ArgumentException>(() => new AgentToolSpec("bad tool", "Bad", schema.RootElement.Clone()));

        StringAssert.Contains(dotted.Message, "^[a-zA-Z0-9_-]+$");
        StringAssert.Contains(spaced.Message, "^[a-zA-Z0-9_-]+$");
    }

    [TestMethod]
    public void LocalAgentToolBridge_CreateOpenAIStrictInputSchema_MakesOptionalFieldsNullableAndRequired()
    {
        using var temp = TestTempDirectory.Create();
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var readFile = tools.Single(static tool => tool.Spec.Name == "read_file");
        var grep = tools.Single(static tool => tool.Spec.Name == "grep");
        var webGet = tools.Single(static tool => tool.Spec.Name == "webget");
        var replaceInFile = tools.Single(static tool => tool.Spec.Name == "replace_in_file");
        var renameFileOrDir = tools.Single(static tool => tool.Spec.Name == "rename_file_or_dir");
        var shellCommand = tools.Single(static tool => tool.Spec.Name == "shell_command");
        var applyPatch = tools.Single(static tool => tool.Spec.Name == "apply_patch");

        var readFileSchema = LocalAgentToolBridge.CreateOpenAIStrictInputSchema(readFile.Spec.InputSchema);
        CollectionAssert.AreEquivalent(
            new[] { "path", "offset", "limit" },
            readFileSchema.GetProperty("required").EnumerateArray().Select(static item => item.GetString()).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "integer", "null" },
            readFileSchema.GetProperty("properties").GetProperty("offset").GetProperty("type").EnumerateArray().Select(static item => item.GetString()).ToArray());
        Assert.IsFalse(readFileSchema.GetProperty("properties").GetProperty("offset").TryGetProperty("minimum", out _));
        StringAssert.Contains(
            readFileSchema.GetProperty("properties").GetProperty("offset").GetProperty("description").GetString(),
            "negative value to count from the end");
        StringAssert.Contains(readFile.Spec.Description, "Omitting limit returns up to");
        StringAssert.Contains(
            readFileSchema.GetProperty("properties").GetProperty("offset").GetProperty("description").GetString(),
            "0 is invalid");
        StringAssert.Contains(readFile.Spec.Description, "Offsets past EOF return an empty text result");

        var grepSchema = LocalAgentToolBridge.CreateOpenAIStrictInputSchema(grep.Spec.InputSchema);
        StringAssert.Contains(grep.Spec.Description, "defaults to case-insensitive");
        StringAssert.Contains(grep.Spec.Description, "returns '(no matches)'");
        StringAssert.Contains(grep.Spec.Description, "one or more optional globs");
        var grepPathTypes = grepSchema.GetProperty("properties").GetProperty("path").GetProperty("type").EnumerateArray().Select(static item => item.GetString()).ToArray();
        Assert.IsTrue(grepPathTypes.Contains("array"));
        var grepGlobTypes = grepSchema.GetProperty("properties").GetProperty("glob").GetProperty("type").EnumerateArray().Select(static item => item.GetString()).ToArray();
        Assert.IsTrue(grepGlobTypes.Contains("array"));
        StringAssert.Contains(
            grepSchema.GetProperty("properties").GetProperty("caseSensitive").GetProperty("description").GetString(),
            "Defaults to false");

        var webGetSchema = LocalAgentToolBridge.CreateOpenAIStrictInputSchema(webGet.Spec.InputSchema);
        StringAssert.Contains(webGet.Spec.Description, "returns a text result containing the fetched body");
        StringAssert.Contains(webGet.Spec.Description, "set rawHtml=true to return raw HTML markup");
        StringAssert.Contains(webGet.Spec.Description, "includeHttpStatus=true");
        StringAssert.Contains(
            webGetSchema.GetProperty("properties").GetProperty("timeoutSeconds").GetProperty("description").GetString(),
            "Defaults to");
        StringAssert.Contains(
            webGetSchema.GetProperty("properties").GetProperty("url").GetProperty("description").GetString(),
            "HTML responses are simplified to plain text");
        StringAssert.Contains(
            webGetSchema.GetProperty("properties").GetProperty("rawHtml").GetProperty("description").GetString(),
            "Defaults to false");
        StringAssert.Contains(
            webGetSchema.GetProperty("properties").GetProperty("includeHttpStatus").GetProperty("description").GetString(),
            "Defaults to false");

        StringAssert.Contains(replaceInFile.Spec.Description, "leaves the file unchanged");
        StringAssert.Contains(
            LocalAgentToolBridge.CreateOpenAIStrictInputSchema(replaceInFile.Spec.InputSchema)
                .GetProperty("properties").GetProperty("replace_all").GetProperty("description").GetString(),
            "leaves the file unchanged");

        StringAssert.Contains(renameFileOrDir.Spec.Description, "Will not overwrite");
        StringAssert.Contains(
            LocalAgentToolBridge.CreateOpenAIStrictInputSchema(renameFileOrDir.Spec.InputSchema)
                .GetProperty("properties").GetProperty("new_path").GetProperty("description").GetString(),
            "Will not overwrite");

        StringAssert.Contains(shellCommand.Spec.Description, "Some hosts may auto-approve");
        StringAssert.Contains(shellCommand.Spec.Description, "exit_code");
        StringAssert.Contains(shellCommand.Spec.Description, "final external command exit code");

        var applyPatchSchema = LocalAgentToolBridge.CreateOpenAIStrictInputSchema(applyPatch.Spec.InputSchema);
        Assert.AreEqual("Use the `apply_patch` tool to edit files.", applyPatch.Spec.Description);
        StringAssert.Contains(
            applyPatchSchema.GetProperty("properties").GetProperty("input").GetProperty("description").GetString(),
            "*** Update File:");
    }

    [TestMethod]
    public void CreateDefaultTools_ExposesApplyPatchOnlyForOfficialOpenAIAndCodexSubscriptionProviders()
    {
        using var temp = TestTempDirectory.Create();

        var officialOpenAiTools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(
            temp.Path,
            provider: CreateProviderDescriptor(
                LocalAgentTransportKind.OpenAIResponses,
                new Uri("https://api.openai.com/v1/"))));
        var compatibleTools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(
            temp.Path,
            provider: CreateProviderDescriptor(
                LocalAgentTransportKind.OpenAIResponses,
                new Uri("https://openrouter.ai/api/v1"))));
        var codexSubscriptionTools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(
            temp.Path,
            provider: CreateProviderDescriptor(
                LocalAgentTransportKind.OpenAIResponses,
                new Uri("https://chatgpt.com/backend-api/codex"),
                protocolFamily: "codex")));
        var anthropicTools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(
            temp.Path,
            provider: CreateProviderDescriptor(
                LocalAgentTransportKind.AnthropicMessages,
                new Uri("https://api.anthropic.com/v1/messages"))));

        Assert.IsTrue(officialOpenAiTools.Any(static tool => tool.Spec.Name == "apply_patch"));
        Assert.IsTrue(codexSubscriptionTools.Any(static tool => tool.Spec.Name == "apply_patch"));
        Assert.IsFalse(compatibleTools.Any(static tool => tool.Spec.Name == "apply_patch"));
        Assert.IsFalse(anthropicTools.Any(static tool => tool.Spec.Name == "apply_patch"));
        Assert.IsFalse(officialOpenAiTools.Any(static tool => tool.Spec.Name == "request_user_input"));
        Assert.IsFalse(compatibleTools.Any(static tool => tool.Spec.Name == "request_user_input"));
        Assert.IsFalse(anthropicTools.Any(static tool => tool.Spec.Name == "request_user_input"));
        Assert.IsFalse(officialOpenAiTools.Any(static tool => tool.Spec.Name == "view_image"));
        Assert.IsFalse(compatibleTools.Any(static tool => tool.Spec.Name == "view_image"));
        Assert.IsFalse(anthropicTools.Any(static tool => tool.Spec.Name == "view_image"));
        CollectionAssert.IsSubsetOf(
            new[] { "write_file", "replace_in_file", "delete_file_or_dir", "rename_file_or_dir" },
            compatibleTools.Select(static tool => tool.Spec.Name).ToArray());
    }

    [TestMethod]
    public void CreateDefaultTools_AllowsProviderOverridesForBuiltInToolAvailability()
    {
        using var temp = TestTempDirectory.Create();
        var provider = CreateProviderDescriptor(
            LocalAgentTransportKind.OpenAIResponses,
            new Uri("https://openrouter.ai/api/v1"),
            new LocalAgentProviderProfile
            {
                BuiltInToolOverrides = new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    ["apply_patch"] = true,
                    ["shell_command"] = false,
                },
            });

        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path, provider: provider));

        Assert.IsTrue(tools.Any(static tool => tool.Spec.Name == "apply_patch"));
        Assert.IsFalse(tools.Any(static tool => tool.Spec.Name == "shell_command"));
    }

    [TestMethod]
    public async Task WriteAndReplaceTools_EditFilesDeterministically()
    {
        using var temp = TestTempDirectory.Create();
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(
            temp.Path,
            provider: CreateProviderDescriptor(LocalAgentTransportKind.AnthropicMessages, new Uri("https://api.anthropic.com/v1/messages"))));
        var writeFile = tools.Single(static tool => tool.Spec.Name == "write_file");
        var replaceInFile = tools.Single(static tool => tool.Spec.Name == "replace_in_file");

        using var writeArgs = JsonDocument.Parse("""{"path":"src/sample.txt","content":"alpha\nbeta\n"}""");
        var writeResult = await writeFile.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    writeFile.Spec.Name,
                    writeArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(writeResult.Success);
        Assert.AreEqual("alpha\nbeta\n", await File.ReadAllTextAsync(Path.Combine(temp.Path, "src", "sample.txt")).ConfigureAwait(false));

        using var replaceArgs = JsonDocument.Parse(
            """
            {
              "path": "src/sample.txt",
              "old_string": "alpha\r\nbeta\r\n",
              "new_string": "gamma\r\ndelta\r\n"
            }
            """);
        var replaceResult = await replaceInFile.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-2",
                    replaceInFile.Spec.Name,
                    replaceArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(replaceResult.Success);
        Assert.AreEqual("gamma\ndelta\n", await File.ReadAllTextAsync(Path.Combine(temp.Path, "src", "sample.txt")).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WriteFileTool_AllowsEmptyAndWhitespaceOnlyContent()
    {
        using var temp = TestTempDirectory.Create();
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "write_file");

        using var emptyArgs = JsonDocument.Parse("""{"path":"empty.txt","content":""}""");
        var emptyResult = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    emptyArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(emptyResult.Success);
        Assert.AreEqual(string.Empty, await File.ReadAllTextAsync(Path.Combine(temp.Path, "empty.txt")).ConfigureAwait(false));

        using var newlineArgs = JsonDocument.Parse("""{"path":"newlines.txt","content":"\n\n"}""");
        var newlineResult = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-2",
                    tool.Spec.Name,
                    newlineArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(newlineResult.Success);
        Assert.AreEqual("\n\n", await File.ReadAllTextAsync(Path.Combine(temp.Path, "newlines.txt")).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReplaceInFileTool_AllowsEmptyAndWhitespaceOnlyTextValues()
    {
        using var temp = TestTempDirectory.Create();
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "replace_in_file");

        var emptyReplacementPath = Path.Combine(temp.Path, "empty-replacement.txt");
        await File.WriteAllTextAsync(emptyReplacementPath, "alpha\nbeta\ngamma\n").ConfigureAwait(false);
        using var emptyReplacementArgs = JsonDocument.Parse(
            """
            {
              "path": "empty-replacement.txt",
              "old_string": "beta\n",
              "new_string": ""
            }
            """);

        var emptyReplacementResult = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    emptyReplacementArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(emptyReplacementResult.Success);
        Assert.AreEqual("alpha\ngamma\n", await File.ReadAllTextAsync(emptyReplacementPath).ConfigureAwait(false));

        var newlineReplacementPath = Path.Combine(temp.Path, "newline-replacement.txt");
        await File.WriteAllTextAsync(newlineReplacementPath, "alpha\nbeta\ngamma\n").ConfigureAwait(false);
        using var newlineReplacementArgs = JsonDocument.Parse(
            """
            {
              "path": "newline-replacement.txt",
              "old_string": "beta\n",
              "new_string": "\n\n"
            }
            """);

        var newlineReplacementResult = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-2",
                    tool.Spec.Name,
                    newlineReplacementArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(newlineReplacementResult.Success);
        Assert.AreEqual("alpha\n\n\ngamma\n", await File.ReadAllTextAsync(newlineReplacementPath).ConfigureAwait(false));

        var newlineSearchPath = Path.Combine(temp.Path, "newline-search.txt");
        await File.WriteAllTextAsync(newlineSearchPath, "alpha\n\nomega\n").ConfigureAwait(false);
        using var newlineSearchArgs = JsonDocument.Parse(
            """
            {
              "path": "newline-search.txt",
              "old_string": "\n\n",
              "new_string": "\n"
            }
            """);

        var newlineSearchResult = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-3",
                    tool.Spec.Name,
                    newlineSearchArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(newlineSearchResult.Success);
        Assert.AreEqual("alpha\nomega\n", await File.ReadAllTextAsync(newlineSearchPath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WriteAndReplaceTools_AcceptAbsolutePathsInsideWorkspace()
    {
        using var temp = TestTempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        var filePath = Path.Combine(temp.Path, "src", "sample.txt");
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var writeFile = tools.Single(static tool => tool.Spec.Name == "write_file");
        var replaceInFile = tools.Single(static tool => tool.Spec.Name == "replace_in_file");

        using var writeArgs = JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(filePath)}},"content":"alpha\nbeta\n"}""");
        var writeResult = await writeFile.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    writeFile.Spec.Name,
                    writeArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(writeResult.Success);

        using var replaceArgs = JsonDocument.Parse(
            $$"""
            {
              "path": {{JsonSerializer.Serialize(filePath)}},
              "old_string": "alpha",
              "new_string": "gamma"
            }
            """);
        var replaceResult = await replaceInFile.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-2",
                    replaceInFile.Spec.Name,
                    replaceArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(replaceResult.Success);
        Assert.AreEqual("gamma\nbeta\n", await File.ReadAllTextAsync(filePath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task WriteAndReplaceTools_AcceptRelativePathsOutsideWorkingDirectory()
    {
        using var temp = TestTempDirectory.Create();
        var workingDirectory = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(workingDirectory);
        var filePath = Path.Combine(temp.Path, "sibling", "sample.txt");
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(workingDirectory));
        var writeFile = tools.Single(static tool => tool.Spec.Name == "write_file");
        var replaceInFile = tools.Single(static tool => tool.Spec.Name == "replace_in_file");

        using var writeArgs = JsonDocument.Parse("""{"path":"../sibling/sample.txt","content":"alpha\nbeta\n"}""");
        var writeResult = await writeFile.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    writeFile.Spec.Name,
                    writeArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(writeResult.Success);
        Assert.AreEqual("alpha\nbeta\n", await File.ReadAllTextAsync(filePath).ConfigureAwait(false));

        using var replaceArgs = JsonDocument.Parse(
            """
            {
              "path": "../sibling/sample.txt",
              "old_string": "alpha",
              "new_string": "gamma"
            }
            """);
        var replaceResult = await replaceInFile.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-2",
                    replaceInFile.Spec.Name,
                    replaceArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(replaceResult.Success);
        Assert.AreEqual("gamma\nbeta\n", await File.ReadAllTextAsync(filePath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ReplaceInFileTool_FailsWhenMultipleMatchesExistAndReplaceAllIsFalse()
    {
        using var temp = TestTempDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "sample.txt"), "dup\nkeep\ndup\n").ConfigureAwait(false);
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "replace_in_file");
        using var args = JsonDocument.Parse("""{"path":"sample.txt","old_string":"dup","new_string":"new"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "replace_all=true");
    }

    [TestMethod]
    public async Task ReplaceInFileTool_RejectsBinaryFiles()
    {
        using var temp = TestTempDirectory.Create();
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "sample.bin"), [0x41, 0x00, 0x42]).ConfigureAwait(false);
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var tool = tools.Single(static tool => tool.Spec.Name == "replace_in_file");
        using var args = JsonDocument.Parse("""{"path":"sample.bin","old_string":"A","new_string":"B"}""");

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "binary files");
    }

    [TestMethod]
    public async Task DeleteAndRenameTools_ModifyWorkingDirectoryEntries()
    {
        using var temp = TestTempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(temp.Path, "old-dir"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "old-dir", "data.txt"), "hello").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "delete-me.txt"), "bye").ConfigureAwait(false);
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var rename = tools.Single(static tool => tool.Spec.Name == "rename_file_or_dir");
        var delete = tools.Single(static tool => tool.Spec.Name == "delete_file_or_dir");

        using var renameArgs = JsonDocument.Parse("""{"old_path":"old-dir","new_path":"new-dir"}""");
        var renameResult = await rename.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    rename.Spec.Name,
                    renameArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(renameResult.Success);
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "old-dir")));
        Assert.IsTrue(File.Exists(Path.Combine(temp.Path, "new-dir", "data.txt")));

        using var deleteArgs = JsonDocument.Parse("""{"path":"delete-me.txt"}""");
        var deleteResult = await delete.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-2",
                    delete.Spec.Name,
                    deleteArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(deleteResult.Success);
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "delete-me.txt")));
    }

    [TestMethod]
    public async Task DeleteAndRenameTools_AcceptAbsolutePathsInsideWorkspace()
    {
        using var temp = TestTempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "old-dir");
        var destinationPath = Path.Combine(temp.Path, "new-dir");
        Directory.CreateDirectory(sourcePath);
        await File.WriteAllTextAsync(Path.Combine(sourcePath, "data.txt"), "hello").ConfigureAwait(false);
        var deletePath = Path.Combine(temp.Path, "delete-me.txt");
        await File.WriteAllTextAsync(deletePath, "bye").ConfigureAwait(false);
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var rename = tools.Single(static tool => tool.Spec.Name == "rename_file_or_dir");
        var delete = tools.Single(static tool => tool.Spec.Name == "delete_file_or_dir");

        using var renameArgs = JsonDocument.Parse(
            $$"""{"old_path":{{JsonSerializer.Serialize(sourcePath)}},"new_path":{{JsonSerializer.Serialize(destinationPath)}}}""");
        var renameResult = await rename.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    rename.Spec.Name,
                    renameArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(renameResult.Success);
        Assert.IsTrue(File.Exists(Path.Combine(destinationPath, "data.txt")));

        using var deleteArgs = JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(deletePath)}}}""");
        var deleteResult = await delete.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-2",
                    delete.Spec.Name,
                    deleteArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(deleteResult.Success);
        Assert.IsFalse(File.Exists(deletePath));
    }

    [TestMethod]
    public async Task DeleteAndRenameTools_AcceptRelativePathsOutsideWorkingDirectory()
    {
        using var temp = TestTempDirectory.Create();
        var workingDirectory = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(workingDirectory);
        var sourcePath = Path.Combine(temp.Path, "sibling-old");
        var destinationPath = Path.Combine(temp.Path, "sibling-new");
        Directory.CreateDirectory(sourcePath);
        await File.WriteAllTextAsync(Path.Combine(sourcePath, "data.txt"), "hello").ConfigureAwait(false);
        var deletePath = Path.Combine(temp.Path, "delete-me.txt");
        await File.WriteAllTextAsync(deletePath, "bye").ConfigureAwait(false);
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(workingDirectory));
        var rename = tools.Single(static tool => tool.Spec.Name == "rename_file_or_dir");
        var delete = tools.Single(static tool => tool.Spec.Name == "delete_file_or_dir");

        using var renameArgs = JsonDocument.Parse("""{"old_path":"../sibling-old","new_path":"../sibling-new"}""");
        var renameResult = await rename.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    rename.Spec.Name,
                    renameArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(renameResult.Success);
        Assert.IsFalse(Directory.Exists(sourcePath));
        Assert.IsTrue(File.Exists(Path.Combine(destinationPath, "data.txt")));

        using var deleteArgs = JsonDocument.Parse("""{"path":"../delete-me.txt"}""");
        var deleteResult = await delete.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-2",
                    delete.Spec.Name,
                    deleteArgs.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(deleteResult.Success);
        Assert.IsFalse(File.Exists(deletePath));
    }

    [TestMethod]
    public async Task ApplyPatchTool_CreatesUpdatesMovesAndDeletesFiles()
    {
        using var temp = TestTempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "Program.cs"), "console.log(\"old\");" + Environment.NewLine).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "obsolete.txt"), "obsolete" + Environment.NewLine).ConfigureAwait(false);

        AgentPermissionRequest? observedRequest = null;
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(
            temp.Path,
            onPermissionRequest: (request, _) =>
            {
                observedRequest = request;
                return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));
            }));
        var tool = tools.Single(static tool => tool.Spec.Name == "apply_patch");
        using var args = JsonDocument.Parse(
            """
            {
              "input": "*** Begin Patch\n*** Add File: notes.txt\n+hello\n*** Update File: src/Program.cs\n*** Move to: src/Main.cs\n@@\n-console.log(\"old\");\n+console.log(\"new\");\n*** Delete File: obsolete.txt\n*** End Patch"
            }
            """);

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(observedRequest);
        Assert.AreEqual("fileChange", observedRequest.Kind);
        Assert.IsTrue(result.Success);
        Assert.IsTrue(File.Exists(Path.Combine(temp.Path, "notes.txt")));
        Assert.AreEqual("hello\n", await File.ReadAllTextAsync(Path.Combine(temp.Path, "notes.txt")).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "src", "Program.cs")));
        Assert.AreEqual(
            "console.log(\"new\");" + Environment.NewLine,
            await File.ReadAllTextAsync(Path.Combine(temp.Path, "src", "Main.cs")).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "obsolete.txt")));

        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "A notes.txt");
        StringAssert.Contains(output, "R src/Program.cs -> src/Main.cs");
        StringAssert.Contains(output, "D obsolete.txt");
    }

    [TestMethod]
    public async Task ApplyPatchTool_RespectsDeniedPermissionDecision()
    {
        using var temp = TestTempDirectory.Create();
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(
            temp.Path,
            onPermissionRequest: static (_, _) =>
                Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny))));
        var tool = tools.Single(static tool => tool.Spec.Name == "apply_patch");
        using var args = JsonDocument.Parse(
            """
            {
              "input": "*** Begin Patch\n*** Add File: notes.txt\n+hello\n*** End Patch"
            }
            """);

        var result = await tool.Handler(
                new AgentToolInvocation(
                    AgentBackendIds.OpenAIResponses,
                    "session-1",
                    "tool-1",
                    tool.Spec.Name,
                    args.RootElement.Clone()),
                CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("apply_patch was denied by the host.", result.Error);
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "notes.txt")));
    }

    private static LocalAgentBuiltInToolOptions CreateOptions(
        string workingDirectory,
        HttpClient? httpClient = null,
        AgentUserInputRequestHandler? onUserInputRequest = null,
        AgentPermissionRequestHandler? onPermissionRequest = null,
        ModelProviderRuntimeDescriptor? provider = null,
        TimeSpan? webGetTimeout = null)
    {
        return new LocalAgentBuiltInToolOptions
        {
            BackendId = AgentBackendIds.OpenAIResponses,
            SessionId = "session-1",
            WorkingDirectory = workingDirectory,
            HttpClient = httpClient,
            OnPermissionRequest = onPermissionRequest ?? ((_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce))),
            OnUserInputRequest = onUserInputRequest,
            Provider = provider ?? CreateProviderDescriptor(LocalAgentTransportKind.OpenAIResponses, null),
            WebGetTimeout = webGetTimeout ?? TimeSpan.FromSeconds(20),
        };
    }

    private static ModelProviderRuntimeDescriptor CreateProviderDescriptor(
        LocalAgentTransportKind transportKind,
        Uri? baseUri,
        LocalAgentProviderProfile? profile = null,
        string? protocolFamily = null)
    {
        return new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = protocolFamily ?? transportKind.ToString(),
            ProviderKey = "provider-1",
            DisplayName = "Provider 1",
            TransportKind = transportKind,
            BaseUri = baseUri,
            Profile = profile,
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(HttpResponseMessage response)
            : this((_, _) => Task.FromResult(response))
        {
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    private static ProcessStartInfo InvokeCreateShellProcessSpec(string command, string workdir, bool login)
    {
        var method = typeof(LocalAgentBuiltInToolFactory).GetMethod(
            "CreateShellProcessSpec",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(method);

        var shellProcessSpec = method.Invoke(null, [command, workdir, login]);
        Assert.IsNotNull(shellProcessSpec);

        var startInfoProperty = shellProcessSpec.GetType().GetProperty("StartInfo");
        Assert.IsNotNull(startInfoProperty);

        return Assert.IsInstanceOfType<ProcessStartInfo>(startInfoProperty.GetValue(shellProcessSpec));
    }
}
