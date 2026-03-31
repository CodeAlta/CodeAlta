using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace PlaygroundShared;

public sealed class HelloWorldMcpHost : IAsyncDisposable {
  private readonly WebApplication _application;
  private readonly HelloInvocationLog _log;

  private HelloWorldMcpHost(WebApplication application, HelloInvocationLog log, string mcpUrl, string serverName) {
    _application = application;
    _log = log;
    McpUrl = mcpUrl;
    ServerName = serverName;
  }

  public string McpUrl { get; }

  public string ServerName { get; }

  public static async Task<HelloWorldMcpHost> StartAsync(
    string serverName = "local_http",
    string path = "/mcp",
    CancellationToken cancellationToken = default) {
    var port = GetFreeTcpPort();
    var log = new HelloInvocationLog();
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Services.AddSingleton(log);
    builder.Services.AddSingleton<HelloTools>();
    builder.Services
      .AddMcpServer()
      .WithHttpTransport()
      .WithTools<HelloTools>();

    var app = builder.Build();
    app.MapMcp(path);
    await app.StartAsync(cancellationToken).ConfigureAwait(false);

    return new HelloWorldMcpHost(
      app,
      log,
      $"http://127.0.0.1:{port}{path}",
      serverName);
  }

  public bool TryGetInvocations(out string[] entries) => _log.TrySnapshot(out entries);

  public void ClearInvocations() => _log.Clear();

  public ValueTask DisposeAsync() => _application.DisposeAsync();

  private static int GetFreeTcpPort() {
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
  }
}

internal sealed class HelloInvocationLog {
  private readonly ConcurrentQueue<string> _entries = new();

  public void Add(string entry) => _entries.Enqueue(entry);

  public void Clear() {
    while (_entries.TryDequeue(out _)) { }
  }

  public bool TrySnapshot(out string[] entries) {
    entries = _entries.ToArray();
    return entries.Length > 0;
  }
}

[McpServerToolType]
file sealed class HelloTools {
  private readonly HelloInvocationLog _log;

  public HelloTools(HelloInvocationLog log) {
    _log = log;
  }

  [McpServerTool(Name = "codealta.hello_world"), Description("Returns a hello message for the provided name.")]
  public string HelloWorld(string name = "world") {
    var message = $"Hello, {name}! This response came from the local MCP server.";
    var logEntry = $"{DateTimeOffset.UtcNow:O} codealta.hello_world(name={name}) -> {message}";
    _log.Add(logEntry);
    Console.WriteLine($"[mcp tool invoked] {logEntry}");
    return message;
  }
}