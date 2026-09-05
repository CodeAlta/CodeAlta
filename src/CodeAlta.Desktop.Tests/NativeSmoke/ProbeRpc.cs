using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(CodeAlta.Desktop.Probe.ProbeJsonContext))]

namespace CodeAlta.Desktop.Probe;

[NeoRpcService("probe", Version = 1)]
internal sealed class ProbeService
{
    private int _started;
    private int _disposed;

    [NeoRpcMethod("hello")]
    public HelloResponse Hello(HelloRequest request) => new($"Hello, {request.Name} (C#)");

    [NeoRpcMethod("observe")]
    public NeoRpcChannel<ProbeItem> Observe(EmptyRequest request, CancellationToken cancellationToken) =>
        new(EnumerateAsync(cancellationToken), ProbeJsonContext.Default.ProbeItem);

    [NeoRpcMethod("state")]
    public ProbeState State(EmptyRequest request) => new(Volatile.Read(ref _started), Volatile.Read(ref _disposed));

    private async IAsyncEnumerable<ProbeItem> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _started);
        try
        {
            for (var index = 0; ; index++)
            {
                await Task.Delay(30, cancellationToken).ConfigureAwait(false);
                yield return new ProbeItem(index);
            }
        }
        finally
        {
            Interlocked.Increment(ref _disposed);
        }
    }
}

internal sealed record EmptyRequest;
internal sealed record HelloRequest(string Name);
internal sealed record HelloResponse(string Message);
internal sealed record ProbeItem(int Index);
internal sealed record ProbeState(int Started, int Disposed);

[JsonSerializable(typeof(EmptyRequest))]
[JsonSerializable(typeof(HelloRequest))]
[JsonSerializable(typeof(HelloResponse))]
[JsonSerializable(typeof(ProbeItem))]
[JsonSerializable(typeof(ProbeState))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ProbeJsonContext : JsonSerializerContext;
