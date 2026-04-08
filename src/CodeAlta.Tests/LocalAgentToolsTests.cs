using System.Net;
using System.Net.Http;
using System.Text.Json;
using CodeAlta.Agent;
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
    public async Task RequestUserInputTool_DelegatesToConfiguredHandler()
    {
        AgentUserInputRequest? observed = null;
        var options = CreateOptions(
            Environment.CurrentDirectory,
            onUserInputRequest: (request, _) =>
            {
                observed = request;
                return Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>
                {
                    ["provider"] = "openai",
                }));
            });
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(options);
        var tool = tools.Single(static tool => tool.Spec.Name == "request_user_input");
        using var args = JsonDocument.Parse(
            """
            {
              "prompts": [
                {
                  "id": "provider",
                  "question": "Pick a provider",
                  "header": "Provider",
                  "allowFreeform": false,
                  "options": [
                    { "label": "OpenAI", "description": "Use OpenAI." }
                  ]
                }
              ]
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

        Assert.IsNotNull(observed);
        Assert.AreEqual("provider", observed.Form.Prompts.Single().Id);
        Assert.IsTrue(result.Success);
        var output = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items.Single()).Value;
        StringAssert.Contains(output, "\"provider\":\"openai\"");
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
    public void LocalAgentToolBridge_CreateDeclarations_PreservesSchemasAndUniqueNames()
    {
        AgentToolDefinition[] tools =
        [
            new(
                new AgentToolSpec("read_file", "Read a file", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
                (_, _) => Task.FromResult(new AgentToolResult(true, []))),
            new(
                new AgentToolSpec("read file", "Read another file", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()),
                (_, _) => Task.FromResult(new AgentToolResult(true, []))),
        ];

        var declarations = LocalAgentToolBridge.CreateDeclarations(tools);
        var map = LocalAgentToolBridge.CreateDefinitionMap(tools);

        Assert.AreEqual(2, declarations.Count);
        CollectionAssert.AreEquivalent(new[] { "read_file", "read_file_2" }, map.Keys.ToArray());
        Assert.IsTrue(declarations.All(static declaration => declaration is not null));
    }

    [TestMethod]
    public void LocalAgentToolBridge_CreateOpenAIStrictInputSchema_MakesOptionalFieldsNullableAndRequired()
    {
        using var temp = TestTempDirectory.Create();
        var tools = LocalAgentBuiltInToolFactory.CreateDefaultTools(CreateOptions(temp.Path));
        var readFile = tools.Single(static tool => tool.Spec.Name == "read_file");
        var requestUserInput = tools.Single(static tool => tool.Spec.Name == "request_user_input");

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
            "minimum: 1");

        var requestUserInputSchema = LocalAgentToolBridge.CreateOpenAIStrictInputSchema(requestUserInput.Spec.InputSchema);
        var optionSchema = requestUserInputSchema
            .GetProperty("properties")
            .GetProperty("prompts")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("options")
            .GetProperty("items");
        CollectionAssert.AreEquivalent(
            new[] { "label", "description" },
            optionSchema.GetProperty("required").EnumerateArray().Select(static item => item.GetString()).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "string", "null" },
            optionSchema.GetProperty("properties").GetProperty("description").GetProperty("type").EnumerateArray().Select(static item => item.GetString()).ToArray());
        Assert.IsFalse(requestUserInputSchema.GetProperty("properties").GetProperty("prompts").GetProperty("items").TryGetProperty("additionalProperties", out var nestedAdditionalProperties) && nestedAdditionalProperties.ValueKind != JsonValueKind.False);
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
        Assert.AreEqual("hello" + Environment.NewLine, await File.ReadAllTextAsync(Path.Combine(temp.Path, "notes.txt")).ConfigureAwait(false));
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
        AgentPermissionRequestHandler? onPermissionRequest = null)
    {
        return new LocalAgentBuiltInToolOptions
        {
            BackendId = AgentBackendIds.OpenAIResponses,
            SessionId = "session-1",
            WorkingDirectory = workingDirectory,
            HttpClient = httpClient,
            OnPermissionRequest = onPermissionRequest ?? ((_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce))),
            OnUserInputRequest = onUserInputRequest,
        };
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
