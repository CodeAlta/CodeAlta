using System.Globalization;
using System.Text.Json;
using CodeAlta.ModelServers;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.LiveTool;

// The `alta llama` commands (doc/model-servers.md): the llama.cpp servers of this computer and of the machines it manages,
// their builds, models, downloads and updates, and the models of Hugging Face, for an agent that manages them for the user.
// What downloads code or starts a program (an install, an update, a server that starts, a test) is refused to a session
// without the shell or whose commands are reviewed; a sandboxed session reads only. A download, an install or an update
// tells the session that started it its end with a prompt.
internal sealed partial class BuiltInAltaCommandContributor
{
    private const string LlamaPeopleNote = "Remove a model or a server, or change the update policy, only when the user asks for it.";

    private static readonly AltaCommandPolicy[] LlamaPolicies =
    [
        Read("llama machine list"),
        Read("llama status"),
        Read("llama build list"),
        Read("llama build check"),
        Mutating("llama build install"),
        Mutating("llama build use"),
        Disruptive("llama build remove"),
        Mutating("llama update now"),
        Mutating("llama update at"),
        Mutating("llama update cancel"),
        Mutating("llama update policy"),
        Read("llama hub search"),
        Read("llama hub show"),
        Read("llama model list"),
        Mutating("llama model download"),
        Disruptive("llama model remove"),
        Mutating("llama model rescan"),
        Read("llama download list"),
        Mutating("llama download pause"),
        Mutating("llama download resume"),
        Mutating("llama download cancel"),
        Read("llama server list"),
        Read("llama server show"),
        Read("llama server log"),
        Read("llama server import"),
        Mutating("llama server add"),
        Mutating("llama server set"),
        Mutating("llama server start"),
        Mutating("llama server stop"),
        Mutating("llama server restart"),
        Mutating("llama server load"),
        Mutating("llama server unload"),
        Mutating("llama server test"),
        Mutating("llama server use"),
        Disruptive("llama server remove"),
    ];

    private static readonly JsonSerializerOptions LlamaJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private static Command CreateLlamaCommand(AltaCommandContext context)
    {
        var group = Group("llama", "Manage the llama.cpp servers of this computer and of the machines it manages: builds, models, downloads, servers, updates, and the models of Hugging Face.");
        group.Add(CreateLlamaMachineCommand(context));
        group.Add(CreateLlamaStatusCommand(context));
        group.Add(CreateLlamaBuildCommand(context));
        group.Add(CreateLlamaUpdateCommand(context));
        group.Add(CreateLlamaHubCommand(context));
        group.Add(CreateLlamaModelCommand(context));
        group.Add(CreateLlamaDownloadCommand(context));
        group.Add(CreateLlamaServerCommand(context));
        AddHelpText(group,
            "A machine is `local` (this computer) or a paired machine, by its name or id; `--machine` defaults to this computer, else to the one machine managed.",
            "A model is `publisher/repository:QUANT`, as on Hugging Face (`unsloth/Qwen3-8B-GGUF:Q4_K_M`); models live in the Hugging Face cache of each machine.",
            "A server runs a build of llama.cpp: `several` models loaded on demand (router mode), or `one` model always loaded. Its models are downloaded first when missing.",
            "`llama server use <id>` adds a provider so that the sessions use the server. Downloads, installs and updates tell this session their end with a prompt: do not poll them.",
            LlamaPeopleNote,
            "Start with `alta llama status`, then `alta llama hub show <repo>` to pick a quantization that fits, `alta llama server add --model <repo:QUANT> --name <name> --use`.");
        return group;
    }

    // --- machine, status ---

    private static Command CreateLlamaMachineCommand(AltaCommandContext context)
    {
        var group = Group("machine", "List the machines whose model servers this computer reads.");
        var list = Leaf("list", "List the machines: this computer and the paired machines, whether each is online and whether this computer manages it.");
        list.Add(async (_, _) => await WithLlamaAsync(context, async servers =>
        {
            foreach (var machine in await servers.ListMachinesAsync(context.CancellationToken).ConfigureAwait(false))
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new
                {
                    type = "alta.llama.machine", version = 1, correlationId = context.CorrelationId,
                    id = machine.Id, name = machine.Name, local = machine.Local, state = machine.State, manages = machine.Manages, uses = machine.Uses, problem = machine.Problem,
                });
            }

            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        group.Add(list);
        return group;
    }

    private static Command CreateLlamaStatusCommand(AltaCommandContext context)
    {
        string? machine = null;
        var command = Leaf("status", "Say what a machine runs: its GPUs, its builds of llama.cpp and the latest one, its update policy, servers, models, downloads and tasks.");
        command.Add("machine=", "The machine. Defaults to this computer.", value => machine = value);
        command.Add(async (_, _) => await WithLlamaMachineAsync(context, machine, manage: false, async (servers, target) =>
        {
            var status = await servers.StatusAsync(target.Id, context.CancellationToken).ConfigureAwait(false);
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.llama.status", version = 1, correlationId = context.CorrelationId,
                machine = target.Id, name = target.Name, manages = target.Manages,
                hardware = new
                {
                    status.Hardware.Os, status.Hardware.Architecture, status.Hardware.CpuName, status.Hardware.MemoryBytes, status.Hardware.Gpus, status.Hardware.Libraries,
                },
                builds = status.Builds.Where(static build => build.Current || build.Previous).Select(LlamaBuildRecord),
                latest = LlamaLatestRecord(status.Latest),
                updateAvailable = LlamaUpdateAvailable(status),
                recommended = status.Candidates.Where(static candidate => candidate.Verdict == "fits").OrderByDescending(static candidate => candidate.Score).Select(static candidate => candidate.Backend).FirstOrDefault(),
                policy = status.Settings.Updates,
                planned = status.Planned,
                modelsFolder = status.ModelsFolder,
                modelsBytes = status.ModelsBytes,
                freeBytes = status.FreeBytes,
                servers = status.Servers.Select(static server => new { server.Definition.Id, server.Definition.Name, server.Status.State, server.Status.Address, models = server.Definition.Models.Select(static model => model.Model) }),
                models = status.Models.Select(static model => new { model.Id, model.SizeBytes, model.State }),
                downloads = status.Downloads.Where(static download => download.State is "queued" or "downloading" or "checking" or "paused" or "failed"),
                tasks = status.Tasks,
                unmanaged = status.Unmanaged,
                warnings = status.Warnings,
                lastUpdate = status.History.FirstOrDefault(),
            });
            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        AddHelpText(command, "`recommended` is the backend that fits the GPUs best; `alta llama build list` says why each backend fits or not.");
        return command;
    }

    // --- builds ---

    private static Command CreateLlamaBuildCommand(AltaCommandContext context)
    {
        var group = Group("build", "List, install, switch or remove the builds of llama.cpp of a machine.");

        string? listMachine = null;
        var list = Leaf("list", "List the builds installed on a machine, and each backend with whether it fits its GPUs, drivers and system.");
        list.Add("machine=", "The machine. Defaults to this computer.", value => listMachine = value);
        list.Add(async (_, _) => await WithLlamaMachineAsync(context, listMachine, manage: false, async (servers, target) =>
        {
            var status = await servers.StatusAsync(target.Id, context.CancellationToken).ConfigureAwait(false);
            foreach (var build in status.Builds) AltaJsonlWriter.WriteRecord(context.Stdout, new { type = "alta.llama.build", version = 1, correlationId = context.CorrelationId, machine = target.Id, build = LlamaBuildRecord(build) });
            foreach (var candidate in status.Candidates)
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new
                {
                    type = "alta.llama.candidate", version = 1, correlationId = context.CorrelationId, machine = target.Id,
                    candidate.Backend, candidate.Verdict, candidate.Message, candidate.Score, candidate.Asset,
                });
            }

            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        group.Add(list);

        string? checkMachine = null;
        var check = Leaf("check", "Read the latest build of the update channel of a machine from GitHub now.");
        check.Add("machine=", "The machine. Defaults to this computer.", value => checkMachine = value);
        check.Add(async (_, _) => await RunLlamaActionAsync(context, checkMachine, new ModelHostAction { Action = "checkLatest" }, startsPrograms: false, result =>
            new { type = "alta.llama.latest", latest = LlamaLatestRecord(result.Latest) }).ConfigureAwait(false));
        group.Add(check);

        string? installMachine = null, installBuild = null;
        var backends = new List<string>();
        var install = Leaf("install", "Install the best build of llama.cpp for a machine (or the backends named), checked against its digest and tested on its GPUs.");
        install.Add("machine=", "The machine. Defaults to this computer.", value => installMachine = value);
        install.Add("backend=", "A backend: cuda, rocm, vulkan, metal, cpu. Repeat for several; none for the recommended one.", value => { if (NormalizeOptionalText(value) is { } text) backends.Add(text.ToLowerInvariant()); });
        install.Add("build=", "The build (`b12110`). Defaults to the latest of the channel.", value => installBuild = value);
        install.Add(async (_, _) => await RunLlamaActionAsync(context, installMachine, new ModelHostAction { Action = "installBuilds", Backends = [.. backends], Build = NormalizeOptionalText(installBuild) },
            startsPrograms: true, static result => new { type = "alta.llama.task", task = result.Task, note = "The install runs on the machine: its end is sent to this session as a prompt." }).ConfigureAwait(false));
        group.Add(install);

        string? useMachine = null, useBackend = null, useBuild = null;
        var use = Leaf("use", "Make the servers of a backend run an installed build: an older one to go back, or a newer one; they restart and are tested.");
        use.Add("machine=", "The machine. Defaults to this computer.", value => useMachine = value);
        use.Add("backend=", "The backend.", value => useBackend = value);
        use.Add("build=", "The installed build (`b12110`).", value => useBuild = value);
        use.Add(async (_, _) =>
        {
            if (NormalizeOptionalText(useBackend) is not { } backend || NormalizeOptionalText(useBuild) is not { } build)
                return UsageError(context, "usage.missingBuild", "Name the backend and the build: --backend <backend> --build <bNNNN>.", "alta llama build use");
            return await RunLlamaActionAsync(context, useMachine, new ModelHostAction { Action = "useBuild", Backend = backend, Build = build }, startsPrograms: true,
                static result => new { type = "alta.llama.build", build = result.Build is { } value ? LlamaBuildRecord(value) : null }).ConfigureAwait(false);
        });
        group.Add(use);

        string? removeMachine = null, removeBackend = null, removeBuild = null;
        var remove = Leaf("remove", "Remove an installed build that no server runs.");
        remove.Add("machine=", "The machine. Defaults to this computer.", value => removeMachine = value);
        remove.Add("backend=", "The backend.", value => removeBackend = value);
        remove.Add("build=", "The build.", value => removeBuild = value);
        remove.Add(async (_, _) =>
        {
            if (NormalizeOptionalText(removeBackend) is not { } backend || NormalizeOptionalText(removeBuild) is not { } build)
                return UsageError(context, "usage.missingBuild", "Name the backend and the build: --backend <backend> --build <bNNNN>.", "alta llama build remove");
            return await RunLlamaActionAsync(context, removeMachine, new ModelHostAction { Action = "removeBuild", Backend = backend, Build = build }, startsPrograms: false,
                static result => new { type = "alta.llama.removed", freedBytes = result.Freed }).ConfigureAwait(false);
        });
        group.Add(remove);
        AddHelpText(group, "A build is checked against the digest GitHub gives, then tested: `--version` and the GPUs it sees; a build that sees no GPU of its backend is refused.");
        return group;
    }

    // --- updates ---

    private static Command CreateLlamaUpdateCommand(AltaCommandContext context)
    {
        var group = Group("update", "Update llama.cpp on a machine now or at a time, and set its update policy.");

        string? nowMachine = null, nowBuild = null;
        var nowModels = false;
        var nowAll = false;
        var now = Leaf("now", "Update the builds of a machine now: it waits for its servers to be idle, tests each with the new build, and goes back when a test fails.");
        now.Add("machine=", "The machine. Defaults to this computer.", value => nowMachine = value);
        now.Add("all", "Every machine this computer manages and reaches now, each at once. To update one after the other, update the next once the end of the previous comes.",
            value => nowAll = value is not null);
        now.Add("build=", "The build, the same for every machine (`b12110`; an older one takes them back to it). Defaults to the latest of each machine's channel.", value => nowBuild = value);
        now.Add("models", "Update the models too, when their repository changed.", value => nowModels = value is not null);
        now.Add(async (_, _) =>
        {
            var action = new ModelHostAction { Action = "updateNow", Build = NormalizeOptionalText(nowBuild), Models = nowModels };
            static object Record(ModelHostActionResult result) => new { type = "alta.llama.task", task = result.Task, note = "The update runs on the machine: its end is sent to this session as a prompt." };
            if (!nowAll) return await RunLlamaActionAsync(context, nowMachine, action, startsPrograms: true, Record).ConfigureAwait(false);
            if (NormalizeOptionalText(nowMachine) is not null) return UsageError(context, "usage.allAndMachine", "Give --all or --machine, not both.", "alta llama update now");
            return await RunLlamaActionOnAllAsync(context, action, Record).ConfigureAwait(false);
        });
        group.Add(now);

        string? atMachine = null, atBuild = null, atTime = null;
        var atModels = false;
        var atAll = false;
        var at = Leaf("at", "Plan one update of a machine at a time: the machine keeps it and runs it then, even while this computer is off.");
        at.Add("<time>", "When: an ISO date and time (`2026-11-09T03:00`, in the time zone of this computer unless it names one).", value => atTime = value);
        at.Add("machine=", "The machine. Defaults to this computer.", value => atMachine = value);
        at.Add("all", "Every machine this computer manages and reaches now: each keeps the time and updates then.", value => atAll = value is not null);
        at.Add("build=", "The build, the same for every machine. Defaults to the latest of each machine's channel then.", value => atBuild = value);
        at.Add("models", "Update the models too.", value => atModels = value is not null);
        at.Add(async (_, _) =>
        {
            if (!DateTimeOffset.TryParse(atTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when))
                return UsageError(context, "usage.invalidTime", "Give the time as an ISO date and time, such as 2026-11-09T03:00.", "alta llama update at");
            if (when <= DateTimeOffset.Now) return UsageError(context, "usage.pastTime", "The time is past: give a time to come.", "alta llama update at");
            var action = new ModelHostAction { Action = "planUpdate", At = when, Build = NormalizeOptionalText(atBuild), Models = atModels };
            static object Record(ModelHostActionResult result) => new { type = "alta.llama.planned", planned = result.Planned };
            if (!atAll) return await RunLlamaActionAsync(context, atMachine, action, startsPrograms: true, Record).ConfigureAwait(false);
            if (NormalizeOptionalText(atMachine) is not null) return UsageError(context, "usage.allAndMachine", "Give --all or --machine, not both.", "alta llama update at");
            return await RunLlamaActionOnAllAsync(context, action, Record).ConfigureAwait(false);
        });
        group.Add(at);

        string? cancelMachine = null;
        var cancel = Leaf("cancel", "Cancel the update planned on a machine.");
        cancel.Add("machine=", "The machine. Defaults to this computer.", value => cancelMachine = value);
        cancel.Add(async (_, _) => await RunLlamaActionAsync(context, cancelMachine, new ModelHostAction { Action = "cancelPlanned" }, startsPrograms: false,
            static _ => new { type = "alta.llama.planned", planned = (PlannedUpdate?)null }).ConfigureAwait(false));
        group.Add(cancel);

        string? policyMachine = null, channel = null, pinned = null, schedule = null, time = null, day = null, models = null;
        var policy = Leaf("policy", "Set the update policy of a machine: which builds, and when (when the user clicks Update, every day, or every week). Only when the user asks.");
        policy.Add("machine=", "The machine. Defaults to this computer.", value => policyMachine = value);
        policy.Add("channel=", "`stable` (the release llama.cpp marks as latest), `every` (every build) or `pinned`.", value => channel = value);
        policy.Add("pinned=", "The build a pinned channel stays on (`b12110`).", value => pinned = value);
        policy.Add("schedule=", "`manual` (when the user clicks Update), `daily` or `weekly`.", value => schedule = value);
        policy.Add("time=", "The time of day of an automatic update, `HH:mm` in the time zone of the machine.", value => time = value);
        policy.Add("day=", "The day of a weekly update: `monday`...`sunday`.", value => day = value);
        policy.Add("models=", "`on` to update the models too, `off` not to.", value => models = value);
        policy.Add(async (_, _) => await WithLlamaMachineAsync(context, policyMachine, manage: false, async (servers, target) =>
        {
            var status = await servers.StatusAsync(target.Id, context.CancellationToken).ConfigureAwait(false);
            var current = status.Settings.Updates;
            if (new[] { channel, pinned, schedule, time, day, models }.All(static value => NormalizeOptionalText(value) is null))
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new { type = "alta.llama.policy", version = 1, correlationId = context.CorrelationId, machine = target.Id, policy = current, planned = status.Planned });
                return AltaExitCodes.Success;
            }

            if (!target.Manages) return PermissionDenied(context, "llama.notManaged", $"This computer does not manage {target.Name}.");
            var next = current with
            {
                Channel = NormalizeOptionalText(channel)?.ToLowerInvariant() ?? current.Channel,
                PinnedBuild = NormalizeOptionalText(pinned) ?? current.PinnedBuild,
                Schedule = NormalizeOptionalText(schedule)?.ToLowerInvariant() ?? current.Schedule,
                Time = NormalizeOptionalText(time) ?? current.Time,
                Day = NormalizeOptionalText(day)?.ToLowerInvariant() ?? current.Day,
                UpdateModels = NormalizeOptionalText(models)?.ToLowerInvariant() switch { "on" or "true" or "yes" => true, "off" or "false" or "no" => false, _ => current.UpdateModels },
            };
            if (next.Channel is not ("stable" or "every" or "pinned")) return UsageError(context, "usage.invalidChannel", "--channel is stable, every or pinned.", "alta llama update policy");
            if (next.Channel == "pinned" && next.PinnedBuild is not ['b', _, ..])
                return UsageError(context, "usage.missingPinned", "A pinned channel needs --pinned <bNNNN>.", "alta llama update policy");
            if (next.Schedule is not ("manual" or "daily" or "weekly")) return UsageError(context, "usage.invalidSchedule", "--schedule is manual, daily or weekly.", "alta llama update policy");
            if (!TimeOnly.TryParseExact(next.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return UsageError(context, "usage.invalidTime", "--time is HH:mm, such as 04:00.", "alta llama update policy");
            if (!Enum.TryParse<DayOfWeek>(next.Day, ignoreCase: true, out _)) return UsageError(context, "usage.invalidDay", "--day is a day of the week, such as sunday.", "alta llama update policy");
            if (StartsLlamaPrograms(next) && await LlamaStartRefusalAsync(context, "plan automatic updates of llama.cpp").ConfigureAwait(false) is { } refused) return refused;
            await servers.RunAsync(target.Id, new ModelHostAction { Action = "setSettings", Settings = status.Settings with { Updates = next } }, SessionOf(context), context.CancellationToken).ConfigureAwait(false);
            AltaJsonlWriter.WriteRecord(context.Stdout, new { type = "alta.llama.policy", version = 1, correlationId = context.CorrelationId, machine = target.Id, policy = next });
            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        AddHelpText(policy, "Without an option, it says the policy. " + LlamaPeopleNote);
        group.Add(policy);
        return group;
    }

    private static bool StartsLlamaPrograms(UpdatePolicy policy) => policy.Schedule != "manual";

    // --- Hugging Face ---

    private static Command CreateLlamaHubCommand(AltaCommandContext context)
    {
        var group = Group("hub", "Search the GGUF models of Hugging Face and read one, with the quantizations that fit the machines.");

        var words = new List<string>();
        string? author = null, kind = null, sort = null;
        var limit = 20;
        var search = Leaf("search", "Search the GGUF models of Hugging Face, the trending ones first.");
        search.Add("<words>*", "Words of their names.", value => { if (NormalizeOptionalText(value) is { } word) words.Add(word); });
        search.Add("author=", "Their publisher (`unsloth`, `bartowski`, `ggml-org`...).", value => author = value);
        search.Add("kind=", "`text-generation`, `image-text-to-text`, `feature-extraction` (embeddings) or `text-ranking` (rerank).", value => kind = value);
        search.Add("sort=", "`trending` (default), `downloads`, `likes` or `recent`.", value => sort = value);
        search.Add("limit=", "How many, at most (20 by default, 100 at most).", value => limit = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 1, 100) : 20);
        search.Add(async (_, _) => await WithLlamaAsync(context, async servers =>
        {
            var order = NormalizeOptionalText(sort)?.ToLowerInvariant() switch { "downloads" => "downloads", "likes" => "likes", "recent" => "lastModified", _ => "trendingScore" };
            var found = await servers.SearchHubAsync(words.Count > 0 ? string.Join(' ', words) : null, NormalizeOptionalText(author), NormalizeOptionalText(kind), order, limit, context.CancellationToken)
                .ConfigureAwait(false);
            foreach (var model in found)
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new
                {
                    type = "alta.llama.hub.model", version = 1, correlationId = context.CorrelationId,
                    model.Id, model.Author, model.Downloads, model.Likes, model.LastModified, kind = model.PipelineTag, model.Gated,
                });
            }

            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        group.Add(search);

        string? repo = null;
        var machines = new List<string>();
        var show = Leaf("show", "Read a model of Hugging Face: its facts and its quantizations, with whether each fits the GPUs of the machines and is on their disk.");
        show.Add("<repo>", "The repository: `publisher/name`, or its address.", value => repo = value);
        show.Add("machine=", "A machine to tell the fit for. Repeat for several; defaults to the machines this computer manages.", value => { if (NormalizeOptionalText(value) is { } text) machines.Add(text); });
        show.Add(async (_, _) => await WithLlamaAsync(context, async servers =>
        {
            if (LlamaRepo(repo) is not { } name) return UsageError(context, "usage.invalidRepo", "Name the repository as publisher/name.", "alta llama hub show");
            var known = await servers.ListMachinesAsync(context.CancellationToken).ConfigureAwait(false);
            var ids = new List<string>();
            foreach (var given in machines)
            {
                if (FindLlamaMachine(known, given) is not { } found) return NotFound(context, "llama.machineNotFound", $"No machine is named '{given}'. Machines: {string.Join(", ", known.Select(static item => item.Name))}.");
                ids.Add(found.Id);
            }

            if (ids.Count == 0) ids.AddRange(known.Where(static item => item.Manages && (item.Local || item.State == "online")).Select(static item => item.Id));
            var model = await servers.ReadHubModelAsync(name, ids, context.CancellationToken).ConfigureAwait(false);
            var details = model.Details;
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.llama.hub.model", version = 1, correlationId = context.CorrelationId,
                details.Model.Id, details.Model.Author, details.Model.Downloads, details.Model.Likes, details.Model.LastModified, kind = details.Model.PipelineTag, details.Model.Gated,
                details.Revision, details.License, details.BaseModels, details.Parameters, details.Architecture, details.ContextLength,
                // From the header of its GGUF: the KV cache of each token, which the fits below count.
                shape = model.Shape is { } shape ? new { shape.Architecture, shape.Layers, shape.TrainedContext, kvBytesPer1kTokens = shape.KvBytes(1024), shape.SlidingWindow, shape.Known } : null,
            });
            foreach (var quant in model.Quantizations)
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new
                {
                    type = "alta.llama.hub.quantization", version = 1, correlationId = context.CorrelationId,
                    model = $"{details.Model.Id}:{quant.Tag}", quant.Tag, quant.Bits, sizeBytes = quant.ModelBytes + quant.MmprojBytes, visionBytes = quant.MmprojBytes, quant.Files,
                    fits = quant.Fits.Select(static fit => new { fit.Machine, fit = fit.Fit.Fit, fit.Fit.Message, fit.Fit.Approximate, context = fit.Fit.Context, onDisk = fit.OnDisk }),
                });
            }

            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        AddHelpText(show, "`fit` is `gpu` (on one GPU), `gpus` (split across them), `partly` (part in the memory of the system: slow) or `too_large`; `context` is the largest context that fits, from the header of the model. Prefer the largest that fits with the context the work needs.");
        group.Add(show);
        return group;
    }

    // --- models and downloads ---

    private static Command CreateLlamaModelCommand(AltaCommandContext context)
    {
        var group = Group("model", "List, download or remove the models on the disks of the machines.");

        string? listMachine = null;
        var list = Leaf("list", "List the models on the disk of a machine, the servers that use each, and what removing it frees.");
        list.Add("machine=", "The machine. Defaults to this computer.", value => listMachine = value);
        list.Add(async (_, _) => await WithLlamaMachineAsync(context, listMachine, manage: false, async (servers, target) =>
        {
            var status = await servers.StatusAsync(target.Id, context.CancellationToken).ConfigureAwait(false);
            foreach (var model in status.Models)
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new
                {
                    type = "alta.llama.model", version = 1, correlationId = context.CorrelationId, machine = target.Id,
                    model.Id, model.Revision, model.SizeBytes, model.FreedBytes, model.Vision, model.State, model.ModelPath,
                    usedBy = status.Servers.Where(server => server.Definition.Models.Any(item => string.Equals(item.Model, model.Id, StringComparison.OrdinalIgnoreCase))).Select(static server => server.Definition.Id),
                });
            }

            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        group.Add(list);

        string? downloadMachine = null, downloadModel = null;
        var download = Leaf("download", "Download a model to a machine, from Hugging Face into its cache: checked, and resumed after a break.");
        download.Add("<model>", "The model: `publisher/repository:QUANT`.", value => downloadModel = value);
        download.Add("machine=", "The machine. Defaults to this computer.", value => downloadMachine = value);
        download.Add(async (_, _) =>
        {
            if (LlamaModel(downloadModel) is not { } model) return UsageError(context, "usage.invalidModel", "Name the model as publisher/repository:QUANT.", "alta llama model download");
            return await RunLlamaActionAsync(context, downloadMachine, new ModelHostAction { Action = "download", Model = model }, startsPrograms: false,
                static result => new { type = "alta.llama.download", download = result.Download, note = "The download runs on the machine: its end is sent to this session as a prompt." }).ConfigureAwait(false);
        });
        group.Add(download);

        string? removeMachine = null, removeModel = null;
        var stop = false;
        var remove = Leaf("remove", "Remove a model from the disk of a machine: the files it shares with another model stay. Only when the user asks.");
        remove.Add("<model>", "The model.", value => removeModel = value);
        remove.Add("machine=", "The machine. Defaults to this computer.", value => removeMachine = value);
        remove.Add("stop", "Stop the servers that serve it first; without it, a model in use is not removed.", value => stop = value is not null);
        remove.Add(async (_, _) =>
        {
            if (NormalizeOptionalText(removeModel) is not { } model) return UsageError(context, "usage.missingModel", "Name the model.", "alta llama model remove");
            return await RunLlamaActionAsync(context, removeMachine, new ModelHostAction { Action = "removeModel", Model = model, Stop = stop }, startsPrograms: false,
                static result => new { type = "alta.llama.removed", freedBytes = result.Freed }).ConfigureAwait(false);
        });
        group.Add(remove);

        string? rescanMachine = null;
        var rescan = Leaf("rescan", "Read the models folder of a machine again, after files were copied there by hand.");
        rescan.Add("machine=", "The machine. Defaults to this computer.", value => rescanMachine = value);
        rescan.Add(async (_, _) => await RunLlamaActionAsync(context, rescanMachine, new ModelHostAction { Action = "rescan" }, startsPrograms: false,
            static _ => new { type = "alta.llama.rescanned" }).ConfigureAwait(false));
        group.Add(rescan);
        return group;
    }

    private static Command CreateLlamaDownloadCommand(AltaCommandContext context)
    {
        var group = Group("download", "Follow, pause, resume or cancel the downloads of models of a machine.");
        string? listMachine = null;
        var list = Leaf("list", "List the downloads of a machine: their state, progress, speed and error.");
        list.Add("machine=", "The machine. Defaults to this computer.", value => listMachine = value);
        list.Add(async (_, _) => await WithLlamaMachineAsync(context, listMachine, manage: false, async (servers, target) =>
        {
            var status = await servers.StatusAsync(target.Id, context.CancellationToken).ConfigureAwait(false);
            foreach (var item in status.Downloads) AltaJsonlWriter.WriteRecord(context.Stdout, new { type = "alta.llama.download", version = 1, correlationId = context.CorrelationId, machine = target.Id, download = item });
            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        group.Add(list);
        foreach (var verb in new[] { "pause", "resume", "cancel" })
        {
            string? machine = null, id = null;
            var leaf = Leaf(verb, verb switch { "pause" => "Pause a download: it keeps what it has.", "resume" => "Resume a paused or failed download.", _ => "Cancel a download and delete what it has." });
            leaf.Add("<id>", "The download.", value => id = value);
            leaf.Add("machine=", "The machine. Defaults to this computer.", value => machine = value);
            leaf.Add(async (_, _) =>
            {
                if (NormalizeOptionalText(id) is not { } download) return UsageError(context, "usage.missingDownload", "Name the download (its id, from `alta llama download list`).", $"alta llama download {verb}");
                return await RunLlamaActionAsync(context, machine, new ModelHostAction { Action = "controlDownload", Id = download, Verb = verb }, startsPrograms: false,
                    static result => new { type = "alta.llama.download", download = result.Download }).ConfigureAwait(false);
            });
            group.Add(leaf);
        }

        return group;
    }

    // --- servers ---

    private static Command CreateLlamaServerCommand(AltaCommandContext context)
    {
        var group = Group("server", "Add, change, start, stop, test or remove the llama.cpp servers of a machine, and use them in the sessions.");

        string? listMachine = null;
        var list = Leaf("list", "List the servers of a machine with their state, address, build and models.");
        list.Add("machine=", "The machine. Defaults to this computer.", value => listMachine = value);
        list.Add(async (_, _) => await WithLlamaMachineAsync(context, listMachine, manage: false, async (servers, target) =>
        {
            var status = await servers.StatusAsync(target.Id, context.CancellationToken).ConfigureAwait(false);
            foreach (var server in status.Servers) WriteLlamaServer(context, target.Id, server, withDefinition: false);
            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        group.Add(list);

        string? showMachine = null, showId = null;
        var show = Leaf("show", "Show a server: its whole definition (what `server set --stdin` takes) and where each of its models stands.");
        show.Add("<id>", "The server.", value => showId = value);
        show.Add("machine=", "The machine. Defaults to this computer.", value => showMachine = value);
        show.Add(async (_, _) => await WithLlamaMachineAsync(context, showMachine, manage: false, async (servers, target) =>
        {
            var status = await servers.StatusAsync(target.Id, context.CancellationToken).ConfigureAwait(false);
            if (status.Servers.FirstOrDefault(server => string.Equals(server.Definition.Id, showId, StringComparison.OrdinalIgnoreCase)) is not { } found)
                return NotFound(context, "llama.serverNotFound", $"No server '{showId}' on {target.Name}. Servers: {string.Join(", ", status.Servers.Select(static server => server.Definition.Id))}.");
            WriteLlamaServer(context, target.Id, found, withDefinition: true);
            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        group.Add(show);

        string? logMachine = null, logId = null;
        var lines = 80;
        var log = Leaf("log", "Read the end of the log of a server: why it failed to start or to load a model.");
        log.Add("<id>", "The server.", value => logId = value);
        log.Add("machine=", "The machine. Defaults to this computer.", value => logMachine = value);
        log.Add("lines=", "How many lines (80 by default, 2000 at most).", value => lines = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 1, 2000) : 80);
        log.Add(async (_, _) => await RunLlamaActionAsync(context, logMachine, new ModelHostAction { Action = "serverLog", Id = NormalizeOptionalText(logId), Lines = lines }, startsPrograms: false,
            static result => new { type = "alta.llama.log", lines = result.Log }, manage: false).ConfigureAwait(false));
        group.Add(log);

        string? importMachine = null, importName = null;
        var import = Leaf("import", "Read the command line of a llama-server (on stdin) into the definition of a server, without saving it: `server add --stdin` saves it.");
        import.Add("name=", "The name of the server. Defaults to its model.", value => importName = value);
        import.Add("machine=", "The machine. Defaults to this computer.", value => importMachine = value);
        import.Add("stdin", "Read the command line from stdin (the default).", _ => { });
        import.Add(async (_, _) =>
        {
            var text = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return UsageError(context, "usage.missingCommandLine", "Give the command line of the llama-server on stdin.", "alta llama server import");
            return await RunLlamaActionAsync(context, importMachine, new ModelHostAction { Action = "import", Text = text, Name = NormalizeOptionalText(importName) }, startsPrograms: false,
                static result => new { type = "alta.llama.imported", server = result.Imported, ignored = result.Notes }, manage: false).ConfigureAwait(false);
        });
        group.Add(import);

        group.Add(CreateLlamaServerAddCommand(context, "add"));
        group.Add(CreateLlamaServerAddCommand(context, "set"));

        foreach (var verb in new[] { "start", "stop", "restart", "load", "unload" })
        {
            string? machine = null, id = null, model = null;
            var leaf = Leaf(verb, verb switch
            {
                "start" => "Start a server.",
                "stop" => "Stop a server: it frees its GPUs.",
                "restart" => "Restart a server.",
                "load" => "Load a model of a server of several models now.",
                _ => "Unload a model of a server of several models: it frees its GPU.",
            });
            leaf.Add("<id>", "The server.", value => id = value);
            if (verb is "load" or "unload") leaf.Add("<model>", "The model.", value => model = value);
            leaf.Add("machine=", "The machine. Defaults to this computer.", value => machine = value);
            leaf.Add(async (_, _) =>
            {
                if (NormalizeOptionalText(id) is not { } server) return UsageError(context, "usage.missingServer", "Name the server.", $"alta llama server {verb}");
                return await RunLlamaActionAsync(context, machine, new ModelHostAction { Action = "controlServer", Id = server, Verb = verb, Model = NormalizeOptionalText(model) },
                    startsPrograms: verb is "start" or "restart" or "load", static result => new { type = "alta.llama.server", server = result.Server is { } view ? LlamaServerRecord(view) : null }).ConfigureAwait(false);
            });
            group.Add(leaf);
        }

        string? testMachine = null, testId = null, testModel = null;
        var test = Leaf("test", "Test a server: a short answer from a model (loaded first when needed), with its speed.");
        test.Add("<id>", "The server.", value => testId = value);
        test.Add("model=", "The model. Defaults to its first one.", value => testModel = value);
        test.Add("machine=", "The machine. Defaults to this computer.", value => testMachine = value);
        test.Add(async (_, _) => await RunLlamaActionAsync(context, testMachine, new ModelHostAction { Action = "testServer", Id = NormalizeOptionalText(testId), Model = NormalizeOptionalText(testModel) },
            startsPrograms: true, static result => new { type = "alta.llama.test", test = result.Test }).ConfigureAwait(false));
        group.Add(test);

        string? useMachine = null, useId = null, useName = null;
        var useOn = new List<string>();
        var use = Leaf("use", "Add a provider for a server, so that the sessions of this computer (and of its machine) use its models.");
        use.Add("<id>", "The server.", value => useId = value);
        use.Add("machine=", "The machine of the server. Defaults to this computer.", value => useMachine = value);
        use.Add("name=", "The name of the provider. Defaults to the name of the server.", value => useName = value);
        use.Add("on=", "Whose sessions: `local` (this computer, the default) or the machine of the server. Repeat for both.", value => { if (NormalizeOptionalText(value) is { } text) useOn.Add(text); });
        use.Add(async (_, _) => await WithLlamaMachineAsync(context, useMachine, manage: true, async (servers, target) =>
        {
            var status = await servers.StatusAsync(target.Id, context.CancellationToken).ConfigureAwait(false);
            if (status.Servers.FirstOrDefault(server => string.Equals(server.Definition.Id, useId, StringComparison.OrdinalIgnoreCase)) is not { } found)
                return NotFound(context, "llama.serverNotFound", $"No server '{useId}' on {target.Name}.");
            var known = await servers.ListMachinesAsync(context.CancellationToken).ConfigureAwait(false);
            var on = new List<string>();
            foreach (var given in useOn.Count == 0 ? ["local"] : useOn)
            {
                if (FindLlamaMachine(known, given) is not { } machine) return NotFound(context, "llama.machineNotFound", $"No machine is named '{given}'.");
                on.Add(machine.Id);
            }

            return await WriteLlamaUseAsync(context, servers, target.Id, found.Definition, NormalizeOptionalText(useName) ?? found.Definition.Name, on).ConfigureAwait(false);
        }).ConfigureAwait(false));
        group.Add(use);

        string? removeMachine = null, removeId = null;
        var remove = Leaf("remove", "Remove a server: it stops and its settings go; its models stay on the disk. Only when the user asks.");
        remove.Add("<id>", "The server.", value => removeId = value);
        remove.Add("machine=", "The machine. Defaults to this computer.", value => removeMachine = value);
        remove.Add(async (_, _) => await RunLlamaActionAsync(context, removeMachine, new ModelHostAction { Action = "removeServer", Id = NormalizeOptionalText(removeId) }, startsPrograms: false,
            static _ => new { type = "alta.llama.removed" }).ConfigureAwait(false));
        group.Add(remove);
        AddHelpText(group, "A server of several models loads each at its first request and frees the GPU of the least used one when it needs room; `--loaded-at-once` keeps more loaded.",
            "Its context, when not given, is what fits the GPUs. Read `alta llama server log <id>` when a server or a model fails.");
        return group;
    }

    private static Command CreateLlamaServerAddCommand(AltaCommandContext context, string verb)
    {
        var adding = verb == "add";
        string? machine = null, id = null, name = null, kind = null, backend = null, purpose = null, access = null, port = null, contextText = null, parallel = null, loaded = null;
        var models = new List<string>();
        var devices = new List<string>();
        var extra = new List<string>();
        bool stdin = false, use = false, keepRunning = false, stopped = false;
        var command = Leaf(verb, adding
            ? "Add a server to a machine and start it: its models are downloaded first when missing, and its build of llama.cpp installed."
            : "Change a server: the options given replace its settings, or `--stdin` gives its whole definition (as `server show` writes it).");
        if (!adding) command.Add("<id>", "The server.", value => id = value);
        command.Add("machine=", "The machine. Defaults to this computer.", value => machine = value);
        command.Add("name=", "Its name.", value => name = value);
        command.Add("model=", "A model, `publisher/repository:QUANT`. Repeat for a server of several models; replaces its models.", value => { if (NormalizeOptionalText(value) is { } text) models.Add(text); });
        command.Add("kind=", "`several` (models loaded on demand, the default) or `one` (one model always loaded).", value => kind = value);
        command.Add("purpose=", "`chat` (default), `embedding` or `rerank` (a server of one model).", value => purpose = value);
        command.Add("backend=", "Its build: cuda, rocm, vulkan, metal or cpu. Defaults to the best installed one.", value => backend = value);
        command.Add("gpu=", "A device it runs on (`CUDA0`, `ROCm1`, `Vulkan0`). Repeat for several; none for every GPU of its build.", value => { if (NormalizeOptionalText(value) is { } text) devices.Add(text); });
        command.Add("context=", "The context of its models, in tokens. Defaults to what fits.", value => contextText = value);
        command.Add("parallel=", "How many requests each model serves at once.", value => parallel = value);
        command.Add("loaded-at-once=", "How many models it keeps loaded at once (several).", value => loaded = value);
        command.Add("port=", "Its port on loopback. Defaults to the first free one from 9931.", value => port = value);
        command.Add("access=", "`my-computers` (this machine and the user's computers, the default) or `this-machine`.", value => access = value);
        command.Add("arg=", "An option of llama-server added to its command line (`--jinja`). Repeat for several.", value => { if (NormalizeOptionalText(value) is { } text) extra.Add(text); });
        command.Add("keep-running", "Keep it running when CodeAlta exits.", value => keepRunning = value is not null);
        command.Add("stopped", "Save it without starting it.", value => stopped = value is not null);
        command.Add("use", "Add a provider so that the sessions of this computer use it (as `server use`).", value => use = value is not null);
        command.Add("stdin", "Read the whole definition of the server as JSON from stdin.", value => stdin = value is not null);
        command.Add(async (_, _) => await WithLlamaMachineAsync(context, machine, manage: true, async (servers, target) =>
        {
            var status = await servers.StatusAsync(target.Id, context.CancellationToken).ConfigureAwait(false);
            ServerDefinition definition;
            ServerView? existing = null;
            if (!adding)
            {
                existing = status.Servers.FirstOrDefault(server => string.Equals(server.Definition.Id, id, StringComparison.OrdinalIgnoreCase));
                if (existing is null) return NotFound(context, "llama.serverNotFound", $"No server '{id}' on {target.Name}.");
            }

            if (stdin)
            {
                ServerDefinition? read;
                try
                {
                    read = JsonSerializer.Deserialize<ServerDefinition>(await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false), LlamaJson);
                }
                catch (JsonException exception)
                {
                    return UsageError(context, "usage.invalidServer", "The definition on stdin is not the JSON of a server: " + exception.Message, $"alta llama server {verb}");
                }

                if (read is null) return UsageError(context, "usage.invalidServer", "Give the definition of the server on stdin.", $"alta llama server {verb}");
                definition = adding ? read : read with { Id = existing!.Definition.Id };
            }
            else
            {
                var installed = status.Builds.Where(static build => build.Current).Select(static build => build.Backend).ToList();
                var best = status.Candidates.Where(candidate => installed.Contains(candidate.Backend)).OrderByDescending(static candidate => candidate.Score).Select(static candidate => candidate.Backend).FirstOrDefault()
                    ?? status.Candidates.Where(static candidate => candidate.Verdict == "fits").OrderByDescending(static candidate => candidate.Score).Select(static candidate => candidate.Backend).FirstOrDefault()
                    ?? "cpu";
                var start = existing?.Definition ?? new ServerDefinition { Id = "", Name = "", Backend = best };
                var modelList = start.Models;
                if (models.Count > 0)
                {
                    var parsed = new List<ServerModel>();
                    foreach (var given in models)
                    {
                        if (LlamaModel(given) is not { } reference) return UsageError(context, "usage.invalidModel", $"'{given}' is not publisher/repository:QUANT.", $"alta llama server {verb}");
                        parsed.Add(start.Models.FirstOrDefault(item => string.Equals(item.Model, reference, StringComparison.OrdinalIgnoreCase)) ?? new ServerModel { Model = reference });
                    }

                    modelList = parsed;
                }

                if (int.TryParse(contextText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contextSize) && contextSize > 0) modelList = [.. modelList.Select(item => item with { Context = contextSize })];
                if (int.TryParse(parallel, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parallelCount) && parallelCount > 0) modelList = [.. modelList.Select(item => item with { Parallel = parallelCount })];
                var kindValue = NormalizeOptionalText(kind)?.ToLowerInvariant() ?? start.Kind;
                if (kindValue is not (ServerKinds.Several or ServerKinds.One)) return UsageError(context, "usage.invalidKind", "--kind is several or one.", $"alta llama server {verb}");
                if (kindValue == ServerKinds.One) modelList = [.. modelList.Select(static item => item with { LoadOnStart = true })];
                var accessValue = NormalizeOptionalText(access)?.ToLowerInvariant() ?? start.Access;
                if (accessValue is not ("my-computers" or "this-machine")) return UsageError(context, "usage.invalidAccess", "--access is my-computers or this-machine.", $"alta llama server {verb}");
                var portValue = start.Port;
                if (NormalizeOptionalText(port) is { } portText && (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out portValue) || portValue is < 1024 or > 65535))
                    return UsageError(context, "usage.invalidPort", "--port is between 1024 and 65535.", $"alta llama server {verb}");
                var nameValue = NormalizeOptionalText(name) ?? (start.Name.Length > 0 ? start.Name : modelList.FirstOrDefault()?.Model.Split('/').Last().Split(':')[0].Replace("-GGUF", "", StringComparison.OrdinalIgnoreCase) ?? "");
                definition = start with
                {
                    Name = nameValue,
                    Kind = kindValue,
                    Purpose = NormalizeOptionalText(purpose)?.ToLowerInvariant() ?? start.Purpose,
                    Backend = NormalizeOptionalText(backend)?.ToLowerInvariant() ?? start.Backend,
                    Devices = devices.Count > 0 ? devices : start.Devices,
                    LoadedAtOnce = int.TryParse(loaded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var loadedCount) && loadedCount > 0 ? loadedCount : start.LoadedAtOnce,
                    Port = portValue,
                    Access = accessValue,
                    ExtraArguments = extra.Count > 0 ? extra : start.ExtraArguments,
                    KeepRunning = keepRunning || start.KeepRunning,
                    Enabled = !stopped && (adding || start.Enabled),
                    Models = modelList,
                };
            }

            if (definition.Name.Length == 0) return UsageError(context, "usage.missingName", "Give the server a name: --name <name>.", $"alta llama server {verb}");
            if (definition.Models.Count == 0) return UsageError(context, "usage.missingModel", "Give the server a model: --model <publisher/repository:QUANT>.", $"alta llama server {verb}");
            if (definition.Enabled && await LlamaStartRefusalAsync(context, "start a llama.cpp server").ConfigureAwait(false) is { } refused) return refused;
            var result = await servers.RunAsync(target.Id, new ModelHostAction { Action = "setServer", Server = definition }, SessionOf(context), context.CancellationToken).ConfigureAwait(false);
            if (result.Server is not { } saved) return Unsupported(context, "llama.notSaved", "The machine did not save the server.");
            WriteLlamaServer(context, target.Id, saved, withDefinition: true);
            var missing = saved.Definition.Models.Where(item => !status.Models.Any(model => string.Equals(model.Id, item.Model, StringComparison.OrdinalIgnoreCase))).Select(static item => item.Model).ToList();
            if (missing.Count > 0)
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new
                {
                    type = "alta.llama.note", version = 1, correlationId = context.CorrelationId,
                    note = $"{string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} downloaded first: the server starts once {(missing.Count == 1 ? "it is" : "they are")} on the disk, and each download tells this session its end.",
                });
            }

            if (use && saved.Definition.Purpose == "chat")
                return await WriteLlamaUseAsync(context, servers, target.Id, saved.Definition, saved.Definition.Name, ["local"]).ConfigureAwait(false);
            return AltaExitCodes.Success;
        }).ConfigureAwait(false));
        if (adding) AddHelpText(command, "Example: `alta llama server add --model unsloth/Qwen3-8B-GGUF:Q4_K_M --name \"Qwen3 8B\" --use`.");
        return command;
    }

    private static async Task<int> WriteLlamaUseAsync(AltaCommandContext context, IAltaModelServers servers, string machine, ServerDefinition server, string name, IReadOnlyList<string> on)
    {
        var (provider, results) = await servers.UseInSessionsAsync(machine, server.Id, name, on, context.CancellationToken).ConfigureAwait(false);
        var failed = results.Where(static pair => pair.Value is not null).ToList();
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.llama.provider", version = 1, correlationId = context.CorrelationId,
            provider, name, machine, server = server.Id,
            machines = results.Select(static pair => new { machine = pair.Key, problem = pair.Value }),
            note = failed.Count == 0 ? $"The sessions use the models of this server through the provider `{provider}`: `alta model list` lists them." : null,
        });
        return failed.Count == 0 ? AltaExitCodes.Success : AltaExitCodes.Failure;
    }

    // --- helpers ---

    private static void WriteLlamaServer(AltaCommandContext context, string machine, ServerView server, bool withDefinition)
        => AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.llama.server", version = 1, correlationId = context.CorrelationId, machine,
            server = LlamaServerRecord(server),
            definition = withDefinition ? server.Definition : null,
        });

    private static object LlamaServerRecord(ServerView server) => new
    {
        server.Definition.Id, server.Definition.Name, server.Definition.Kind, server.Definition.Purpose, server.Definition.Backend, server.Definition.Port, server.Definition.Access,
        server.Definition.Enabled, server.Status.State, server.Status.Address, build = server.Status.BuildTag, server.Status.Reason, server.Status.Restarts, models = server.Status.Models,
    };

    private static object LlamaBuildRecord(InstalledBuild build) => new
    {
        build.Tag, build.Build, build.Backend, build.BackendVersion, build.Current, build.Previous, build.Bad, build.SizeBytes, build.InstalledAt, build.Source,
        devices = build.Devices.Select(static device => new { device.Name, device.Description, device.TotalMiB }),
    };

    private static object? LlamaLatestRecord(LlamaRelease? release)
        => release is null ? null : new { release.Tag, release.Build, release.Version, release.PublishedAt };

    private static bool LlamaUpdateAvailable(ModelHostStatus status)
        => status.Latest is { } latest && status.Builds.Any(build => build.Current && build.Source != "own" && build.Build < latest.Build);

    private static string? SessionOf(AltaCommandContext context) => NormalizeOptionalText(context.Caller.SourceSessionId);

    private static string? LlamaRepo(string? text)
    {
        var value = NormalizeOptionalText(text);
        if (value is null) return null;
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var address) && address.Host.EndsWith("huggingface.co", StringComparison.OrdinalIgnoreCase))
            value = string.Join('/', address.AbsolutePath.Trim('/').Split('/').Take(2));
        if (value.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase)) value = value[6..];
        var colon = value.LastIndexOf(':');
        if (colon > 0) value = value[..colon];
        var parts = value.Split('/');
        return parts.Length == 2 && parts.All(static part => part.Length > 0 && part.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) ? value : null;
    }

    private static string? LlamaModel(string? text)
    {
        var value = NormalizeOptionalText(text);
        if (value is null || LlamaRepo(value) is not { } repo) return null;
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1) return null;
        var tag = value[(colon + 1)..];
        return tag.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? $"{repo}:{tag.ToUpperInvariant()}" : null;
    }

    private static AltaModelServersMachine? FindLlamaMachine(IReadOnlyList<AltaModelServersMachine> machines, string given)
    {
        if (given is "local" or "this" or "here") return machines.FirstOrDefault(static machine => machine.Local);
        return machines.FirstOrDefault(machine => string.Equals(machine.Id, given, StringComparison.OrdinalIgnoreCase))
            ?? machines.FirstOrDefault(machine => string.Equals(machine.Name, given, StringComparison.OrdinalIgnoreCase))
            ?? machines.FirstOrDefault(machine => machine.Name.StartsWith(given + ".", StringComparison.OrdinalIgnoreCase));
    }

    // Why the caller may not download code or start a program through the model host, or null when it may.
    private static async Task<int?> LlamaStartRefusalAsync(AltaCommandContext context, string what)
    {
        if (ReviewsCommands(context))
            return PermissionDenied(context, "llama.startDenied", $"The user reviews the commands of this session: it cannot {what}. Ask the user to do it in Settings > Fleet > Model servers.");
        if (await ShellTurnedOffAsync(context).ConfigureAwait(false)) return PermissionDenied(context, "llama.shellDisabled", ShellTurnedOffMessage(what));
        return null;
    }

    private static async Task<int> WithLlamaAsync(AltaCommandContext context, Func<IAltaModelServers, Task<int>> body)
    {
        if (!context.TryGetRequired<IAltaModelServers>(nameof(IAltaModelServers), out var servers)) return AltaExitCodes.ServiceUnavailable;
        try
        {
            return await body(servers).ConfigureAwait(false);
        }
        catch (ModelHostException exception)
        {
            return LlamaFailure(context, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or TimeoutException or UnauthorizedAccessException or InvalidDataException)
        {
            return LlamaFailure(context, "failed", exception.Message);
        }
    }

    private static Task<int> WithLlamaMachineAsync(AltaCommandContext context, string? given, bool manage, Func<IAltaModelServers, AltaModelServersMachine, Task<int>> body)
        => WithLlamaAsync(context, async servers =>
        {
            var machines = await servers.ListMachinesAsync(context.CancellationToken).ConfigureAwait(false);
            AltaModelServersMachine? machine;
            if (NormalizeOptionalText(given) is { } name)
            {
                machine = FindLlamaMachine(machines, name);
                if (machine is null)
                    return NotFound(context, "llama.machineNotFound", $"No machine is named '{name}'. Machines: {string.Join(", ", machines.Select(static item => item.Local ? $"{item.Name} (local)" : item.Name))}.");
            }
            else
            {
                machine = machines.FirstOrDefault(static item => item.Local);
                if (machine is null)
                {
                    var managed = machines.Where(static item => item.Manages).ToList();
                    if (managed.Count != 1)
                        return UsageError(context, "usage.missingMachine", $"This computer runs no model host: name the machine with --machine. Machines: {string.Join(", ", machines.Select(static item => item.Name))}.", "alta llama");
                    machine = managed[0];
                }
            }

            if (manage && !machine.Manages)
                return PermissionDenied(context, "llama.notManaged", $"This computer uses the model servers of {machine.Name} but does not manage it: the user gives it Manage on that machine, or does it there.");
            if (!machine.Local && machine.State != "online")
                return Unsupported(context, "llama.offline", $"{machine.Name} is {machine.State}{(machine.Problem is { } problem ? $": {problem}" : "")}.");
            return await body(servers, machine).ConfigureAwait(false);
        });

    private static Task<int> RunLlamaActionAsync(AltaCommandContext context, string? machine, ModelHostAction action, bool startsPrograms, Func<ModelHostActionResult, object> record, bool manage = true)
        => WithLlamaMachineAsync(context, machine, manage, async (servers, target) =>
        {
            if (startsPrograms && await LlamaStartRefusalAsync(context, LlamaWhat(action)).ConfigureAwait(false) is { } refused) return refused;
            if (action.Id is null && action.Action is "serverLog" or "testServer" or "removeServer")
                return UsageError(context, "usage.missingServer", "Name the server.", "alta llama server");
            var result = await servers.RunAsync(target.Id, action, SessionOf(context), context.CancellationToken).ConfigureAwait(false);
            var fields = JsonSerializer.SerializeToElement(record(result), LlamaJson);
            var properties = new Dictionary<string, object?> { ["type"] = null, ["version"] = 1, ["correlationId"] = context.CorrelationId, ["machine"] = target.Id };
            foreach (var property in fields.EnumerateObject()) properties[property.Name] = property.Value;
            AltaJsonlWriter.WriteRecord(context.Stdout, properties);
            return AltaExitCodes.Success;
        });

    // An action on every machine this computer manages and reaches now: a record per machine, an error record for each that
    // refuses; it succeeds when one machine did it.
    private static Task<int> RunLlamaActionOnAllAsync(AltaCommandContext context, ModelHostAction action, Func<ModelHostActionResult, object> record)
        => WithLlamaAsync(context, async servers =>
        {
            if (await LlamaStartRefusalAsync(context, LlamaWhat(action)).ConfigureAwait(false) is { } refused) return refused;
            var machines = (await servers.ListMachinesAsync(context.CancellationToken).ConfigureAwait(false))
                .Where(static machine => machine.Manages && (machine.Local || machine.State == "online")).ToList();
            if (machines.Count == 0) return NotFound(context, "llama.noMachine", "This computer manages no machine it reaches now.");
            var done = 0;
            foreach (var machine in machines)
            {
                try
                {
                    var result = await servers.RunAsync(machine.Id, action, SessionOf(context), context.CancellationToken).ConfigureAwait(false);
                    var properties = new Dictionary<string, object?> { ["type"] = null, ["version"] = 1, ["correlationId"] = context.CorrelationId, ["machine"] = machine.Id };
                    foreach (var property in JsonSerializer.SerializeToElement(record(result), LlamaJson).EnumerateObject()) properties[property.Name] = property.Value;
                    AltaJsonlWriter.WriteRecord(context.Stdout, properties);
                    done++;
                }
                catch (ModelHostException exception)
                {
                    AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "llama." + exception.Code, AltaExitCodes.Failure, $"{machine.Name}: {exception.Message}");
                }
            }

            return done > 0 ? AltaExitCodes.Success : AltaExitCodes.Failure;
        });

    private static string LlamaWhat(ModelHostAction action) => action.Action switch
    {
        "installBuilds" => "install a build of llama.cpp",
        "useBuild" => "switch the build of llama.cpp of its servers",
        "planUpdate" or "updateNow" => "update llama.cpp",
        "testServer" => "test a llama.cpp server",
        _ => "start a llama.cpp server",
    };

    private static int LlamaFailure(AltaCommandContext context, string code, string message)
    {
        var exit = code switch
        {
            "not_found" => AltaExitCodes.NotFound,
            "invalid" or "port_taken" => AltaExitCodes.Usage,
            "forbidden" => AltaExitCodes.PolicyDenied,
            "offline" or "unavailable" or "unreachable" => AltaExitCodes.ServiceUnavailable,
            "unsupported" => AltaExitCodes.Unsupported,
            _ => AltaExitCodes.Failure,
        };
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "llama." + code, exit, message);
        return exit;
    }
}
