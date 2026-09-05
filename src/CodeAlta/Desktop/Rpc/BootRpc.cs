using System.Text.Json.Serialization;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(CodeAlta.Desktop.Rpc.DesktopJsonContext))]

namespace CodeAlta.Desktop.Rpc;

[NeoRpcService("boot", Version = 1)]
internal sealed class BootService
{
    [NeoRpcMethod("status")]
    public BootStatus Status(BootRequest request) => new("in-development", "CodeAlta", DesktopCommandLine.Version, false);
}

internal sealed record BootRequest;
internal sealed record BootStatus(string State, string ProductName, string Version, bool HostAvailable);

[JsonSerializable(typeof(BootRequest))]
[JsonSerializable(typeof(BootStatus))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class DesktopJsonContext : JsonSerializerContext;
