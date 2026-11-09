using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Tests.FakeLlamaServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A llama-server for the tests of the model host. It answers what CodeAlta uses of llama.cpp: --version, --list-devices,
// and a server on 127.0.0.1:--port with the key of --api-key-file, as a router (--models-preset) or for one model
// (--model). What it is comes from the fake-llama.json beside it, so that one program serves every test by its file.
var settings = FakeSettings.Read(Path.Combine(AppContext.BaseDirectory, "fake-llama.json"));
var options = FakeArguments.Parse(args);

if (options.Has("version"))
{
    Console.Error.WriteLine($"version: {settings.Build.ToString(CultureInfo.InvariantCulture)} ({settings.Commit})");
    Console.Error.WriteLine("built with the fake llama-server of the CodeAlta tests");
    return 0;
}

if (options.Has("list-devices"))
{
    Console.WriteLine("Available devices:");
    foreach (var device in settings.Devices)
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {device.Name}: {device.Description} ({device.TotalMiB} MiB, {device.FreeMiB} MiB free)"));
    return 0;
}

using var log = FakeLog.Open(options.Value("log-file"));
log.Write($"build: {settings.Build.ToString(CultureInfo.InvariantCulture)} ({settings.Commit}) with the fake llama-server of the CodeAlta tests");
log.Write("system_info: " + string.Join(' ', args));
if (settings.ExitAtOnce is { } failure)
{
    log.Write("error: " + failure);
    return 1;
}

if (!int.TryParse(options.Value("port"), CultureInfo.InvariantCulture, out var port))
{
    log.Write("error: --port is missing");
    return 2;
}

var key = options.Value("api-key-file") is { } keyFile && File.Exists(keyFile)
    ? File.ReadAllLines(keyFile).Select(static line => line.Trim()).FirstOrDefault(static line => line.Length > 0 && !line.StartsWith('#'))
    : options.Value("api-key");
var models = FakeModels.From(options);

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
// A router runs each model in a process of its own, on a port of loopback it chooses: the fake has one more port for them.
builder.WebHost.UseKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Loopback, port);
    if (models.Router) kestrel.Listen(IPAddress.Loopback, 0);
});
var app = builder.Build();
app.Run(context => FakeEndpoints.HandleAsync(context, settings, models, key, log));

// A fake left behind by a test that died ends by itself.
_ = Task.Delay(TimeSpan.FromSeconds(settings.LifetimeSeconds)).ContinueWith(_ =>
{
    log.Write("main: the lifetime of the fake ended");
    Environment.Exit(3);
}, TaskScheduler.Default);

try
{
    await app.StartAsync();
}
catch (IOException exception)
{
    log.Write($"error: couldn't bind HTTP server socket, port: {port.ToString(CultureInfo.InvariantCulture)}: {exception.Message}");
    return 1;
}

models.ProcessPort = app.Urls.Select(static url => new Uri(url).Port).FirstOrDefault(other => other != port);
log.Write($"main: server is listening on http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}");
await app.WaitForShutdownAsync();
return 0;

namespace CodeAlta.Tests.FakeLlamaServer
{
    /// <summary>What the fake is, read from its fake-llama.json.</summary>
    internal sealed record FakeSettings
    {
        public int Build { get; init; } = 12000;

        public string Commit { get; init; } = "fake000";

        public IReadOnlyList<FakeDevice> Devices { get; init; } = [];

        /// <summary><c>ok</c>, <c>error</c> (a 500) or <c>empty</c> (no token).</summary>
        public string Chat { get; init; } = "ok";

        public double TokensPerSecond { get; init; } = 50;

        /// <summary>An error the server writes in its log before it exits at once with code 1, or null.</summary>
        public string? ExitAtOnce { get; init; }

        public int LifetimeSeconds { get; init; } = 300;

        public static FakeSettings Read(string path)
        {
            if (!File.Exists(path)) return new FakeSettings();
            var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [];
            var defaults = new FakeSettings();
            return new FakeSettings
            {
                Build = root["build"]?.GetValue<int>() ?? defaults.Build,
                Commit = root["commit"]?.GetValue<string>() ?? defaults.Commit,
                Devices = root["devices"]?.AsArray().OfType<JsonObject>().Select(static device => new FakeDevice(
                    device["name"]!.GetValue<string>(), device["description"]?.GetValue<string>() ?? "Fake device",
                    device["totalMiB"]?.GetValue<long>() ?? 8192, device["freeMiB"]?.GetValue<long>() ?? 8000)).ToList() ?? [],
                Chat = root["chat"]?.GetValue<string>() ?? defaults.Chat,
                TokensPerSecond = root["tokensPerSecond"]?.GetValue<double>() ?? defaults.TokensPerSecond,
                ExitAtOnce = root["exitAtOnce"]?.GetValue<string>(),
                LifetimeSeconds = root["lifetimeSeconds"]?.GetValue<int>() ?? defaults.LifetimeSeconds,
            };
        }
    }

    internal sealed record FakeDevice(string Name, string Description, long TotalMiB, long FreeMiB);

    /// <summary>The options of the command line: <c>--name value</c>, or <c>--name</c> alone for a flag.</summary>
    internal sealed class FakeArguments
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        public static FakeArguments Parse(string[] args)
        {
            var result = new FakeArguments();
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
                var name = args[i][2..];
                string? value = null;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++i];
                result._values[name] = value;
            }

            return result;
        }

        public bool Has(string name) => _values.ContainsKey(name);

        public string? Value(string name) => _values.GetValueOrDefault(name);
    }

    /// <summary>The log of --log-file, which the host reads and rotates while it is written.</summary>
    internal sealed class FakeLog : IDisposable
    {
        private readonly StreamWriter? _writer;
        private readonly Lock _gate = new();

        private FakeLog(StreamWriter? writer) => _writer = writer;

        public static FakeLog Open(string? path)
        {
            if (path is null) return new FakeLog(null);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new FakeLog(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
        }

        public void Write(string line)
        {
            lock (_gate)
            {
                try { _writer?.WriteLine(line); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            lock (_gate) _writer?.Dispose();
        }
    }

    /// <summary>The models the server serves: the sections of its preset (a router), or its --model (one model).</summary>
    internal sealed class FakeModels
    {
        private readonly Lock _gate = new();
        private readonly List<(string Name, string Path)> _models = [];
        private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);

        public bool Router { get; private init; }

        /// <summary>Gets or sets the port of the processes of the models of a router; 0 before it listens.</summary>
        public int ProcessPort { get; set; }

        public static FakeModels From(FakeArguments options)
        {
            if (options.Value("models-preset") is { } preset)
            {
                var result = new FakeModels { Router = true };
                string? section = null;
                foreach (var raw in File.Exists(preset) ? File.ReadAllLines(preset) : [])
                {
                    var line = raw.Trim();
                    if (line.StartsWith('[') && line.EndsWith(']'))
                    {
                        section = line[1..^1];
                        result._models.Add((section, ""));
                        continue;
                    }

                    var equals = line.IndexOf('=', StringComparison.Ordinal);
                    if (section is null || equals < 0) continue;
                    var (name, value) = (line[..equals].Trim(), line[(equals + 1)..].Trim());
                    if (name == "model") result._models[^1] = (section, value);
                    if (name == "load-on-startup" && value == "true") result._loaded.Add(section);
                }

                return result;
            }

            var one = new FakeModels { Router = false };
            if (options.Value("model") is { } model)
            {
                var alias = options.Value("alias") ?? model;
                one._models.Add((alias, model));
                one._loaded.Add(alias);
            }

            return one;
        }

        public IReadOnlyList<(string Name, string Path, bool Loaded)> List()
        {
            lock (_gate) return _models.Select(model => (model.Name, model.Path, _loaded.Contains(model.Name))).ToList();
        }

        public bool Knows(string? name)
        {
            lock (_gate) return name is not null && _models.Any(model => model.Name == name);
        }

        public bool IsLoaded(string? name)
        {
            lock (_gate) return name is null ? _loaded.Count > 0 : _loaded.Contains(name);
        }

        public bool SetLoaded(string? name, bool loaded)
        {
            lock (_gate)
            {
                if (name is null || !_models.Any(model => model.Name == name)) return false;
                if (loaded) _loaded.Add(name);
                else _loaded.Remove(name);
                return true;
            }
        }
    }

    /// <summary>The answers of the server, as llama-server gives them.</summary>
    internal static class FakeEndpoints
    {
        public static async Task HandleAsync(HttpContext context, FakeSettings settings, FakeModels models, string? key, FakeLog log)
        {
            var request = context.Request;
            var path = request.Path.Value ?? "/";
            // A process of a model, as a router starts it: no key (the router passes none), and no line in the log for a request.
            var process = models.Router && models.ProcessPort > 0 && context.Connection.LocalPort == models.ProcessPort;
            // At its usual verbosity, the router writes a line for each request it passes on to the process of a model, and
            // none for its own requests (/health, /models).
            var model = request.Query["model"].FirstOrDefault();
            if (models.Router && !process && model is not null) log.Write($"srv  proxy_reques: proxying request to model {model} on port {models.ProcessPort.ToString(CultureInfo.InvariantCulture)}");
            if (path == "/health")
            {
                await WriteAsync(context, 200, "{\"status\":\"ok\"}");
                return;
            }

            if (key is not null && !process && request.Headers.Authorization.ToString() != "Bearer " + key)
            {
                await WriteAsync(context, 401, "{\"error\":{\"code\":401,\"message\":\"Invalid API Key\",\"type\":\"authentication_error\"}}");
                return;
            }

            switch (request.Method, path)
            {
                case ("GET", "/v1/models"):
                    await WriteAsync(context, 200, new JsonObject
                    {
                        ["object"] = "list",
                        ["data"] = new JsonArray([.. models.List().Select(static entry => (JsonNode)new JsonObject { ["id"] = entry.Name, ["object"] = "model", ["owned_by"] = "llamacpp" })]),
                    }.ToJsonString());
                    return;
                case ("GET", "/models"):
                    await WriteAsync(context, 200, new JsonObject
                    {
                        ["data"] = new JsonArray([.. models.List().Select(entry => (JsonNode)new JsonObject
                        {
                            ["id"] = entry.Name,
                            ["object"] = "model",
                            ["status"] = new JsonObject
                            {
                                ["value"] = entry.Loaded ? "loaded" : "unloaded",
                                ["args"] = new JsonArray("llama-server", "--model", entry.Path, "--port", (entry.Loaded ? models.ProcessPort : 0).ToString(CultureInfo.InvariantCulture)),
                            },
                        })]),
                    }.ToJsonString());
                    return;
                case ("POST", "/models/load" or "/models/unload"):
                {
                    var name = await ModelOfAsync(request);
                    if (!models.SetLoaded(name, path == "/models/load"))
                    {
                        await WriteAsync(context, 400, "{\"error\":{\"code\":400,\"message\":\"model not found\",\"type\":\"invalid_request_error\"}}");
                        return;
                    }

                    log.Write($"srv  {(path == "/models/load" ? "load" : "unload")}: {name}");
                    await WriteAsync(context, 200, "{\"success\":true}");
                    return;
                }
                case ("GET", "/slots"):
                    await WriteAsync(context, 200, "[{\"id\":0,\"n_ctx\":4096,\"is_processing\":false},{\"id\":1,\"n_ctx\":4096,\"is_processing\":false}]");
                    return;
                case ("GET", "/metrics"):
                    if (models.Router && !process && !models.IsLoaded(model))
                    {
                        await WriteAsync(context, 400, "{\"error\":{\"code\":400,\"message\":\"model is not loaded\",\"type\":\"invalid_request_error\"}}");
                        return;
                    }

                    await WriteAsync(context, 200, "# HELP llamacpp:predicted_tokens_seconds Average generation throughput in tokens/s.\n"
                        + "# TYPE llamacpp:predicted_tokens_seconds gauge\n"
                        + string.Create(CultureInfo.InvariantCulture, $"llamacpp:predicted_tokens_seconds {settings.TokensPerSecond}\n"), "text/plain; version=0.0.4");
                    return;
                case ("POST", "/v1/chat/completions"):
                    await ChatAsync(context, settings, models, log);
                    return;
                default:
                    await WriteAsync(context, 404, "{\"error\":{\"code\":404,\"message\":\"File Not Found\",\"type\":\"not_found_error\"}}");
                    return;
            }
        }

        private static async Task ChatAsync(HttpContext context, FakeSettings settings, FakeModels models, FakeLog log)
        {
            var name = await ModelOfAsync(context.Request);
            if (models.Router)
            {
                if (!models.Knows(name))
                {
                    await WriteAsync(context, 400, "{\"error\":{\"code\":400,\"message\":\"model not found\",\"type\":\"invalid_request_error\"}}");
                    return;
                }

                // A router loads the model a request names, and passes the request on to its process.
                models.SetLoaded(name, true);
                log.Write($"srv  proxy_reques: proxying request to model {name} on port {models.ProcessPort.ToString(CultureInfo.InvariantCulture)}");
            }

            if (settings.Chat == "error")
            {
                log.Write("srv  operator(): error: the fake failed this request");
                await WriteAsync(context, 500, "{\"error\":{\"code\":500,\"message\":\"the fake failed this request\",\"type\":\"server_error\"}}");
                return;
            }

            var tokens = settings.Chat == "empty" ? 0 : 8;
            await WriteAsync(context, 200, new JsonObject
            {
                ["object"] = "chat.completion",
                ["model"] = name,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["finish_reason"] = "length",
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = tokens == 0 ? "" : "ready" },
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 12, ["completion_tokens"] = tokens, ["total_tokens"] = 12 + tokens },
                ["timings"] = new JsonObject { ["predicted_n"] = tokens, ["predicted_per_second"] = settings.TokensPerSecond },
            }.ToJsonString());
        }

        private static async Task<string?> ModelOfAsync(HttpRequest request)
        {
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body);
                return document.RootElement.TryGetProperty("model", out var model) ? model.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static async Task WriteAsync(HttpContext context, int status, string body, string contentType = "application/json; charset=utf-8")
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            await context.Response.WriteAsync(body);
        }
    }
}
