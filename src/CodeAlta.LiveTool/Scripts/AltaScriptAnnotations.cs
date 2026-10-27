using System.Collections.Frozen;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>
/// The annotations of the <c>alta</c> commands a Code Mode script calls: what the command tree does not say about their
/// values, their stdin and the records they write.
/// </summary>
/// <remarks>
/// Required lists the options and the positional arguments the handler refuses to run without. A command that needs
/// one of two (<c>--message</c> or <c>--stdin</c>, <c>--project</c> or <c>--global</c>) lists neither: its description says it.
/// A <see cref="AltaValueKind.CommaList"/> or a <see cref="AltaValueKind.StringList"/> with <see cref="AltaValue.Values"/>
/// accepts those items only.
/// </remarks>
internal static class AltaScriptAnnotations
{
    private static readonly AltaValue ReasoningEffort = AltaValue.Enum("minimal", "low", "medium", "high", "xhigh", "max", "none");

    // `--scope` of `session result` and `session report`; `turn` and `all` are accepted as aliases.
    private static readonly AltaValue ResultScope = AltaValue.Enum("last-turn", "session");

    /// <summary>Gets the annotations by command path (<c>session create</c>).</summary>
    public static FrozenDictionary<string, AltaCommandAnnotation> Commands { get; } = new Dictionary<string, AltaCommandAnnotation>(StringComparer.Ordinal)
    {
        // Refused with usage.missingCurrentSession when the caller is no session.
        ["session current"] = new()
        {
            Stdin = AltaStdinKind.None,
            Result = "SessionCurrent",
        },

        // Refused without --project or --global (usage.missingScope), and with both.
        ["session create"] = new()
        {
            Values = new Dictionary<string, AltaValue>(StringComparer.Ordinal)
            {
                ["tier"] = AltaValue.Enum("complex", "standard", "quick"),
                ["reasoning"] = ReasoningEffort,
                // One of the modes the provider offers (default, acceptEdits, bypassPermissions; Claude Code also plan,
                // auto, dontAsk): checked against the provider, so left a string.
                ["permission-mode"] = AltaValue.String,
                // A sandbox profile (workspace, strict, or one of the user's) or `off`.
                ["sandbox"] = AltaValue.String,
            },
            Required = [],
            Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["prompt-id"] = "default",
                ["tier"] = "standard, when no model is named or inherited",
                ["base"] = "the commit the project folder is on",
            },
            Stdin = AltaStdinKind.None,
            Result = "SessionCreated",
        },

        // Refused without --message or --stdin (usage.missingMessage), and with both.
        ["session send"] = new()
        {
            Values = new Dictionary<string, AltaValue>(StringComparer.Ordinal)
            {
                ["reasoning"] = ReasoningEffort,
                // One of `alta session choices`, or `provider`.
                ["permission-mode"] = AltaValue.String,
                // A profile of `alta session choices`, `off`, or `scope`.
                ["sandbox"] = AltaValue.String,
            },
            Required = ["session-id"],
            Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["prompt-id"] = "the prompt the session runs",
            },
            Stdin = AltaStdinKind.Text,
            StdinDescription = "The prompt, in the place of `message`: for a prompt of several lines.",
            Result = "SessionSent",
        },

        ["session list"] = new()
        {
            Values = new Dictionary<string, AltaValue>(StringComparer.Ordinal)
            {
                ["state"] = AltaValue.Enum("running", "idle", "inactive", "archived", "all"),
                // Refused when not positive (usage.invalidLimit).
                ["limit"] = AltaValue.Integer,
            },
            Required = [],
            Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["limit"] = "50",
            },
            Stdin = AltaStdinKind.None,
            Result = "(SessionItem | SessionSummary)[]",
        },

        ["session status"] = new()
        {
            Required = ["session-id"],
            Stdin = AltaStdinKind.None,
            Result = "SessionStatus",
        },

        ["session result"] = new()
        {
            Values = new Dictionary<string, AltaValue>(StringComparer.Ordinal)
            {
                ["scope"] = ResultScope,
            },
            Required = ["session-id"],
            Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scope"] = "last-turn",
            },
            Stdin = AltaStdinKind.None,
            Result = "SessionResult",
        },

        // Refused without any session id, from the arguments or stdin (usage.missingSession).
        ["session report"] = new()
        {
            Values = new Dictionary<string, AltaValue>(StringComparer.Ordinal)
            {
                // A repeated positional argument: one argument per id.
                ["session-id"] = AltaValue.StringList,
                ["scope"] = ResultScope,
                ["include"] = new AltaValue(AltaValueKind.CommaList) { Values = ["result", "metrics"] },
            },
            Required = [],
            Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scope"] = "last-turn",
                ["include"] = "result,metrics",
            },
            Stdin = AltaStdinKind.Text,
            StdinDescription = "More session ids, separated by spaces, commas or new lines.",
            Result = "(SessionReportItem | SessionReportNotFound | SessionReportSummary)[]",
        },

        ["session children"] = new()
        {
            Required = ["session-id"],
            Stdin = AltaStdinKind.None,
            Result = "(SessionItem | SessionChildrenSummary)[]",
        },

        // Refused without --command or --stdin (usage.missingCommand), and with both; without a session when no session
        // calls (usage.missingSession).
        ["job start"] = new()
        {
            Values = new Dictionary<string, AltaValue>(StringComparer.Ordinal)
            {
                // Seconds (`1800`) or a time (`00:30:00`), at most 7 days: kept a string for the second form.
                ["timeout"] = AltaValue.String,
                ["notify"] = AltaValue.Enum("always", "success", "never"),
            },
            Required = [],
            Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["notify"] = "always",
                ["cwd"] = "the folder the calling session works in",
                ["session"] = "the calling session",
            },
            Stdin = AltaStdinKind.Text,
            StdinDescription = "The command line, in the place of `command`: for a command of several lines, or with quotes.",
            Result = "JobStarted",
        },

        ["job status"] = new()
        {
            Required = ["job-id"],
            Stdin = AltaStdinKind.None,
            Result = "JobStatus",
        },

        ["job output"] = new()
        {
            Values = new Dictionary<string, AltaValue>(StringComparer.Ordinal)
            {
                // From 1 (usage.invalidLines otherwise).
                ["lines"] = AltaValue.Integer,
            },
            Required = ["job-id"],
            Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lines"] = "as many as one call returns",
            },
            Stdin = AltaStdinKind.None,
            Result = "JobOutput",
        },

        ["project current"] = new()
        {
            Stdin = AltaStdinKind.None,
            Result = "ProjectResolution",
        },

        // Without --detailed: one ProjectRefs record. With --detailed: one ProjectItem per project, then a ProjectSummary.
        ["project list"] = new()
        {
            Stdin = AltaStdinKind.None,
            Result = "ProjectRefs | (ProjectItem | ProjectSummary)[]",
        },

        // The search form only: `show <ref>`, `load <ref>…`, `status` and `reindex` as the query are other forms with other
        // records (alta.search.item, alta.search.load, alta.search.status, alta.search.reindex).
        ["search"] = new()
        {
            Values = new Dictionary<string, AltaValue>(StringComparer.Ordinal)
            {
                // `<query>*`: the words are joined with spaces, so one string holds the whole query.
                ["query"] = AltaValue.String,
                ["kind"] = new AltaValue(AltaValueKind.CommaList)
                {
                    Values = ["message", "session", "note", "plan", "task", "skill", "tool", "mcp", "plugin", "command", "doc"],
                },
                ["role"] = AltaValue.Enum("user", "assistant", "agent"),
                // `keyword` also accepts `keywords` and `words`; `meaning` also `semantic`.
                ["mode"] = AltaValue.Enum("auto", "keyword", "meaning"),
                // From 1 to 50.
                ["limit"] = AltaValue.Integer,
                // `30d`, `12h`, `2w`, `6m`, `1y`, or a date such as `2026-10-01`.
                ["since"] = AltaValue.String,
                ["until"] = AltaValue.String,
                // With `show` of a message only: from 0 to 10.
                ["context"] = AltaValue.Integer,
            },
            Required = ["query"],
            Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = "every kind",
                ["project"] = "every project of the space",
                ["mode"] = "auto",
                ["limit"] = "10",
            },
            Stdin = AltaStdinKind.None,
            Result = "(SearchHit | SearchSummary)[]",
        },

        ["issue list"] = new()
        {
            Values = new Dictionary<string, AltaValue>(StringComparer.Ordinal)
            {
                // Also accepts issues, prs, pull_request, pull, mr, merge_request.
                ["kind"] = AltaValue.Enum("issue", "pr"),
                ["state"] = AltaValue.Enum("open", "closed", "merged", "all"),
                // github, gitlab, azure_devops, bitbucket, jira, or the service of another tracker plugin: left a string.
                ["tracker"] = AltaValue.String,
                // `<id>=<value>`, repeated: `sprint=@current`, `field:System.State=Ready`.
                ["filter"] = AltaValue.StringList,
                // Clamped to 1..100.
                ["limit"] = AltaValue.Integer,
            },
            Required = [],
            Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["project"] = "the project of the calling session, then the cwd",
                ["tracker"] = "the first tracker that has the kind",
                ["kind"] = "issue",
                ["state"] = "open",
                ["limit"] = "30",
            },
            Stdin = AltaStdinKind.None,
            Result = "(Issue | IssueSummary)[]",
        },

        ["issue show"] = new()
        {
            Values = new Dictionary<string, AltaValue>(StringComparer.Ordinal)
            {
                ["kind"] = AltaValue.Enum("issue", "pr"),
                ["tracker"] = AltaValue.String,
            },
            Required = ["id"],
            Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["project"] = "the project of the calling session, then the cwd",
                ["kind"] = "issue",
            },
            Stdin = AltaStdinKind.None,
            Result = "Issue",
        },

        // Diagnostic records come first, then one record per tool, then the summary.
        ["mcp tool search"] = new()
        {
            Stdin = AltaStdinKind.None,
            Result = "(McpToolDiagnostic | McpTool | McpToolSearch)[]",
        },

        // Diagnostic records (alta.mcp.tool.diagnostic) may come before the main record; exit code 1 without it.
        ["mcp tool describe"] = new()
        {
            Required = ["server", "tool"],
            Stdin = AltaStdinKind.None,
            Result = "McpToolDescribe",
        },

        // Diagnostic records may come before the main record. A tool that answers an error writes the record with
        // isError: true and exits with code 2; bad arguments write alta.mcp.error to stdout and exit with code 1.
        ["mcp tool call"] = new()
        {
            Values = new Dictionary<string, AltaValue>(StringComparer.Ordinal)
            {
                // A JSON object; anything else is refused (invalid_arguments).
                ["arguments"] = AltaValue.Json,
            },
            Required = ["server", "tool"],
            Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["arguments"] = "{}",
            },
            Stdin = AltaStdinKind.None,
            Result = "McpToolCall",
        },
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The TypeScript declarations of the records.</summary>
    public const string Types = """
        /** A record of a command: its type, and what its command writes. */
        interface AltaRecord { type: string; [property: string]: unknown; }

        type ReasoningEffort = "minimal" | "low" | "medium" | "high" | "xhigh" | "max" | "none";

        /** Who created a session or submitted a prompt. */
        interface ActorProvenance {
          kind: string;
          automationId?: string;
          sourceSessionId?: string;
          sourceProjectId?: string;
          sourceAgentId?: string;
          pluginRuntimeKey?: string;
          promptProvenance?: string;
          correlationId?: string;
          createdAt: string;
        }

        interface ModelSelection {
          providerKey: string;
          modelId?: string;
          reasoningEffort?: ReasoningEffort;
          modelRef: string;
          promptId: string;
          agentPromptId: string;
        }

        interface SessionSandbox {
          profile?: string;
          source: "session" | "model" | "provider" | "project" | "space" | "all_sessions" | "none";
          problem?: string;
        }

        /** The error that ended a failed or cancelled run. */
        interface FinalError {
          kind: "failed" | "cancelled";
          message: string;
          timestamp: string;
          runId?: string;
          exceptionType?: string;
          failureKind?: string;
          details?: Record<string, string>;
          causes?: string[];
        }

        interface UsageWindow {
          currentTokens?: number;
          tokenLimit?: number;
          messageCount?: number;
          label?: string;
          windowUsagePercentage?: number;
        }

        interface UsageOperation {
          model?: string;
          inputTokens?: number;
          outputTokens?: number;
          cachedInputTokens?: number;
          cacheReadTokens?: number;
          cacheWriteTokens?: number;
          reasoningTokens?: number;
          durationMs?: number;
          cost?: number;
          costUnit?: string;
          initiator?: string;
          parentToolCallId?: string;
          reasoningEffort?: string;
          label?: string;
        }

        interface Usage {
          window?: UsageWindow;
          lastOperation?: UsageOperation;
          scope: "Unknown" | "CurrentWindow" | "LastOperation" | "SessionTotal" | "Compaction" | "Truncation" | "RateLimitOnly";
          /** The name of the event that gave it, such as "CodexTokenCountEvent" or "ProviderUsage". */
          source: string;
          updatedAt: string;
        }

        interface UsageTotals {
          operationCount: number;
          inputTokens: number;
          outputTokens: number;
          cachedInputTokens: number;
          reasoningTokens: number;
          cacheWriteTokens: number;
          cost?: number;
          costUnit?: string;
        }

        interface AnswerSize { finalAnswerCharacters: number; finalAnswerWords: number; estimatedTokens: number; }

        type OutcomeStatus = "completed" | "failed" | "cancelled" | "running" | "unknown";

        interface CompactMetrics {
          scope: "last-turn" | "session";
          status: OutcomeStatus;
          eventCount: number;
          startedAt?: string;
          completedAt?: string;
          durationMs?: number;
          toolCallCount: number;
          finalAnswer: AnswerSize;
          usage?: Usage;
          providerOperations: UsageTotals;
          finalError?: FinalError;
        }

        interface DetailedMetrics {
          scope: "last-turn" | "session";
          status: OutcomeStatus;
          eventCount: number;
          runCount: number;
          startedAt?: string;
          completedAt?: string;
          durationMs?: number;
          finalAssistantAt?: string;
          userMessageCount: number;
          assistantMessageCount: number;
          toolCallCount: number;
          finalAnswer: AnswerSize;
          currentUsage?: Usage;
          providerOperations: UsageTotals;
          finalError?: FinalError;
          modelSelection: ModelSelection;
        }

        interface SessionCurrent extends AltaRecord {
          type: "alta.session.current";
          sessionId: string;
          sourceSessionId: string;
          sourceProjectId?: string;
          sourceAgentId?: string;
          callerKind: string;
          worktreeDirectory?: string;
        }

        /** What alta.session.item and alta.session.status say of a session. */
        interface SessionRecord extends AltaRecord {
          sessionId: string;
          kind: "global_session" | "project_session" | "internal_session";
          providerId: string;
          providerKey: string;
          projectId?: string;
          projectRef?: string;
          spaceId?: string;
          parentSessionId?: string;
          createdBy?: ActorProvenance;
          title: string;
          titleSource: "auto" | "user" | "unset";
          titleMode?: string;
          state: "running" | "idle" | "inactive" | "archived";
          status: "draft" | "active" | "waiting" | "blocked" | "background" | "completed" | "archived";
          archivedAt?: string;
          workingDirectory: string;
          worktreeDirectory?: string;
          latestSummary?: string;
          messageCount?: number;
          isRunning: boolean;
          queuedPromptCount: number;
          agentPromptId: string;
          promptId: string;
          modelSelection: ModelSelection;
          createdAt: string;
          updatedAt: string;
          lastActiveAt: string;
          startedAt?: string;
          sourcePath?: string;
          /** Written by session list and session status; never by session children. */
          sandbox?: SessionSandbox;
        }

        interface SessionItem extends SessionRecord {
          type: "alta.session.item";
          /** With session list --metrics only. */
          metrics?: CompactMetrics;
        }

        interface SessionStatus extends SessionRecord {
          type: "alta.session.status";
          childCount: number;
          childSessionIds: string[];
        }

        interface SessionSummary extends AltaRecord { type: "alta.session.summary"; count: number; truncated: boolean; }

        interface SessionChildrenSummary extends AltaRecord { type: "alta.session.children.summary"; count: number; truncated: boolean; }

        interface SessionCreated extends AltaRecord {
          type: "alta.session.created";
          sessionId: string;
          providerId: string;
          providerKey: string;
          projectId?: string;
          spaceId?: string;
          title: string;
          parentSessionId?: string;
          createdBy: ActorProvenance;
          workingDirectory: string;
          worktreeDirectory?: string;
          worktreeBranch?: string;
          worktreeWarning?: string;
          modelSelection: ModelSelection;
          tier?: "complex" | "standard" | "quick";
          permissionMode?: string;
          sandbox: SessionSandbox;
        }

        /** What an agent caller is told when the target is its child: its end comes as a notification. */
        interface PromptNotification {
          parentSessionId: string;
          automaticParentNotification: true;
          expected: true;
          shouldPoll: false;
          shouldYield: true;
          activeWaitAllowed: false;
          waitForCompletion: false;
          followUpMode: "notification";
          recommendedAction: "stop";
          forbiddenWaitActions: string[];
          nextStep: string;
          guidance: string;
        }

        /**
         * alta.session.submitted for a prompt sent, alta.session.steered when it went into the running turn of a peer,
         * alta.session.queued when --queue-if-busy queued it. The follow-up properties are written only when an agent sends
         * to its child (a session with a parent).
         */
        interface SessionSent extends AltaRecord {
          type: "alta.session.submitted" | "alta.session.steered" | "alta.session.queued";
          sessionId: string;
          runId?: string;
          queueItemId?: string;
          queued: boolean;
          /** True when the prompt was handed over before a run id was known. */
          detached: boolean;
          dispatchKind: "send";
          notificationExpected?: true;
          shouldPoll?: false;
          shouldYield?: true;
          activeWaitAllowed?: false;
          waitForCompletion?: false;
          followUpMode?: "notification";
          recommendedAction?: "stop";
          forbiddenWaitActions?: string[];
          nextStep?: string;
          notification?: PromptNotification;
          submittedBy: ActorProvenance;
          /** The first 160 characters of the prompt. */
          promptPreview: string;
        }

        interface FinalAnswer {
          text: string;
          characters: number;
          words: number;
          estimatedTokens: number;
          completedAt?: string;
        }

        interface SessionResultRecord extends AltaRecord {
          sessionId: string;
          providerId: string;
          providerKey: string;
          status: OutcomeStatus;
          scope: "last-turn" | "session";
          startedAt?: string;
          completedAt?: string;
          durationMs?: number;
          toolCallCount: number;
          modelSelection: ModelSelection;
          /** Absent when the scope has no answer, and in a report without `result`. */
          finalAnswer?: FinalAnswer;
          /** Absent unless the run failed or was cancelled, and in a report without `result`. */
          finalError?: FinalError;
        }

        interface SessionResult extends SessionResultRecord {
          type: "alta.session.result";
          metrics: DetailedMetrics;
        }

        interface SessionReportItem extends SessionResultRecord {
          type: "alta.session.report.item";
          /** Absent in a report without `metrics`. */
          metrics?: DetailedMetrics;
        }

        interface SessionReportNotFound extends AltaRecord {
          type: "alta.session.report.item";
          sessionId: string;
          status: "not_found";
          diagnostic: { code: string; exitCode: number; message: string; };
        }

        interface SessionReportSummary extends AltaRecord {
          type: "alta.session.report.summary";
          count: number;
          successCount: number;
          diagnosticCount: number;
          truncated: boolean;
        }

        type JobState = "running" | "succeeded" | "failed" | "timed_out" | "cancelled";

        interface JobRecord extends AltaRecord {
          id: string;
          state: JobState;
          /** Absent while it runs, and for a job cancelled or out of time. */
          exitCode?: number;
          title?: string;
          command: string;
          folder: string;
          sessionId: string;
          projectId?: string;
          notify: "always" | "success" | "never";
          timeoutSeconds?: number;
          processId?: number;
          startedAt: string;
          endedAt?: string;
          elapsedSeconds: number;
          outputCharacters: number;
          /** What became of the prompt that tells its end; absent while nothing was decided. */
          resultPrompt?: "none" | "steered" | "queued" | "failed";
        }

        interface JobStarted extends JobRecord { type: "alta.job.started"; nextStep: string; }

        interface JobStatus extends JobRecord {
          type: "alta.job.status";
          /** The last line it wrote, as shown last when rewritten in place; absent when it wrote nothing. */
          lastLine?: string;
        }

        interface JobOutput extends AltaRecord {
          type: "alta.job.output";
          id: string;
          state: JobState;
          exitCode?: number;
          title?: string;
          lineCount: number;
          truncated: boolean;
          text: string;
        }

        interface ProjectRecord extends AltaRecord {
          projectId: string;
          slug: string;
          name: string;
          displayName: string;
          projectPath: string;
          defaultBranch?: string;
          archived: boolean;
          sourcePath?: string;
          description?: string;
          tags: string[];
          /** The spaces of the project besides the default one, which holds every project. */
          spaces: string[];
          labels: string[];
        }

        interface ProjectResolution extends ProjectRecord { type: "alta.project.resolution"; }

        interface ProjectItem extends ProjectRecord { type: "alta.project.item"; }

        interface ProjectSummary extends AltaRecord { type: "alta.project.summary"; count: number; truncated: boolean; spaceId: string; }

        /** Written without version and correlationId. */
        interface ProjectRefs {
          type: "alta.project.refs";
          spaceId: string;
          /** [slug, projectPath] of each project. */
          projects: [string, string][];
        }

        type SearchKind = "message" | "session" | "note" | "plan" | "task" | "skill" | "tool" | "mcp" | "plugin" | "command" | "doc" | "script";

        interface SearchHit extends AltaRecord {
          type: "alta.search.hit";
          rank: number;
          kind: SearchKind;
          ref: string;
          title: string;
          /** The matched words in **bold**. */
          snippet: string;
          at: string;
          match: "words" | "meaning" | "both";
          role?: "user" | "assistant" | "agent";
          sessionId?: string;
          runId?: string;
          projectId?: string;
          /** The next properties come from what the source of the hit recorded, when it did. */
          state?: string;
          server?: string;
          tool?: string;
          name?: string;
          page?: string;
          anchor?: string;
          path?: string;
          id?: string;
          shortcut?: string;
          /** The command that uses the hit. */
          next: string;
        }

        interface SearchSummary extends AltaRecord {
          type: "alta.search.summary";
          query: string;
          count: number;
          truncated: boolean;
          mode: "words+meaning" | "words";
          meaning: "ready" | "indexing" | "warming" | "failed" | "not_installed" | "off";
          /** While meaning is indexing only. */
          coverage?: number;
          kinds?: SearchKind[];
          projectId?: string;
          sessionId?: string;
          spaceId?: string;
          /** When nothing matched. */
          hint?: string;
        }

        interface Issue extends AltaRecord {
          type: "alta.issue";
          tracker: string;
          kind: "issue" | "pull_request";
          id: string;
          title: string;
          url: string;
          state: "open" | "draft" | "closed" | "merged";
          /** The state as the tracker names it, when it says more. */
          status?: string;
          issueType?: string;
          priority?: string;
          author?: string;
          assignees?: string[];
          labels?: string[];
          createdAt?: string;
          updatedAt?: string;
          commentCount?: number;
          sourceBranch?: string;
          targetBranch?: string;
          /** issue show only, from here on. */
          description?: string;
          descriptionTruncated?: true;
          comments?: { author?: string; createdAt?: string; body: string; }[];
          moreComments?: true;
          note?: string;
        }

        interface IssueSummary extends AltaRecord {
          type: "alta.issue.summary";
          count: number;
          more?: true;
          tracker: string;
          location: string;
          part?: string;
          kind: "issue" | "pull_request";
          /** Why the listing is empty or incomplete. */
          problem?: string;
          needsSignIn?: true;
          /** The filters applied, by id, with their values. */
          filters?: Record<string, string[]>;
        }

        interface McpToolDiagnostic extends AltaRecord {
          type: "alta.mcp.tool.diagnostic";
          code: string;
          message: string;
          server?: string;
          tool?: string;
          transport?: string;
        }

        interface McpToolRecord extends AltaRecord {
          server: string;
          tool: string;
          /** The name of the tool once loaded in a session (mcp__<server>__<tool>). */
          alias: string;
          title?: string;
          description?: string;
          enabled: boolean;
          disabledReason?: string;
          /** The JSON schema of the arguments. */
          inputSchema: { [key: string]: unknown };
          outputSchema?: { [key: string]: unknown };
        }

        interface McpTool extends McpToolRecord { type: "alta.mcp.tool"; }

        interface McpToolDescribe extends McpToolRecord { type: "alta.mcp.tool.describe"; }

        interface McpToolSearch extends AltaRecord {
          type: "alta.mcp.tool.search";
          server?: string;
          query?: string;
          matchingToolCount: number;
          diagnosticCount: number;
        }

        interface McpContentBlock {
          type: string;
          text?: string;
          mimeType?: string;
          /** What names an image or a resource, whose bytes are not written. */
          summary?: string;
        }

        interface McpToolCall extends AltaRecord {
          type: "alta.mcp.tool.call";
          server: string;
          tool: string;
          alias: string;
          isError: boolean;
          contentText?: string;
          content: McpContentBlock[];
          structuredContent?: unknown;
          truncated: boolean;
        }
        """;
}
