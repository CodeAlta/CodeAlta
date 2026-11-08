using System.Net;
using System.Text;

namespace CodeAlta.Hosting;

/// <summary>The transport of a provider whose model server this host does not know: each request answers why.</summary>
internal sealed class UnavailableModelServerHandler(string server) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            RequestMessage = request,
            Content = new StringContent(
                "{\"error\":{\"code\":503,\"message\":\"The model server " + server.Replace("\"", "", StringComparison.Ordinal)
                + " is not known here: CodeAlta Desktop or CodeAlta Node runs model servers, and reaches those of its paired machines.\"}}",
                Encoding.UTF8, "application/json"),
        });
}
