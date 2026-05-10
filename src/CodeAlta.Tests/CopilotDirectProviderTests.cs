using System.Net;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.CopilotDirect;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CopilotDirectProviderTests
{
    [TestMethod]
    public void ValidateGlobalConfigContent_AcceptsCopilotDirectProvider()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.github-copilot]
            type = "github-copilot-direct"
            experimental = true
            auth_source = "github_token_env"
            github_token_env = "GITHUB_TOKEN"
            model_discovery = "copilot_endpoint_with_static_fallback"
            """);

        Assert.IsTrue(result.IsValid, result.Message);
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_RejectsReservedCopilotDirectProviderKey()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.copilot]
            type = "github-copilot-direct"
            experimental = true
            auth_source = "copilot_token_env"
            copilot_token_env = "COPILOT_TOKEN"
            """);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Message, "providers.copilot type must be 'copilot'");
    }

    [TestMethod]
    public async Task CopilotDirect_ListModels_FiltersDisabledAndUnsupportedModels()
    {
        var handler = new StubHandler(request =>
        {
            Assert.AreEqual("Bearer test-token", request.Headers.Authorization?.ToString());
            Assert.AreEqual(new Uri("https://api.individual.githubcopilot.com/models"), request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        {
                          "model_picker_enabled": true,
                          "id": "gpt-test",
                          "name": "GPT Test",
                          "version": "gpt-test-2026-01-01",
                          "supported_endpoints": ["/chat/completions"],
                          "policy": { "state": "enabled" },
                          "capabilities": {
                            "family": "gpt",
                            "limits": { "max_context_window_tokens": 1000, "max_output_tokens": 100, "max_prompt_tokens": 900 },
                            "supports": { "streaming": true, "tool_calls": true, "reasoning_effort": ["low", "medium"] }
                          }
                        },
                        {
                          "model_picker_enabled": false,
                          "id": "hidden",
                          "name": "Hidden",
                          "version": "hidden-2026-01-01",
                          "supported_endpoints": ["/chat/completions"],
                          "capabilities": { "family": "gpt", "limits": {}, "supports": { "streaming": true, "tool_calls": false } }
                        },
                        {
                          "model_picker_enabled": true,
                          "id": "disabled",
                          "name": "Disabled",
                          "version": "disabled-2026-01-01",
                          "supported_endpoints": ["/chat/completions"],
                          "policy": { "state": "disabled" },
                          "capabilities": { "family": "gpt", "limits": {}, "supports": { "streaming": true, "tool_calls": false } }
                        },
                        {
                          "model_picker_enabled": true,
                          "id": "unsupported",
                          "name": "Unsupported",
                          "version": "unsupported-2026-01-01",
                          "supported_endpoints": ["/unsupported"],
                          "policy": { "state": "enabled" },
                          "capabilities": { "limits": {}, "supports": { "streaming": true, "tool_calls": false } }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var backend = new CopilotDirectAgentBackend(new CopilotDirectAgentBackendOptions
        {
            StateRootPath = Path.GetTempPath(),
            Providers =
            {
                new CopilotDirectProviderOptions
                {
                    ProviderKey = "github-copilot",
                    Auth = new CopilotDirectAuthOptions
                    {
                        AuthSource = CopilotDirectAuthSources.CopilotTokenEnvironment,
                        CopilotTokenEnvironmentVariable = "CODEALTA_TEST_COPILOT_TOKEN",
                    },
                    HttpClient = new HttpClient(handler),
                },
            },
        });

        Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", "test-token");
        try
        {
            var models = await backend.ListModelsAsync().ConfigureAwait(false);

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("gpt-test", models[0].Id);
            Assert.AreEqual("GPT Test", models[0].DisplayName);
            Assert.AreEqual("ChatCompletions", models[0].Capabilities!["copilotEndpointKind"]?.ToString());
            CollectionAssert.AreEqual(
                new[] { AgentReasoningEffort.Low, AgentReasoningEffort.Medium },
                models[0].SupportedReasoningEfforts!.ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", null);
            await backend.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CopilotDirect_GitHubTokenAuth_ExchangesTokenAndUsesEndpointApi()
    {
        var exchanged = false;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri == new Uri("https://api.github.com/copilot_internal/v2/token"))
            {
                exchanged = true;
                Assert.AreEqual("Bearer github-token", request.Headers.Authorization?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"token":"copilot-token","expires_at":4102444800,"endpoints":{"api":"https://copilot.test"}}
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            Assert.AreEqual(new Uri("https://copilot.test/models"), request.RequestUri);
            Assert.AreEqual("Bearer copilot-token", request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
            };
        });
        var backend = new CopilotDirectAgentBackend(new CopilotDirectAgentBackendOptions
        {
            StateRootPath = Path.GetTempPath(),
            Providers =
            {
                new CopilotDirectProviderOptions
                {
                    ProviderKey = "github-copilot",
                    Auth = new CopilotDirectAuthOptions
                    {
                        AuthSource = CopilotDirectAuthSources.GitHubTokenEnvironment,
                        GitHubTokenEnvironmentVariable = "CODEALTA_TEST_GITHUB_TOKEN",
                    },
                    HttpClient = new HttpClient(handler),
                    ModelDiscovery = CopilotDirectModelDiscoveryModes.Endpoint,
                },
            },
        });

        Environment.SetEnvironmentVariable("CODEALTA_TEST_GITHUB_TOKEN", "github-token");
        try
        {
            _ = await backend.ListModelsAsync().ConfigureAwait(false);
            Assert.IsTrue(exchanged);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_TEST_GITHUB_TOKEN", null);
            await backend.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CopilotDirect_ChatCompletions_SendsCopilotHeadersAndStreamsText()
    {
        var handler = new StubHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual(new Uri("https://api.individual.githubcopilot.com/chat/completions"), request.RequestUri);
            Assert.AreEqual("Bearer test-token", request.Headers.Authorization?.ToString());
            Assert.IsTrue(request.Headers.TryGetValues("X-Initiator", out var initiator));
            Assert.AreEqual("user", initiator.Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    data: {"id":"chatcmpl-test","model":"gpt-test","choices":[{"delta":{"content":"Hello"}}]}

                    data: {"id":"chatcmpl-test","model":"gpt-test","choices":[{"delta":{"content":" world"}}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        });
        var backend = new CopilotDirectAgentBackend(new CopilotDirectAgentBackendOptions
        {
            StateRootPath = Path.GetTempPath(),
            Providers =
            {
                new CopilotDirectProviderOptions
                {
                    ProviderKey = "github-copilot",
                    Auth = new CopilotDirectAuthOptions
                    {
                        AuthSource = CopilotDirectAuthSources.CopilotTokenEnvironment,
                        CopilotTokenEnvironmentVariable = "CODEALTA_TEST_COPILOT_TOKEN",
                    },
                    HttpClient = new HttpClient(handler),
                },
            },
        });

        Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", "test-token");
        try
        {
            await using var session = await backend.CreateSessionAsync(new AgentSessionCreateOptions
            {
                Model = "gpt-test",
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
            }).ConfigureAwait(false);
            var events = new List<AgentEvent>();
            using var subscription = session.Subscribe(events.Add);
            _ = await session.SendAsync(new AgentSendOptions
            {
                Input = AgentInput.Text("Say hello"),
            }, CancellationToken.None).ConfigureAwait(false);

            var completed = events.OfType<AgentContentCompletedEvent>()
                .Where(static ev => ev.Kind == AgentContentKind.Assistant)
                .ToArray();
            Assert.IsTrue(completed.Any(static ev => ev.Content.Contains("Hello world", StringComparison.Ordinal)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", null);
            await backend.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
