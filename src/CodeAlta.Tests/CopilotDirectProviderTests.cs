using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.Runtime;
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
            type = "copilot"
            experimental = true
            auth_source = "github_token_env"
            github_token_env = "GITHUB_TOKEN"
            model_discovery = "copilot_endpoint_with_static_fallback"
            """);

        Assert.IsTrue(result.IsValid, result.Message);
    }

    [TestMethod]
    public void ValidateGlobalConfigContent_MapsLegacyReservedCopilotTypeToCli()
    {
        var result = CodeAltaConfigStore.ValidateGlobalConfigContent(
            """
            [providers.copilot]
            type = "copilot"
            """);

        Assert.IsTrue(result.IsValid, result.Message);
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
        var providerRuntime = new CopilotDirectModelProviderRuntime(new CopilotDirectModelProviderRuntimeOptions
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
            var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);

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
            await providerRuntime.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CopilotDirect_ListModels_PrefersAnthropicMessagesEndpointForClaudeModels()
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
                          "id": "claude-haiku-4.5",
                          "name": "Claude Haiku 4.5",
                          "version": "claude-haiku-4.5-2026-01-01",
                          "supported_endpoints": ["/chat/completions", "/v1/messages"],
                          "policy": { "state": "enabled" },
                          "capabilities": {
                            "family": "claude",
                            "limits": { "max_context_window_tokens": 1000, "max_output_tokens": 100, "max_prompt_tokens": 900 },
                            "supports": { "streaming": true, "tool_calls": true }
                          }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var providerRuntime = new CopilotDirectModelProviderRuntime(new CopilotDirectModelProviderRuntimeOptions
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
            var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("claude-haiku-4.5", models[0].Id);
            Assert.AreEqual("AnthropicMessages", models[0].Capabilities!["copilotEndpointKind"]?.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", null);
            await providerRuntime.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CopilotDirect_ListModels_MapsGeminiToOpenAIChatWithoutReasoningEffort()
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
                          "id": "gemini-2.5-pro",
                          "name": "Gemini 2.5 Pro",
                          "version": "gemini-2.5-pro-2026-01-01",
                          "supported_endpoints": ["/chat/completions"],
                          "policy": { "state": "enabled" },
                          "capabilities": {
                            "family": "gemini",
                            "limits": { "max_context_window_tokens": 1000, "max_output_tokens": 100, "max_prompt_tokens": 900 },
                            "supports": { "streaming": true, "tool_calls": true, "vision": true }
                          }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var providerRuntime = new CopilotDirectModelProviderRuntime(new CopilotDirectModelProviderRuntimeOptions
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
            var models = await providerRuntime.ListModelsAsync().ConfigureAwait(false);

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("gemini-2.5-pro", models[0].Id);
            Assert.AreEqual("ChatCompletions", models[0].Capabilities!["copilotEndpointKind"]?.ToString());
            Assert.AreEqual(0, models[0].SupportedReasoningEfforts!.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", null);
            await providerRuntime.DisposeAsync().ConfigureAwait(false);
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
        var providerRuntime = new CopilotDirectModelProviderRuntime(new CopilotDirectModelProviderRuntimeOptions
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
            _ = await providerRuntime.ListModelsAsync().ConfigureAwait(false);
            Assert.IsTrue(exchanged);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_TEST_GITHUB_TOKEN", null);
            await providerRuntime.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CopilotDirectLoginManager_DeviceFlow_StoresCachedCredential()
    {
        using var temp = TestTempDirectory.Create();
        var tokenPolls = 0;
        var observedDeviceCode = default(CopilotDirectDeviceCode);
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri == new Uri("https://github.com/login/device/code"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"device_code":"device-1","user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device","expires_in":60,"interval":5}
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (request.RequestUri == new Uri("https://github.com/login/oauth/access_token"))
            {
                tokenPolls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"github-token\"}", Encoding.UTF8, "application/json"),
                };
            }

            Assert.AreEqual(new Uri("https://api.github.com/copilot_internal/v2/token"), request.RequestUri);
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
        });
        var manager = new CopilotDirectLoginManager(new HttpClient(handler));

        var result = await manager.LoginWithDeviceCodeAsync(
            new CopilotDirectLoginOptions("github-copilot", temp.Path)
            {
                PollingIntervalOverride = TimeSpan.FromMilliseconds(1),
            },
            (deviceCode, _) =>
            {
                observedDeviceCode = deviceCode;
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        Assert.IsNotNull(observedDeviceCode);
        Assert.AreEqual("ABCD-EFGH", observedDeviceCode!.UserCode);
        Assert.AreEqual(new Uri("https://copilot.test"), result.BaseUri);
        Assert.AreEqual(1, tokenPolls);

        var status = await manager.GetCredentialStatusAsync(new CopilotDirectLoginOptions("github-copilot", temp.Path)).ConfigureAwait(false);
        Assert.IsNotNull(status);
        Assert.AreEqual(new Uri("https://copilot.test"), status!.BaseUri);
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
        var providerRuntime = new CopilotDirectModelProviderRuntime(new CopilotDirectModelProviderRuntimeOptions
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
            await using var session = await providerRuntime.CreateSessionAsync(new AgentSessionCreateOptions
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
            await providerRuntime.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CopilotDirect_AnthropicMessages_UsesNonStreamingCompatibilityAndKeepsSharedHttpClientUsable()
    {
        var sawAnthropicRequest = false;
        var sawModelDiscoveryRequest = false;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri == new Uri("https://api.individual.githubcopilot.com/v1/messages"))
            {
                sawAnthropicRequest = true;
                Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual("Bearer test-token", request.Headers.Authorization?.ToString());
                Assert.IsTrue(request.Headers.TryGetValues("X-Initiator", out var initiator));
                Assert.AreEqual("user", initiator.Single());
                var requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.IsFalse(requestJson.Contains("\"stream\":true", StringComparison.OrdinalIgnoreCase));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "id": "msg_test",
                          "type": "message",
                          "role": "assistant",
                          "model": "claude-haiku-4.5",
                          "content": [{ "type": "text", "text": "Hello from Haiku" }],
                          "stop_reason": "end_turn",
                          "stop_sequence": null,
                          "usage": { "input_tokens": 3, "output_tokens": 4 }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (request.RequestUri == new Uri("https://api.individual.githubcopilot.com/models"))
            {
                sawModelDiscoveryRequest = true;
                Assert.AreEqual("Bearer test-token", request.Headers.Authorization?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
                };
            }

            Assert.Fail($"Unexpected request: {request.Method} {request.RequestUri}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var executor = new CopilotDirectTurnExecutor(new CopilotDirectProviderOptions
        {
            ProviderKey = "github-copilot",
            Auth = new CopilotDirectAuthOptions
            {
                AuthSource = CopilotDirectAuthSources.CopilotTokenEnvironment,
                CopilotTokenEnvironmentVariable = "CODEALTA_TEST_COPILOT_TOKEN",
            },
            HttpClient = new HttpClient(handler),
        });
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = CopilotDirectModelProviderRuntime.ProtocolFamily,
            ProviderKey = "github-copilot",
            DisplayName = "Copilot",
            TransportKind = AgentTransportKind.AnthropicMessages,
            BaseUri = new Uri("https://api.individual.githubcopilot.com"),
        };

        Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", "test-token");
        try
        {
            var response = await executor.ExecuteTurnAsync(
                new AgentTurnRequest
                {
                    Provider = provider,
                    ProviderId = new ModelProviderId(provider.ProviderKey),
                    SessionId = "session-test",
                    RunId = new AgentRunId("run-test"),
                    ModelId = "claude-haiku-4.5",
                    ModelInfo = new AgentModelInfo(
                        "claude-haiku-4.5",
                        Provider: "github-copilot",
                        Capabilities: new Dictionary<string, object?>
                        {
                            ["copilotEndpointKind"] = "AnthropicMessages",
                            ["family"] = "claude",
                        }),
                    Conversation =
                    [
                        new AgentConversationMessage(
                            AgentConversationRole.User,
                            [new AgentMessagePart.Text("test")]),
                    ],
                    Tools = [],
                    State = new AgentSessionState
                    {
                        SessionId = "session-test",
                        ProtocolFamily = CopilotDirectModelProviderRuntime.ProtocolFamily,
                        ProviderKey = "github-copilot",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                },
                static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

            Assert.IsTrue(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>()
                .Any(static part => part.Value.Contains("Hello from Haiku", StringComparison.Ordinal)));

            _ = await executor.ListModelsAsync(provider).ConfigureAwait(false);
            Assert.IsTrue(sawAnthropicRequest);
            Assert.IsTrue(sawModelDiscoveryRequest);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", null);
        }
    }

    [TestMethod]
    public async Task CopilotDirect_AnthropicMessages_AcceptsTokenWithReservedHeaderCharacters()
    {
        // Regression test: GitHub Copilot bearer tokens contain ';' and ',' separators
        // that are rejected by HttpRequestHeaders.Add validation but accepted by the
        // Authorization setter. The provider runtime must route these tokens through the SDK's
        // Credentials pipeline rather than ClientOptions.AuthToken to avoid throwing
        // "The format of value 'Bearer ...' is invalid." while sending the prompt.
        const string CopilotToken = "tid=aaaa;ol=bbbb,cccc;exp=1;iat=2;chat=1";

        var sawAnthropicRequest = false;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri == new Uri("https://api.individual.githubcopilot.com/v1/messages"))
            {
                sawAnthropicRequest = true;
                Assert.AreEqual($"Bearer {CopilotToken}", request.Headers.Authorization?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "id": "msg_test",
                          "type": "message",
                          "role": "assistant",
                          "model": "claude-haiku-4.5",
                          "content": [{ "type": "text", "text": "ok" }],
                          "stop_reason": "end_turn",
                          "stop_sequence": null,
                          "usage": { "input_tokens": 1, "output_tokens": 1 }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            Assert.Fail($"Unexpected request: {request.Method} {request.RequestUri}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var executor = new CopilotDirectTurnExecutor(new CopilotDirectProviderOptions
        {
            ProviderKey = "github-copilot",
            Auth = new CopilotDirectAuthOptions
            {
                AuthSource = CopilotDirectAuthSources.CopilotTokenEnvironment,
                CopilotTokenEnvironmentVariable = "CODEALTA_TEST_COPILOT_TOKEN",
            },
            HttpClient = new HttpClient(handler),
        });
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = CopilotDirectModelProviderRuntime.ProtocolFamily,
            ProviderKey = "github-copilot",
            DisplayName = "Copilot",
            TransportKind = AgentTransportKind.AnthropicMessages,
            BaseUri = new Uri("https://api.individual.githubcopilot.com"),
        };

        Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", CopilotToken);
        try
        {
            var response = await executor.ExecuteTurnAsync(
                new AgentTurnRequest
                {
                    Provider = provider,
                    ProviderId = new ModelProviderId(provider.ProviderKey),
                    SessionId = "session-test",
                    RunId = new AgentRunId("run-test"),
                    ModelId = "claude-haiku-4.5",
                    ModelInfo = new AgentModelInfo(
                        "claude-haiku-4.5",
                        Provider: "github-copilot",
                        Capabilities: new Dictionary<string, object?>
                        {
                            ["copilotEndpointKind"] = "AnthropicMessages",
                            ["family"] = "claude",
                        }),
                    Conversation =
                    [
                        new AgentConversationMessage(
                            AgentConversationRole.User,
                            [new AgentMessagePart.Text("hi")]),
                    ],
                    Tools = [],
                    State = new AgentSessionState
                    {
                        SessionId = "session-test",
                        ProtocolFamily = CopilotDirectModelProviderRuntime.ProtocolFamily,
                        ProviderKey = "github-copilot",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                },
                static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

            Assert.IsTrue(sawAnthropicRequest);
            Assert.IsTrue(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Any());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", null);
        }
    }

    [TestMethod]
    public async Task CopilotDirect_ChatCompletions_OmitsReasoningAndDeveloperRoleForGeminiCompat()
    {
        var handler = new StubHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual(new Uri("https://api.individual.githubcopilot.com/chat/completions"), request.RequestUri);
            using (var requestDocument = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()))
            {
                Assert.IsFalse(requestDocument.RootElement.TryGetProperty("reasoning_effort", out _));
                var messages = requestDocument.RootElement.GetProperty("messages");
                Assert.IsFalse(messages.EnumerateArray().Any(static message =>
                    string.Equals(message.GetProperty("role").GetString(), "developer", StringComparison.Ordinal)));
                Assert.IsTrue(messages.GetRawText().Contains("Developer instructions", StringComparison.Ordinal));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    data: {"id":"chatcmpl-test","model":"gemini-2.5-pro","choices":[{"delta":{"content":"Hello"}}]}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        });
        var executor = new CopilotDirectTurnExecutor(new CopilotDirectProviderOptions
        {
            ProviderKey = "github-copilot",
            Auth = new CopilotDirectAuthOptions
            {
                AuthSource = CopilotDirectAuthSources.CopilotTokenEnvironment,
                CopilotTokenEnvironmentVariable = "CODEALTA_TEST_COPILOT_TOKEN",
            },
            HttpClient = new HttpClient(handler),
        });
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = CopilotDirectModelProviderRuntime.ProtocolFamily,
            ProviderKey = "github-copilot",
            DisplayName = "Copilot",
            TransportKind = AgentTransportKind.OpenAIChatCompletions,
            BaseUri = new Uri("https://api.individual.githubcopilot.com"),
        };

        Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", "test-token");
        try
        {
            _ = await executor.ExecuteTurnAsync(
                new AgentTurnRequest
                {
                    Provider = provider,
                    ProviderId = new ModelProviderId(provider.ProviderKey),
                    SessionId = "session-test",
                    RunId = new AgentRunId("run-test"),
                    ModelId = "gemini-2.5-pro",
                    ReasoningEffort = AgentReasoningEffort.Low,
                    SystemMessage = "System instructions",
                    DeveloperInstructions = "Developer instructions",
                    ModelInfo = new AgentModelInfo(
                        "gemini-2.5-pro",
                        Provider: "github-copilot",
                        SupportedReasoningEfforts: [],
                        Capabilities: new Dictionary<string, object?>
                        {
                            ["copilotEndpointKind"] = "ChatCompletions",
                            ["family"] = "gemini",
                        }),
                    Conversation =
                    [
                        new AgentConversationMessage(
                            AgentConversationRole.User,
                            [new AgentMessagePart.Text("test")]),
                    ],
                    Tools = [],
                    State = new AgentSessionState
                    {
                        SessionId = "session-test",
                        ProtocolFamily = CopilotDirectModelProviderRuntime.ProtocolFamily,
                        ProviderKey = "github-copilot",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                },
                static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", null);
        }
    }

    [TestMethod]
    public async Task CopilotDirect_Responses_MapsFunctionCallArgumentsAsToolCallNotAssistantText()
    {
        var handler = new StubHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual(new Uri("https://api.individual.githubcopilot.com/responses"), request.RequestUri);
            Assert.AreEqual("Bearer test-token", request.Headers.Authorization?.ToString());
            Assert.IsTrue(request.Headers.TryGetValues("X-Initiator", out var initiator));
            Assert.AreEqual("user", initiator.Single());
            using (var requestDocument = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()))
            {
                Assert.IsTrue(requestDocument.RootElement.TryGetProperty("reasoning", out var reasoning));
                Assert.AreEqual("high", reasoning.GetProperty("effort").GetString());
                Assert.AreEqual("auto", reasoning.GetProperty("summary").GetString());
                Assert.IsTrue(requestDocument.RootElement.TryGetProperty("include", out var include));
                Assert.IsTrue(include.EnumerateArray().Any(static item => item.GetString() == "reasoning.encrypted_content"));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    event: response.created
                    data: {"type":"response.created","response":{"id":"resp_test","object":"response","created_at":1762845696,"status":"in_progress","model":"gpt-test","output":[]}}

                    event: response.output_item.added
                    data: {"type":"response.output_item.added","output_index":0,"item":{"id":"fc_test","type":"function_call","status":"in_progress","call_id":"call_test","name":"list_dir","arguments":""}}

                    event: response.function_call_arguments.delta
                    data: {"type":"response.function_call_arguments.delta","item_id":"fc_test","output_index":0,"delta":"{\"path\":\"C:\\\\code\\\\CodeAlta\"}"}

                    event: response.function_call_arguments.done
                    data: {"type":"response.function_call_arguments.done","item_id":"fc_test","output_index":0,"arguments":"{\"path\":\"C:\\\\code\\\\CodeAlta\"}"}

                    event: response.output_item.done
                    data: {"type":"response.output_item.done","output_index":0,"item":{"id":"fc_test","type":"function_call","status":"completed","call_id":"call_test","name":"list_dir","arguments":"{\"path\":\"C:\\\\code\\\\CodeAlta\"}"}}

                    event: response.completed
                    data: {"type":"response.completed","response":{"id":"resp_test","object":"response","created_at":1762845696,"status":"completed","model":"gpt-test","output":[{"id":"fc_test","type":"function_call","status":"completed","call_id":"call_test","name":"list_dir","arguments":"{\"path\":\"C:\\\\code\\\\CodeAlta\"}"}],"usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}}

                    """,
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        });
        var executor = new CopilotDirectTurnExecutor(new CopilotDirectProviderOptions
        {
            ProviderKey = "github-copilot",
            Auth = new CopilotDirectAuthOptions
            {
                AuthSource = CopilotDirectAuthSources.CopilotTokenEnvironment,
                CopilotTokenEnvironmentVariable = "CODEALTA_TEST_COPILOT_TOKEN",
            },
            HttpClient = new HttpClient(handler),
        });
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = CopilotDirectModelProviderRuntime.ProtocolFamily,
            ProviderKey = "github-copilot",
            DisplayName = "Copilot",
            TransportKind = AgentTransportKind.OpenAIResponses,
            BaseUri = new Uri("https://api.individual.githubcopilot.com"),
        };

        Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", "test-token");
        try
        {
            var response = await executor.ExecuteTurnAsync(
                new AgentTurnRequest
                {
                    Provider = provider,
                    ProviderId = new ModelProviderId(provider.ProviderKey),
                    SessionId = "session-test",
                    RunId = new AgentRunId("run-test"),
                    ModelId = "gpt-test",
                    ReasoningEffort = AgentReasoningEffort.High,
                    ModelInfo = new AgentModelInfo(
                        "gpt-test",
                        Provider: "github-copilot",
                        Capabilities: new Dictionary<string, object?>
                        {
                            ["copilotEndpointKind"] = "Responses",
                        }),
                    Conversation =
                    [
                        new AgentConversationMessage(
                            AgentConversationRole.User,
                            [new AgentMessagePart.Text("test")]),
                    ],
                    Tools = [],
                    State = new AgentSessionState
                    {
                        SessionId = "session-test",
                        ProtocolFamily = CopilotDirectModelProviderRuntime.ProtocolFamily,
                        ProviderKey = "github-copilot",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                },
                static (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

            Assert.AreEqual(1, response.AssistantMessage.Parts.Count);
            var toolCall = Assert.IsInstanceOfType<AgentMessagePart.ToolCall>(response.AssistantMessage.Parts[0]);
            Assert.AreEqual("call_test", toolCall.CallId);
            Assert.AreEqual("list_dir", toolCall.Name);
            Assert.AreEqual(@"C:\code\CodeAlta", toolCall.Arguments.GetProperty("path").GetString());
            Assert.IsFalse(response.AssistantMessage.Parts.OfType<AgentMessagePart.Text>().Any());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", null);
        }
    }

    [TestMethod]
    public async Task CopilotDirect_Responses_RequestsAndStreamsReasoningSummary()
    {
        var handler = new StubHandler(request =>
        {
            using (var requestDocument = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()))
            {
                Assert.IsTrue(requestDocument.RootElement.TryGetProperty("reasoning", out var reasoning));
                Assert.AreEqual("high", reasoning.GetProperty("effort").GetString());
                Assert.AreEqual("auto", reasoning.GetProperty("summary").GetString());
                Assert.IsTrue(requestDocument.RootElement.TryGetProperty("include", out var include));
                Assert.IsTrue(include.EnumerateArray().Any(static item => item.GetString() == "reasoning.encrypted_content"));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    event: response.created
                    data: {"type":"response.created","response":{"id":"resp_reasoning","object":"response","created_at":1762845696,"status":"in_progress","model":"gpt-5.5","output":[]}}

                    event: response.output_item.added
                    data: {"type":"response.output_item.added","output_index":0,"item":{"id":"rs_test","type":"reasoning","status":"in_progress","summary":[]}}

                    event: response.reasoning_summary_text.delta
                    data: {"type":"response.reasoning_summary_text.delta","item_id":"rs_test","output_index":0,"summary_index":0,"delta":"Thinking visible."}

                    event: response.output_item.done
                    data: {"type":"response.output_item.done","output_index":0,"item":{"id":"rs_test","type":"reasoning","status":"completed","summary":[{"type":"summary_text","text":"Thinking visible."}],"encrypted_content":"encrypted-reasoning"}}

                    event: response.output_item.done
                    data: {"type":"response.output_item.done","output_index":1,"item":{"id":"msg_test","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Done.","annotations":[]}]}}

                    event: response.completed
                    data: {"type":"response.completed","response":{"id":"resp_reasoning","object":"response","created_at":1762845696,"status":"completed","model":"gpt-5.5","output":[{"id":"rs_test","type":"reasoning","status":"completed","summary":[{"type":"summary_text","text":"Thinking visible."}],"encrypted_content":"encrypted-reasoning"},{"id":"msg_test","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Done.","annotations":[]}]}],"usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}}

                    """,
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        });
        var executor = new CopilotDirectTurnExecutor(new CopilotDirectProviderOptions
        {
            ProviderKey = "github-copilot",
            Auth = new CopilotDirectAuthOptions
            {
                AuthSource = CopilotDirectAuthSources.CopilotTokenEnvironment,
                CopilotTokenEnvironmentVariable = "CODEALTA_TEST_COPILOT_TOKEN",
            },
            HttpClient = new HttpClient(handler),
        });
        var provider = new ModelProviderRuntimeDescriptor
        {
            ProtocolFamily = CopilotDirectModelProviderRuntime.ProtocolFamily,
            ProviderKey = "github-copilot",
            DisplayName = "Copilot",
            TransportKind = AgentTransportKind.OpenAIResponses,
            BaseUri = new Uri("https://api.individual.githubcopilot.com"),
        };
        var deltas = new List<AgentTurnDelta>();

        Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", "test-token");
        try
        {
            var response = await executor.ExecuteTurnAsync(
                new AgentTurnRequest
                {
                    Provider = provider,
                    ProviderId = new ModelProviderId(provider.ProviderKey),
                    SessionId = "session-test",
                    RunId = new AgentRunId("run-test"),
                    ModelId = "gpt-5.5",
                    ReasoningEffort = AgentReasoningEffort.High,
                    ModelInfo = new AgentModelInfo(
                        "gpt-5.5",
                        Provider: "github-copilot",
                        Capabilities: new Dictionary<string, object?>
                        {
                            ["copilotEndpointKind"] = "Responses",
                        }),
                    Conversation =
                    [
                        new AgentConversationMessage(
                            AgentConversationRole.User,
                            [new AgentMessagePart.Text("test")]),
                    ],
                    Tools = [],
                    State = new AgentSessionState
                    {
                        SessionId = "session-test",
                        ProtocolFamily = CopilotDirectModelProviderRuntime.ProtocolFamily,
                        ProviderKey = "github-copilot",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                },
                (delta, _) =>
                {
                    deltas.Add(delta);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            var reasoningDelta = deltas.Single(static delta => delta.Kind == AgentContentKind.Reasoning);
            Assert.AreEqual("Thinking visible.", reasoningDelta.Text);
            var reasoningPart = response.AssistantMessage.Parts.OfType<AgentMessagePart.Reasoning>().Single();
            Assert.AreEqual("Thinking visible.", reasoningPart.Value);
            Assert.AreEqual("encrypted-reasoning", reasoningPart.ProtectedData);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEALTA_TEST_COPILOT_TOKEN", null);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
