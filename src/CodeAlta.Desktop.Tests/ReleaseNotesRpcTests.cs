using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Hosting;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ReleaseNotesRpcTests
{
    [TestMethod]
    public async Task GeneratedContract_CarriesReleaseNotesThroughTheBridge()
    {
        Type[] wireTypes = [typeof(ReleaseNotesListRequest), typeof(ReleaseNotesListResponse),
            typeof(ReleaseNotesReadRequest), typeof(ReleaseNotesReadResponse),
            typeof(ReleaseNotesPictureRequest), typeof(ReleaseNotesPictureResponse)];
        foreach (var type in wireTypes)
            Assert.IsNotNull(DesktopJsonContext.Default.GetTypeInfo(type), type.Name);

        using var feed = new ReleaseFeed();
        using var notes = new CodeAltaReleaseNotes("1.2.3", feed);
        var builder = new NeoRpcBuilder(new NeoRpcOptions { ContractHash = string.Empty, Release = true });
        builder.AddReleaseNotesService(new ReleaseNotesService(new DesktopReleaseNotes(notes, "1.2.3", settings: null)));
        await using var host = builder.Build();
        var answers = Channel.CreateUnbounded<string>();
        await using var session = host.OpenSession(new NeoRpcSessionIdentity("window", "page"), (json, _) =>
        {
            answers.Writer.TryWrite(json);
            return ValueTask.CompletedTask;
        });

        using var list = await InvokeAsync("list", """{"refresh":true}""");
        var listed = list.RootElement.GetProperty("value");
        Assert.AreEqual("ok", listed.GetProperty("status").GetString());
        Assert.AreEqual("1.2.3", listed.GetProperty("currentVersion").GetString());
        Assert.AreEqual("A release", listed.GetProperty("releases")[0].GetProperty("name").GetString());

        using var read = await InvokeAsync("read", """{"version":"1.2.3"}""");
        var release = read.RootElement.GetProperty("value");
        Assert.AreEqual("ok", release.GetProperty("status").GetString());
        Assert.AreEqual("1.2.3", release.GetProperty("release").GetProperty("version").GetString());
        Assert.AreEqual("## Notes", release.GetProperty("body").GetString());

        using var picture = await InvokeAsync("picture", """{"address":"not an address"}""");
        Assert.AreEqual("invalid_request", picture.RootElement.GetProperty("value").GetProperty("status").GetString());

        async Task<JsonDocument> InvokeAsync(string method, string args)
        {
            await session.ReceiveAsync($$$"""{"neoastra":1,"kind":"invoke","id":"{{{method}}}","command":"releaseNotes.{{{method}}}","args":{{{args}}}}""");
            return JsonDocument.Parse(await answers.Reader.ReadAsync().AsTask().WithinAsync(TestTime.Seconds(10)));
        }
    }

    private sealed class ReleaseFeed : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    [{"tag_name":"v1.2.3","name":"A release","body":"## Notes","html_url":"https://example.invalid/releases/1.2.3"}]
                    """),
            });
    }
}
