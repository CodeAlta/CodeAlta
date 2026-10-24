using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog.Tests;

// A session whose provider keeps the conversation, read again after a start of CodeAlta: its summary and state come from
// its row in the application database, and its first turn writes the row again, without a read of its journal.
[TestClass]
public sealed class SessionResumeReadsNoJournalTests
{
    [TestMethod]
    public async Task SessionOfAProviderThatKeepsItsConversation_IsResumedAndContinued_WithoutAReadOfItsJournal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var options = new CatalogOptions { GlobalRoot = root };
            await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions
            {
                DatabasePath = options.ApplicationDatabasePath,
                BusyTimeout = TimeSpan.FromSeconds(30),
            });
            var journals = new SessionViewJournalStore(options, database);
            string sessionId;
            await using (var runtime = CreateRuntime(options, journals))
            {
                await using var session = await runtime.CreateSessionAsync(new AgentSessionCreateOptions
                {
                    ProviderKey = "kept",
                    WorkingDirectory = root,
                    OnPermissionRequest = Allow,
                });
                sessionId = session.SessionId;
                await session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("one") });
            }

            // The list of the sessions, as a start of the application reads it: the row is the projection of the journal.
            Assert.HasCount(1, await journals.CreateSessionStore().ListSessionsAsync().ToArrayAsync());
            var journal = Directory.EnumerateFiles(new AgentRuntimePathLayout(options.StateRoot).SessionsRootPath, "*.jsonl", SearchOption.AllDirectories).Single();

            // As at a start of the application: nothing is remembered in memory, and the journal can be appended to, not read.
            var executor = new KeptConversationExecutor();
            await using (var locked = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Write))
            await using (var runtime = CreateRuntime(options, journals, executor))
            {
                await using var resumed = await runtime.ResumeSessionAsync(sessionId, new AgentSessionResumeOptions
                {
                    WorkingDirectory = root,
                    OnPermissionRequest = Allow,
                });
                await resumed.SendAsync(new AgentSendOptions { Input = AgentInput.Text("two") });
            }

            Assert.AreEqual("two", executor.Prompts.Single());
            var (length, summary) = ReadRow(options.ApplicationDatabasePath, sessionId);
            Assert.AreEqual(new FileInfo(journal).Length, length, "The row was written again after the turn, without a read of the journal.");
            StringAssert.Contains(summary, "kept answer");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly AgentPermissionRequestHandler Allow =
        static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));

    private static AgentRuntime CreateRuntime(CatalogOptions options, SessionViewJournalStore journals, KeptConversationExecutor? executor = null)
        => new(
            new ModelProviderId("kept"),
            "Kept",
            new AgentRuntimeOptions
            {
                StateRootPath = options.StateRoot,
                SessionProjectionCache = journals.ProjectionCache,
                Providers =
                [
                    new AgentRuntimeProviderRegistration
                    {
                        Provider = new ModelProviderRuntimeDescriptor
                        {
                            ProtocolFamily = "kept-cli",
                            ProviderKey = "kept",
                            DisplayName = "Kept",
                            TransportKind = AgentTransportKind.ClaudeCodeCli,
                            IsDefault = true,
                        },
                        TurnExecutor = executor ?? new KeptConversationExecutor(),
                    },
                ],
            });

    private static (long Length, string Summary) ReadRow(string databasePath, string sessionId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT journal_length, summary FROM session_projection_cache WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        return (reader.GetInt64(0), reader.GetString(1));
    }

    // A provider whose CLI keeps the conversation: what it holds is counted in its state.
    private sealed class KeptConversationExecutor : IModelProviderTurnExecutor, IAgentProviderKeptConversation
    {
        public List<string> Prompts { get; } = [];

        public Task<AgentCompactionOutcome> CompactAsync(AgentTurnRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new AgentCompactionOutcome(true, "Compacted by the provider."));

        public Task<AgentTurnResponse> ExecuteTurnAsync(
            AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            Prompts.Add(string.Concat(request.Conversation[^1].Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value)));
            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("kept answer")]),
                ProviderSessionId = "cli-conversation",
                ProviderState = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["turns"] = Prompts.Count }),
            });
        }
    }
}
