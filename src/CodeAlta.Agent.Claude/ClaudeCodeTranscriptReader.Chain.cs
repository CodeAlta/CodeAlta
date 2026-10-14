using System.Text.Json;

namespace CodeAlta.Agent.Claude;

// The tree of the entries of a transcript and its main chain, by the rules of the SDK of Claude Code.
internal static partial class ClaudeCodeTranscriptReader
{
    /// <summary>An entry of the tree: a user, assistant, system, attachment or progress line.</summary>
    private sealed class Node
    {
        public required string Uuid { get; init; }

        public required string Type { get; init; }

        /// <summary>The parent, after the re-parenting of the messages a compaction kept.</summary>
        public string? Parent { get; set; }

        /// <summary>The parent as written, or the entry it is bridged to when the CLI did not write it.</summary>
        public string? OriginalParent { get; set; }

        /// <summary>The parent as written, also when the CLI did not write that entry.</summary>
        public string? WrittenParent { get; init; }

        /// <summary>Whether the entry is of the conversation itself, not of a subagent.</summary>
        public bool IsMainSide => !IsSidechain && string.IsNullOrEmpty(AgentId);

        /// <summary>For a compaction: the last entry of the conversation it compacted.</summary>
        public string? LogicalParent { get; init; }

        public string? Subtype { get; init; }

        public string? AttachmentType { get; init; }

        public bool IsSidechain { get; init; }

        public bool HasTeam { get; init; }

        public string? AgentId { get; init; }

        /// <summary>What wrote the line (<c>claude-vscode</c>, <c>cli</c>, <c>sdk-cli</c>, ...): two of them may write one transcript.</summary>
        public string? Entrypoint { get; init; }

        public bool IsMeta { get; init; }

        public bool IsCompactSummary { get; init; }

        /// <summary>For an assistant line: the identifier of the model message it is a part of.</summary>
        public string? MessageId { get; init; }

        public string[] ToolUseIds { get; init; } = [];

        public string[] ToolResultIds { get; init; } = [];

        public string? SourceToolAssistantUuid { get; init; }

        public string? Timestamp { get; init; }

        /// <summary>The index of the line among the entries of the file.</summary>
        public int Position { get; init; }

        /// <summary>Where the line is in the buffer read.</summary>
        public int Start { get; init; }

        public int Length { get; init; }

        public (string? Head, string? Anchor, string? Tail)? PreservedSegment { get; init; }

        public (string? Anchor, string[] Uuids)? PreservedMessages { get; init; }

        public bool IsCompactBoundary => Type == "system" && Subtype == "compact_boundary";

        public bool IsMessage => Type is "user" or "assistant";

        /// <summary>A user line that holds tool results.</summary>
        public bool IsToolResultLine => Type == "user" && Parent is not null && ToolResultIds.Length > 0;
    }

    private sealed class ScanResult
    {
        public Dictionary<string, Node> Nodes { get; } = new(StringComparer.Ordinal);

        /// <summary>The entries in the order they first appear in the file.</summary>
        public List<string> Order { get; } = [];

        /// <summary>The absolute offset of the end of the last complete line.</summary>
        public long End { get; set; }

        public bool HasCompaction { get; set; }

        public double? Cost { get; set; }

        public string? CustomTitle { get; set; }

        public string? AiTitle { get; set; }

        public string? LastPrompt { get; set; }

        public string? Summary { get; set; }

        public string? PromptTitle { get; set; }

        // A field: the prompt reader sets it by reference.
        public string? CommandFallback;

        public string? MediaTitle { get; set; }

        public bool FirstNodeSeen { get; set; }

        public string? FirstEntrypoint { get; set; }

        public string? FirstCwd { get; set; }

        public string? FirstPrompt => PromptTitle ?? NonEmpty(CommandFallback) ?? MediaTitle;

        public IEnumerable<Node> Ordered()
        {
            foreach (var uuid in Order)
            {
                yield return Nodes[uuid];
            }
        }
    }

    private static ScanResult Scan(byte[] buffer, long baseOffset, CancellationToken cancellationToken)
    {
        var scan = new ScanResult { End = baseOffset };
        var start = baseOffset == 0 && buffer.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        var position = 0;
        var lines = 0;
        while (start < buffer.Length)
        {
            var newline = buffer.AsSpan(start).IndexOf((byte)'\n');
            if (newline < 0)
            {
                // The last line is still being written: it is read once it ends.
                break;
            }

            if ((++lines & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ScanLine(scan, buffer, start, newline, ref position);
            start += newline + 1;
            scan.End = baseOffset + start;
        }

        return scan;
    }

    private static void ScanLine(ScanResult scan, byte[] buffer, int start, int length, ref int position)
    {
        using var document = TryParse(buffer, start, length);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root)
        {
            return;
        }

        var type = ClaudeCodeJson.GetString(root, "type");
        switch (type)
        {
            case "user" or "assistant" or "system" or "attachment" or "progress":
                if (ClaudeCodeJson.GetString(root, "uuid") is not { Length: > 0 } uuid)
                {
                    return;
                }

                var node = CreateNode(root, type, uuid, start, length, position++);
                if (!scan.Nodes.ContainsKey(uuid))
                {
                    scan.Order.Add(uuid);
                }

                scan.Nodes[uuid] = node;
                if (!scan.FirstNodeSeen)
                {
                    scan.FirstNodeSeen = true;
                    scan.FirstEntrypoint = ClaudeCodeJson.GetString(root, "entrypoint");
                    scan.FirstCwd = ClaudeCodeJson.GetString(root, "cwd");
                }

                scan.HasCompaction |= node.IsCompactBoundary;
                if (type == "user" && scan.PromptTitle is null)
                {
                    scan.PromptTitle = ReadPromptTitle(root, ref scan.CommandFallback);
                    scan.MediaTitle ??= ReadMediaTitle(root);
                }

                break;
            case "cost-state":
                scan.Cost = ClaudeCodeJson.GetDouble(root, "totalCostUSD") ?? scan.Cost;
                break;
            case "custom-title":
                scan.CustomTitle = NonEmpty(ClaudeCodeJson.GetString(root, "customTitle")) ?? scan.CustomTitle;
                break;
            case "ai-title":
                scan.AiTitle = NonEmpty(ClaudeCodeJson.GetString(root, "aiTitle")) ?? scan.AiTitle;
                break;
            case "last-prompt":
                scan.LastPrompt = NonEmpty(ClaudeCodeJson.GetString(root, "lastPrompt")) ?? scan.LastPrompt;
                break;
            case "summary":
                scan.Summary = NonEmpty(ClaudeCodeJson.GetString(root, "summary")) ?? scan.Summary;
                break;
        }
    }

    private static JsonDocument? TryParse(byte[] buffer, int start, int length)
    {
        var line = buffer.AsMemory(start, length);
        var span = line.Span;
        var from = 0;
        var to = span.Length;
        while (from < to && span[from] <= ' ')
        {
            from++;
        }

        while (to > from && span[to - 1] <= ' ')
        {
            to--;
        }

        if (from == to)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(line[from..to], JsonOptions);
        }
        catch (JsonException)
        {
            // A line cut by a crash, or not JSON: what follows does not depend on it.
            return null;
        }
    }

    private static Node CreateNode(JsonElement root, string type, string uuid, int start, int length, int position)
    {
        string? messageId = null;
        string[] toolUseIds = [];
        string[] toolResultIds = [];
        if (ClaudeCodeJson.TryGetObject(root, "message", out var message))
        {
            if (type == "assistant")
            {
                messageId = ClaudeCodeJson.GetString(message, "id");
                toolUseIds = CollectBlockIds(message, "tool_use", "id");
            }
            else if (type == "user")
            {
                toolResultIds = CollectBlockIds(message, "tool_result", "tool_use_id");
            }
        }

        (string?, string?, string?)? segment = null;
        (string?, string[])? preserved = null;
        if (ClaudeCodeJson.TryGetObject(root, "compactMetadata", out var metadata))
        {
            if (ClaudeCodeJson.TryGetObject(metadata, "preservedMessages", out var messages))
            {
                var uuids = ClaudeCodeJson.TryGetArray(messages, "uuids", out var list)
                    ? [.. list.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()!)]
                    : Array.Empty<string>();
                preserved = (ClaudeCodeJson.GetString(messages, "anchorUuid"), uuids);
            }

            if (ClaudeCodeJson.TryGetObject(metadata, "preservedSegment", out var kept))
            {
                segment = (ClaudeCodeJson.GetString(kept, "headUuid"), ClaudeCodeJson.GetString(kept, "anchorUuid"), ClaudeCodeJson.GetString(kept, "tailUuid"));
            }
        }

        var parent = ClaudeCodeJson.GetString(root, "parentUuid");
        return new Node
        {
            Uuid = uuid,
            Type = type,
            Parent = parent,
            OriginalParent = parent,
            WrittenParent = parent,
            LogicalParent = ClaudeCodeJson.GetString(root, "logicalParentUuid"),
            Subtype = ClaudeCodeJson.GetString(root, "subtype"),
            AttachmentType = ClaudeCodeJson.TryGetObject(root, "attachment", out var attachment) ? ClaudeCodeJson.GetString(attachment, "type") : null,
            IsSidechain = ClaudeCodeJson.GetBoolean(root, "isSidechain"),
            HasTeam = ClaudeCodeJson.GetString(root, "teamName") is { Length: > 0 },
            AgentId = ClaudeCodeJson.GetString(root, "agentId"),
            Entrypoint = ClaudeCodeJson.GetString(root, "entrypoint"),
            IsMeta = ClaudeCodeJson.GetBoolean(root, "isMeta"),
            IsCompactSummary = ClaudeCodeJson.GetBoolean(root, "isCompactSummary"),
            MessageId = messageId,
            ToolUseIds = toolUseIds,
            ToolResultIds = toolResultIds,
            SourceToolAssistantUuid = ClaudeCodeJson.GetString(root, "sourceToolAssistantUUID"),
            Timestamp = ClaudeCodeJson.GetString(root, "timestamp"),
            Position = position,
            Start = start,
            Length = length,
            PreservedSegment = segment,
            PreservedMessages = preserved,
        };
    }

    private static string[] CollectBlockIds(JsonElement message, string blockType, string idProperty)
    {
        if (!ClaudeCodeJson.TryGetArray(message, "content", out var content))
        {
            return [];
        }

        List<string>? ids = null;
        foreach (var block in content.EnumerateArray())
        {
            if (string.Equals(ClaudeCodeJson.GetString(block, "type"), blockType, StringComparison.Ordinal) &&
                ClaudeCodeJson.GetString(block, idProperty) is { Length: > 0 } id)
            {
                (ids ??= []).Add(id);
            }
        }

        return ids is null ? [] : [.. ids];
    }

    private static IReadOnlyList<ClaudeCodeTranscriptLastLine> BridgeUnwrittenParents(ScanResult scan)
        => BridgeUnwrittenParents(scan, scan.Nodes.ContainsKey, [], mainSideOnly: false);

    // The CLI does not write some entries (the results of its own tool searches, for one) but writes the next entry
    // below them: that entry is put below the last line the same writer (the same entrypoint) wrote before it on the
    // same side (the conversation, or the same subagent), so that the chain goes on through it instead of ending
    // there. A line of another writer is never taken: when two processes write the transcript, a line is not grafted
    // on the branch of the other one. Nor is a line that already has a child written after it on the same side: the
    // conversation went on from it without this line, which is left a root. (A child written before its parent, as the
    // CLI sometimes writes a tool result before the line of its call, does not count.) For a part of the file, the
    // caller says which parents are written before the part, and gives the last lines the previous read found.
    // Returns the last line of the conversation of each writer, for the next read.
    private static IReadOnlyList<ClaudeCodeTranscriptLastLine> BridgeUnwrittenParents(
        ScanResult scan,
        Func<string, bool> isWritten,
        IReadOnlyList<ClaudeCodeTranscriptLastLine> lastLinesBefore,
        bool mainSideOnly)
    {
        var previous = new Dictionary<(bool Sidechain, string Agent, string Writer), string>();
        var withChildAfter = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lastLinesBefore)
        {
            previous[(false, string.Empty, line.Entrypoint ?? string.Empty)] = line.Uuid;
            if (line.HasChild)
            {
                withChildAfter.Add(line.Uuid);
            }
        }

        foreach (var node in scan.Nodes.Values.OrderBy(static node => node.Position))
        {
            var key = (node.IsSidechain, node.AgentId ?? string.Empty, node.Entrypoint ?? string.Empty);
            if (node.WrittenParent is { } parent && (!mainSideOnly || node.IsMainSide) && !isWritten(parent))
            {
                var bridge = previous.GetValueOrDefault(key) is { } before && !withChildAfter.Contains(before) ? before : null;
                node.Parent = bridge;
                node.OriginalParent = bridge;
            }

            // A child counts when written after its parent, on the same side (a line of a subagent below a line of the
            // conversation does not end it). A parent before the part is a line of the conversation, written before.
            if (node.OriginalParent is { } written &&
                (scan.Nodes.TryGetValue(written, out var parentNode)
                    ? parentNode.Position < node.Position && parentNode.IsSidechain == node.IsSidechain && parentNode.AgentId == node.AgentId
                    : node.IsMainSide))
            {
                withChildAfter.Add(written);
            }

            previous[key] = node.Uuid;
        }

        return [.. previous
            .Where(static pair => !pair.Key.Sidechain && pair.Key.Agent.Length == 0)
            .OrderBy(static pair => pair.Key.Writer, StringComparer.Ordinal)
            .Select(pair => new ClaudeCodeTranscriptLastLine(pair.Key.Writer.Length == 0 ? null : pair.Key.Writer, pair.Value, withChildAfter.Contains(pair.Value)))];
    }

    // A compaction keeps the last messages of the conversation: they are re-parented below the summary, so that the
    // chain from the leaf goes through them.
    private static void ApplyCompactions(ScanResult scan)
    {
        foreach (var boundary in scan.Ordered().Where(static node => node.IsCompactBoundary).ToList())
        {
            // Without an anchor, every root would be taken for a child of it.
            if (boundary.PreservedMessages is { } preserved)
            {
                if (preserved.Anchor is null ||
                    preserved.Uuids.Length == 0 || preserved.Uuids.Any(uuid => !scan.Nodes.ContainsKey(uuid)))
                {
                    continue;
                }

                var previous = preserved.Anchor;
                foreach (var uuid in preserved.Uuids)
                {
                    scan.Nodes[uuid].Parent = previous;
                    previous = uuid;
                }

                var first = preserved.Uuids[0];
                var last = preserved.Uuids[^1];
                foreach (var node in scan.Ordered())
                {
                    if (node.Parent == preserved.Anchor && node.Uuid != first)
                    {
                        node.Parent = last;
                    }
                }
            }
            else if (boundary.PreservedSegment is { Anchor: not null } segment)
            {
                if (segment.Head is { } head && scan.Nodes.TryGetValue(head, out var headNode))
                {
                    headNode.Parent = segment.Anchor;
                }

                foreach (var node in scan.Ordered())
                {
                    if (node.Parent == segment.Anchor && node.Uuid != segment.Head)
                    {
                        node.Parent = segment.Tail;
                    }
                }
            }
        }
    }

    private static bool CanEndTheChain(Node node)
        => !node.IsSidechain && !node.HasTeam && node.Type != "progress" &&
           !(node.Type == "attachment" && node.AttachmentType == "fork_briefing");

    /// <summary>What the search of the leaf found.</summary>
    /// <param name="Leaf">The leaf, when it is an entry read.</param>
    /// <param name="Terminal">The newest entry the leaf was reached from.</param>
    /// <param name="EscapedAt">
    /// For a part of the file: the parent before the part that the walk from the newest entry reached without meeting a
    /// message; the leaf is then above it.
    /// </param>
    /// <param name="HadCandidates">Whether an entry read could end the chain.</param>
    private readonly record struct LeafSearch(Node? Leaf, Node? Terminal, string? EscapedAt, bool HadCandidates);

    private static (Node? Leaf, Node? Terminal) FindLeaf(ScanResult scan)
    {
        var search = FindLeaf(scan, partOfFile: false);
        return (search.Leaf, search.Terminal);
    }

    // The leaf of the conversation: among the entries that can end the chain and have no child that can, the newest
    // in the file, walked up to its first user or assistant entry. Also returns that newest entry (the terminal). In a
    // part of the file, a walk that leaves the part stops there: what is above is not read.
    private static LeafSearch FindLeaf(ScanResult scan, bool partOfFile)
    {
        var withChild = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in scan.Nodes.Values)
        {
            if (node.Parent is { } parent && CanEndTheChain(node))
            {
                withChild.Add(parent);
            }
        }

        var candidates = scan.Nodes.Values
            .Where(node => CanEndTheChain(node) && !withChild.Contains(node.Uuid))
            .OrderByDescending(static node => node.Position)
            .ToList();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var path = new List<string>();
            var local = new HashSet<string>(StringComparer.Ordinal);
            var current = candidate;
            while (current is not null && !visited.Contains(current.Uuid) && !local.Contains(current.Uuid))
            {
                if (current.IsMessage)
                {
                    return new LeafSearch(current, candidate, null, true);
                }

                local.Add(current.Uuid);
                path.Add(current.Uuid);
                if (current.Parent is not { } parent)
                {
                    break;
                }

                current = scan.Nodes.GetValueOrDefault(parent);
                if (current is null && partOfFile)
                {
                    return new LeafSearch(null, candidate, parent, true);
                }
            }

            visited.UnionWith(path);
        }

        if (partOfFile)
        {
            // The whole file would go on with the entries before the part, and with the rule below.
            return new LeafSearch(null, null, null, candidates.Count > 0);
        }

        // No chain ends in a message: the newest message above any entry without a child.
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in scan.Nodes.Values)
        {
            if (node.Parent is { } parent)
            {
                parents.Add(parent);
            }
        }

        var found = new List<Node>();
        foreach (var end in scan.Nodes.Values.Where(node => !parents.Contains(node.Uuid)))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var current = end; current is not null && seen.Add(current.Uuid); current = current.Parent is { } parent ? scan.Nodes.GetValueOrDefault(parent) : null)
            {
                if (current.IsMessage)
                {
                    found.Add(current);
                    break;
                }
            }
        }

        if (found.Count == 0)
        {
            return new LeafSearch(null, null, null, candidates.Count > 0);
        }

        var main = found.Where(static node => !node.IsSidechain && !node.HasTeam && !node.IsMeta).ToList();
        return new LeafSearch((main.Count > 0 ? main : found).MaxBy(static node => node.Position), null, null, candidates.Count > 0);
    }

    private static List<Node> WalkChain(ScanResult scan, Node leaf)
    {
        var chain = new List<Node>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var node = leaf; node is not null && seen.Add(node.Uuid); node = node.Parent is { } parent ? scan.Nodes.GetValueOrDefault(parent) : null)
        {
            chain.Add(node);
        }

        chain.Reverse();
        return chain;
    }

    // A compaction starts a new root: the conversation before it (from its logical parent, as written) is shown before
    // it, once; the messages it kept are already there.
    private static List<Node> WithHistoryBeforeCompactions(ScanResult scan, List<Node> chain)
    {
        var segments = new List<List<Node>> { chain };
        var boundaries = new HashSet<string>(StringComparer.Ordinal);
        while (segments[0] is [{ IsCompactBoundary: true, LogicalParent: { } logical } boundary, ..] &&
               boundaries.Add(boundary.Uuid) &&
               scan.Nodes.TryGetValue(logical, out var before))
        {
            var history = new List<Node>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var node = before; node is not null && seen.Add(node.Uuid); node = node.OriginalParent is { } parent ? scan.Nodes.GetValueOrDefault(parent) : null)
            {
                history.Add(node);
            }

            history.Reverse();
            segments.Insert(0, history);
        }

        if (segments.Count == 1)
        {
            return chain;
        }

        var result = new List<Node>();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in segments.SelectMany(static segment => segment))
        {
            if (shown.Add(node.Uuid))
            {
                result.Add(node);
            }
        }

        return result;
    }

    // Parallel tool calls give sibling branches: each result is a child of the line of its call, and the chain goes
    // through one of them only. The other lines of the model messages of the chain, and the results of their calls,
    // are put back after the last line of their message on the chain.
    private static List<Node> ReattachSiblings(ScanResult scan, List<Node> chain)
    {
        var assistants = chain.Where(static node => node.Type == "assistant").ToList();
        if (assistants.Count == 0)
        {
            return [.. chain];
        }

        var lastOnChain = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var assistant in assistants)
        {
            if (assistant.MessageId is { } id)
            {
                lastOnChain[id] = assistant;
            }
        }

        var linesByMessage = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
        var callOwners = new Dictionary<string, Node?>(StringComparer.Ordinal);
        var resultLines = new List<Node>();
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in scan.Ordered())
        {
            order[node.Uuid] = order.Count;
            if (node.Type == "assistant" && node.MessageId is { } messageId)
            {
                if (!linesByMessage.TryGetValue(messageId, out var lines))
                {
                    linesByMessage[messageId] = lines = [];
                }

                lines.Add(node);
                foreach (var callId in node.ToolUseIds)
                {
                    // A call named by lines of two different messages belongs to neither.
                    callOwners[callId] = !callOwners.TryGetValue(callId, out var owner) || (owner is not null && owner.MessageId == messageId) ? node : null;
                }
            }
            else if (node.IsToolResultLine)
            {
                resultLines.Add(node);
            }
        }

        var attached = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
        var pairs = new HashSet<(string, string)>();
        void Attach(string? to, Node result)
        {
            if (to is null || !pairs.Add((to, result.Uuid)))
            {
                return;
            }

            if (!attached.TryGetValue(to, out var list))
            {
                attached[to] = list = [];
            }

            list.Add(result);
        }

        static bool SameSide(Node left, Node right) => left.IsSidechain == right.IsSidechain && left.AgentId == right.AgentId;

        foreach (var result in resultLines)
        {
            Attach(result.Parent, result);
            if (result.SourceToolAssistantUuid is { } source &&
                source != result.Parent &&
                scan.Nodes.TryGetValue(source, out var sourceNode) &&
                SameSide(result, sourceNode))
            {
                Attach(sourceNode.Uuid, result);
            }

            foreach (var callId in result.ToolResultIds)
            {
                if (callOwners.GetValueOrDefault(callId) is { } owner && SameSide(result, owner))
                {
                    Attach(owner.Uuid, result);
                }
            }
        }

        var answered = new HashSet<string>(chain.SelectMany(static node => node.ToolResultIds), StringComparer.Ordinal);
        var onChain = new HashSet<string>(chain.Select(static node => node.Uuid), StringComparer.Ordinal);
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var inserts = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
        foreach (var assistant in assistants)
        {
            if (assistant.MessageId is not { } messageId || !handled.Add(messageId))
            {
                continue;
            }

            var lines = linesByMessage.GetValueOrDefault(messageId) ?? [assistant];
            var lineIds = new HashSet<string>(lines.Select(static line => line.Uuid), StringComparer.Ordinal);
            var offChain = lines.Where(line => !onChain.Contains(line.Uuid)).ToList();
            var direct = new List<Node>();
            var indirect = new List<Node>();
            var picked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                foreach (var result in attached.GetValueOrDefault(line.Uuid) ?? [])
                {
                    if (onChain.Contains(result.Uuid) || !picked.Add(result.Uuid))
                    {
                        continue;
                    }

                    (result.Parent is { } parent && lineIds.Contains(parent) ? direct : indirect).Add(result);
                }
            }

            if (indirect.Count > 0)
            {
                // A result found through its call or its source, not its parent: kept only when it answers a call of
                // this message that nothing answered yet.
                // What the chain answers, and what this message's results answer: two sets, not a copy of the first.
                var covered = new HashSet<string>(direct.SelectMany(static result => result.ToolResultIds), StringComparer.Ordinal);
                foreach (var result in indirect.OrderBy(result => order.GetValueOrDefault(result.Uuid, int.MaxValue)))
                {
                    if (!result.ToolResultIds.Any(callId => !answered.Contains(callId) && !covered.Contains(callId) && callOwners.GetValueOrDefault(callId) is { } owner && lineIds.Contains(owner.Uuid)))
                    {
                        continue;
                    }

                    covered.UnionWith(result.ToolResultIds);
                    direct.Add(result);
                }
            }

            if (offChain.Count == 0 && direct.Count == 0)
            {
                continue;
            }

            List<Node> inserted = [.. offChain.OrderBy(static line => line.Timestamp ?? string.Empty, StringComparer.Ordinal),
                .. direct.OrderBy(static result => result.Timestamp ?? string.Empty, StringComparer.Ordinal)];
            onChain.UnionWith(inserted.Select(static node => node.Uuid));
            inserts[lastOnChain[messageId].Uuid] = inserted;
        }

        if (inserts.Count == 0)
        {
            return [.. chain];
        }

        var entries = new List<Node>(chain.Count + inserts.Values.Sum(static list => list.Count));
        foreach (var node in chain)
        {
            entries.Add(node);
            if (inserts.TryGetValue(node.Uuid, out var inserted))
            {
                entries.AddRange(inserted);
            }
        }

        return entries;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
