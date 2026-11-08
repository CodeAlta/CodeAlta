using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeAlta.ModelServers.Servers;

/// <summary>A command line imported from a llama-server command: the server it gives, and what was not taken.</summary>
/// <param name="Model">The model it names, <c>org/repo:TAG</c>, or null.</param>
/// <param name="Settings">The settings of the model.</param>
/// <param name="Devices">The devices it names (from <c>--device</c> or <c>HIP_VISIBLE_DEVICES</c>/<c>CUDA_VISIBLE_DEVICES</c>).</param>
/// <param name="VisibleDevices">The indexes of <c>HIP_VISIBLE_DEVICES</c> or <c>CUDA_VISIBLE_DEVICES</c>, to map to device names.</param>
/// <param name="Port">The port it names, or null.</param>
/// <param name="Ignored">What was not taken, as written.</param>
public sealed record ImportedCommand(string? Model, ServerModel? Settings, IReadOnlyList<string> Devices, IReadOnlyList<int> VisibleDevices, int? Port,
    IReadOnlyList<string> Ignored);

/// <summary>
/// The command line of a server and the preset of its models. CodeAlta writes only options it knows for the build that
/// runs (llama.cpp renames flags and its presets refuse unknown keys), and never an option that makes llama.cpp run
/// commands, open the network, read or write files, or download (<see cref="IsRefused"/>), also not from the extra
/// arguments of a person or an agent.
/// </summary>
public static partial class ServerArguments
{
    /// <summary>The first build whose router has the options CodeAlta gives it.</summary>
    public const int MinimumRouterBuild = 10000;

    private static readonly FrozenSet<string> Refused = FrozenSet.Create(StringComparer.Ordinal,
    [
        // Run commands, act as an agent, open tunnels or the network.
        "tools", "tools-runtime", "agent", "ag", "mcp-servers-config", "mcp-servers-json", "ui-mcp-proxy", "connect", "rpc",
        // Read or write files of the machine.
        "media-path", "slot-save-path", "path", "props", "log-file", "lora", "lora-scaled", "chat-template-file", "ui-config-file", "models-dir", "models-preset",
        // Download, or point somewhere else.
        "model-url", "mu", "hf-repo", "hf", "hfr", "hf-file", "hff", "hf-token", "hft", "docker-repo", "dr", "mmproj-url", "mmu", "spec-draft-hf", "hfd",
        // What CodeAlta sets itself.
        "host", "port", "api-key", "api-key-file", "ssl-key-file", "ssl-cert-file", "cors-origins", "cors-methods", "cors-headers", "cors-credentials",
        "model", "m", "mmproj", "mm", "alias", "a", "offline", "models-max", "models-memory-margin", "models-autoload", "no-models-autoload", "webui", "no-webui",
        "ui", "no-ui", "api-prefix",
    ]);

    /// <summary>Whether an option may not be given by a person or an agent: one that runs commands, opens the network, touches files, downloads, or that CodeAlta sets.</summary>
    /// <param name="option">The option, with or without its dashes.</param>
    /// <returns>Whether it is refused.</returns>
    public static bool IsRefused(string option)
    {
        var name = option.TrimStart('-').Split('=', 2)[0].ToLowerInvariant().Replace('_', '-');
        return Refused.Contains(name) || name.StartsWith("mcp", StringComparison.Ordinal) || name.StartsWith("tools", StringComparison.Ordinal)
            || name.StartsWith("ssl", StringComparison.Ordinal) || name.StartsWith("hf-", StringComparison.Ordinal);
    }

    /// <summary>Checks the options a person or an agent gave a server.</summary>
    /// <param name="definition">The server.</param>
    /// <exception cref="ModelHostException">An option is refused, or its value is not plain.</exception>
    public static void Validate(ServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!IdPattern().IsMatch(definition.Id)) throw new ModelHostException("invalid", "The id of a server is 1 to 40 letters, digits and '-'.");
        if (definition.Port is < 1024 or > 65535) throw new ModelHostException("invalid", "The port of a server is between 1024 and 65535.");
        if (definition.Kind is not (ServerKinds.Several or ServerKinds.One)) throw new ModelHostException("invalid", "A server serves several models or one.");
        if (definition.Kind == ServerKinds.One && definition.Models.Count != 1) throw new ModelHostException("invalid", "A one-model server has exactly one model.");
        if (definition.Purpose != "chat" && definition.Kind != ServerKinds.One) throw new ModelHostException("invalid", "Embeddings and rerank are served by a one-model server.");
        foreach (var argument in definition.ExtraArguments)
        {
            if (argument.StartsWith('-') && IsRefused(argument)) throw new ModelHostException("refused_option", $"{argument} cannot be given to a server: CodeAlta sets it, or it runs commands, touches files or downloads.");
            if (argument.Any(static c => char.IsControl(c))) throw new ModelHostException("invalid", "An argument has a control character.");
        }

        foreach (var model in definition.Models)
        {
            foreach (var (key, value) in model.Options)
            {
                if (!KeyPattern().IsMatch(key) || IsRefused(key)) throw new ModelHostException("refused_option", $"{key} cannot be given to a model.");
                if (value.Any(static c => char.IsControl(c)) || value.Contains('\n')) throw new ModelHostException("invalid", $"The value of {key} has a control character.");
            }

            foreach (var device in model.Devices.Concat(definition.Devices))
            {
                if (!DevicePattern().IsMatch(device)) throw new ModelHostException("invalid", $"'{device}' is not a device of llama.cpp (CUDA0, ROCm1, Vulkan2...).");
            }

            if (model.ReasoningEffort is { } effort && effort is not ("none" or "low" or "medium" or "high" or "max"))
                throw new ModelHostException("invalid", $"'{effort}' is not a reasoning effort.");
            if (model.CacheType is { } cache && cache is not ("f32" or "f16" or "bf16" or "q8_0" or "q4_0" or "q4_1" or "iq4_nl" or "q5_0" or "q5_1"))
                throw new ModelHostException("invalid", $"'{cache}' is not a KV cache type.");
            if (model.FlashAttention is { } fa && fa is not ("on" or "off" or "auto")) throw new ModelHostException("invalid", "Flash attention is on, off or auto.");
        }
    }

    /// <summary>The command line of a server.</summary>
    /// <param name="definition">The server.</param>
    /// <param name="build">The number of its build.</param>
    /// <param name="keysPath">Its key file.</param>
    /// <param name="presetPath">Its preset, for a router.</param>
    /// <param name="logPath">Its log.</param>
    /// <param name="paths">Gives the files of a model: its main file and its projector.</param>
    /// <returns>The arguments.</returns>
    public static IReadOnlyList<string> CommandLine(ServerDefinition definition, int build, string keysPath, string presetPath, string logPath,
        Func<string, (string Model, string? Mmproj)?> paths)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(paths);
        var arguments = new List<string> { "--host", "127.0.0.1", "--port", definition.Port.ToString(CultureInfo.InvariantCulture), "--api-key-file", keysPath,
            "--offline", "--metrics", "--log-file", logPath };
        if (!definition.ChatPage) arguments.Add("--no-ui");
        if (build is 0 or >= 10300) arguments.AddRange(["--cors-origins", "localhost"]);
        if (definition.Kind == ServerKinds.Several)
        {
            arguments.AddRange(["--models-preset", presetPath, "--models-max", Math.Max(1, definition.LoadedAtOnce).ToString(CultureInfo.InvariantCulture)]);
            if (build is 0 or >= 12000) arguments.AddRange(["--models-memory-margin", Math.Max(0, definition.MemoryMarginMiB).ToString(CultureInfo.InvariantCulture)]);
            if (definition.Devices.Count > 0) arguments.AddRange(["--device", string.Join(',', definition.Devices)]);
        }
        else
        {
            var model = definition.Models[0];
            var files = paths(model.Model) ?? throw new ModelHostException("missing_model", $"{model.Model} is not on this machine.");
            arguments.AddRange(["--model", files.Model, "--alias", model.Model]);
            if (files.Mmproj is { } mmproj && model.Vision && definition.Purpose == "chat") arguments.AddRange(["--mmproj", mmproj]);
            if (definition.Purpose == "embedding") arguments.Add("--embedding");
            if (definition.Purpose == "rerank") arguments.Add("--rerank");
            foreach (var (key, value) in ModelOptions(model, definition)) arguments.AddRange(value == "true" ? ["--" + key] : ["--" + key, value]);
        }

        arguments.AddRange(definition.ExtraArguments);
        return arguments;
    }

    /// <summary>The preset of a router: one section per model, each named by its path in the store.</summary>
    /// <param name="definition">The server.</param>
    /// <param name="paths">Gives the files of a model.</param>
    /// <returns>The INI text, and the models that are not on disk.</returns>
    public static (string Preset, IReadOnlyList<string> Missing) Preset(ServerDefinition definition, Func<string, (string Model, string? Mmproj)?> paths)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(paths);
        var text = new StringBuilder("version = 1\n");
        var missing = new List<string>();
        foreach (var model in definition.Models)
        {
            if (paths(model.Model) is not { } files)
            {
                missing.Add(model.Model);
                continue;
            }

            text.Append('\n').Append('[').Append(model.Model).Append("]\n");
            text.Append("model = ").Append(files.Model.Replace('\\', '/')).Append('\n');
            if (files.Mmproj is { } mmproj && model.Vision) text.Append("mmproj = ").Append(mmproj.Replace('\\', '/')).Append('\n');
            foreach (var (key, value) in ModelOptions(model, definition)) text.Append(key).Append(" = ").Append(value).Append('\n');
            if (model.LoadOnStart) text.Append("load-on-startup = true\n");
        }

        return (text.ToString(), missing);
    }

    private static IEnumerable<(string Key, string Value)> ModelOptions(ServerModel model, ServerDefinition definition)
    {
        var devices = model.Devices.Count > 0 ? model.Devices : definition.Devices;
        if (devices.Count > 0) yield return ("device", string.Join(',', devices));
        if (model.Context is { } context) yield return ("ctx-size", context.ToString(CultureInfo.InvariantCulture));
        if (model.Parallel is { } parallel) yield return ("parallel", parallel.ToString(CultureInfo.InvariantCulture));
        if (model.ReasoningEffort is { } effort) yield return ("reasoning-effort", effort);
        if (model.CacheType is { } cache)
        {
            yield return ("cache-type-k", cache);
            yield return ("cache-type-v", cache);
        }

        if (model.FlashAttention is { } fa) yield return ("flash-attn", fa);
        var sampling = model.Sampling;
        if (sampling.Temperature is { } temperature) yield return ("temp", Number(temperature));
        if (sampling.TopP is { } topP) yield return ("top-p", Number(topP));
        if (sampling.TopK is { } topK) yield return ("top-k", topK.ToString(CultureInfo.InvariantCulture));
        if (sampling.MinP is { } minP) yield return ("min-p", Number(minP));
        if (sampling.PresencePenalty is { } presence) yield return ("presence-penalty", Number(presence));
        if (sampling.RepeatPenalty is { } repeat) yield return ("repeat-penalty", Number(repeat));
        if (model.FreeGpuAfterMinutes is { } minutes and > 0) yield return ("sleep-idle-seconds", (minutes * 60).ToString(CultureInfo.InvariantCulture));
        foreach (var (key, value) in model.Options) yield return (key, value);
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Reads a llama-server command (a line of a script), with the variables it sets before it.</summary>
    /// <param name="text">The command, or a small script.</param>
    /// <returns>What it gives.</returns>
    public static ImportedCommand Import(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\\\r\n", " ", StringComparison.Ordinal).Replace("\\\n", " ", StringComparison.Ordinal).Split('\n');
        List<string>? words = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var assignment = AssignmentPattern().Match(line);
            if (assignment.Success)
            {
                environment[assignment.Groups[1].Value] = Expand(assignment.Groups[2].Value.Trim('"', '\''), environment);
                continue;
            }

            if (line.Contains("llama-server", StringComparison.Ordinal) || line.Contains("llama serve", StringComparison.Ordinal))
            {
                words = Split(Expand(line, environment));
                break;
            }
        }

        if (words is null) return new ImportedCommand(null, null, [], [], null, ["No llama-server command was found."]);
        var start = words.FindIndex(static word => word.EndsWith("llama-server", StringComparison.Ordinal) || word.EndsWith("llama-server.exe", StringComparison.Ordinal) || word.EndsWith("llama", StringComparison.Ordinal));
        if (start >= 0 && start + 1 < words.Count && words[start + 1] == "serve") start++;
        var ignored = new List<string>();
        string? model = null;
        int? port = null, context = null, parallel = null, topK = null;
        double? temperature = null, topP = null, minP = null, presence = null, repeat = null;
        string? effort = null, cache = null, flash = null;
        var devices = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = start + 1; i < words.Count; i++)
        {
            var word = words[i];
            if (!word.StartsWith('-'))
            {
                ignored.Add(word);
                continue;
            }

            var name = word.TrimStart('-').Replace('_', '-');
            string? value = null;
            if (name.Contains('=', StringComparison.Ordinal))
            {
                value = name[(name.IndexOf('=', StringComparison.Ordinal) + 1)..];
                name = name[..name.IndexOf('=', StringComparison.Ordinal)];
            }
            else if (i + 1 < words.Count && !words[i + 1].StartsWith("--", StringComparison.Ordinal) && !(words[i + 1].StartsWith('-') && !IsNumber(words[i + 1])) && TakesValue(name))
            {
                value = words[++i];
            }

            switch (name)
            {
                case "hf" or "hfr" or "hf-repo": model = NormalizeModel(value); break;
                case "port": port = Int(value); break;
                case "c" or "ctx-size": context = Int(value) is > 0 and var c ? c : null; break;
                case "np" or "parallel": parallel = Int(value) is > 0 and var p ? p : null; break;
                case "temp" or "temperature": temperature = Double(value); break;
                case "top-p": topP = Double(value); break;
                case "top-k": topK = Int(value); break;
                case "min-p": minP = Double(value); break;
                case "presence-penalty": presence = Double(value); break;
                case "repeat-penalty": repeat = Double(value); break;
                case "reasoning-effort": effort = value; break;
                case "ctk" or "cache-type-k" or "ctv" or "cache-type-v": cache = value; break;
                case "fa" or "flash-attn": flash = value; break;
                case "dev" or "device": devices.AddRange((value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                case "host" or "ngl" or "gpu-layers" or "n-gpu-layers" or "jinja" or "lv" or "verbosity" or "log-verbosity":
                    ignored.Add(value is null ? word : $"{word} {value}"); // CodeAlta sets the address, and --fit chooses the layers.
                    break;
                default:
                    if (IsRefused(name) || !KeyPattern().IsMatch(name)) ignored.Add(value is null ? word : $"{word} {value}");
                    else options[name] = value ?? "true";
                    break;
            }
        }

        var visible = (environment.GetValueOrDefault("HIP_VISIBLE_DEVICES") ?? environment.GetValueOrDefault("ROCR_VISIBLE_DEVICES") ?? environment.GetValueOrDefault("CUDA_VISIBLE_DEVICES") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(static index => int.TryParse(index, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .Where(static index => index >= 0).ToList();
        if (model is null) ignored.Insert(0, "No -hf model was found: choose the model after the import.");
        var settings = model is null ? null : new ServerModel
        {
            Model = model,
            Devices = devices,
            Context = context,
            Parallel = parallel,
            ReasoningEffort = effort,
            CacheType = cache,
            FlashAttention = flash,
            Sampling = new SamplingValues { Temperature = temperature, TopP = topP, TopK = topK, MinP = minP, PresencePenalty = presence, RepeatPenalty = repeat },
            Options = options,
        };
        return new ImportedCommand(model, settings, devices, visible, port, ignored);
    }

    private static string? NormalizeModel(string? value)
    {
        if (value is null) return null;
        var colon = value.LastIndexOf(':');
        return colon > 0 ? value[..colon] + ":" + value[(colon + 1)..].ToUpperInvariant() : value + ":Q4_K_M";
    }

    private static bool TakesValue(string name)
        => name is not ("jinja" or "no-jinja" or "embedding" or "rerank" or "metrics" or "no-mmap" or "cnv" or "no-webui" or "no-ui" or "verbose" or "offline");

    private static bool IsNumber(string word) => double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static int? Int(string? value) => int.TryParse(value, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static double? Double(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string Expand(string text, Dictionary<string, string> environment)
        => VariablePattern().Replace(text, match => environment.GetValueOrDefault(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) ?? match.Value);

    private static List<string> Split(string line)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        foreach (var c in line)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
            }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) words.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }

        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{0,39}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9\-]{0,40}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,15}\d+$")]
    private static partial Regex DevicePattern();

    [GeneratedRegex(@"^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)=(.*)$")]
    private static partial Regex AssignmentPattern();

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex VariablePattern();
}
