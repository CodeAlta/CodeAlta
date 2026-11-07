using System.Net;
using System.Text;
using System.Text.Json;
using CodeAlta.Hosting;

namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class CodeAltaReleaseNotesTests
{
    private const string Releases = """
        [
          { "tag_name": "4.1.0", "name": "4.1.0 Fleet", "draft": false, "prerelease": false, "published_at": "2026-11-07T10:00:00Z",
            "html_url": "https://github.com/CodeAlta/CodeAlta/releases/tag/4.1.0", "body": "## Fleet\n- A tab" },
          { "tag_name": "v4.2.0-preview.1", "name": "", "draft": false, "prerelease": true, "published_at": "2026-11-08T10:00:00Z",
            "html_url": "https://github.com/CodeAlta/CodeAlta/releases/tag/v4.2.0-preview.1", "body": "Preview" },
          { "tag_name": "4.0.0", "name": "4.0.0", "draft": false, "prerelease": false, "published_at": "2026-10-20T10:00:00Z",
            "html_url": "https://github.com/CodeAlta/CodeAlta/releases/tag/4.0.0", "body": null },
          { "tag_name": "4.3.0", "name": "Draft", "draft": true, "prerelease": false, "published_at": null, "html_url": "x", "body": "secret" }
        ]
        """;

    [TestMethod]
    public void Parse_LeavesDraftsOut_TrimsTheV_AndPutsTheNewestFirst()
    {
        using var document = JsonDocument.Parse(Releases);
        var releases = CodeAltaReleaseNotes.Parse(document.RootElement);
        CollectionAssert.AreEqual(new[] { "4.2.0-preview.1", "4.1.0", "4.0.0" }, releases.Select(static release => release.Version).ToArray());
        Assert.AreEqual("4.2.0-preview.1", releases[0].Name, "a release without a title is named by its version");
        Assert.IsTrue(releases[0].Prerelease);
        Assert.AreEqual("## Fleet\n- A tab", releases[1].Body);
        Assert.AreEqual("", releases[2].Body);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero), releases[1].PublishedAt);
        Assert.ThrowsExactly<JsonException>(() => CodeAltaReleaseNotes.Parse(JsonDocument.Parse("{}").RootElement));
    }

    [TestMethod]
    public void Latest_IsTheNewestStableVersion_AndARunningVersionIsComparedToIt()
    {
        using var document = JsonDocument.Parse(Releases);
        var releases = CodeAltaReleaseNotes.Parse(document.RootElement);
        Assert.AreEqual("4.1.0", CodeAltaReleaseNotes.Latest(releases), "a prerelease is not the latest version");
        Assert.AreEqual(true, CodeAltaReleaseNotes.IsLatest("4.1.0", "4.1.0"));
        Assert.AreEqual(false, CodeAltaReleaseNotes.IsLatest("4.0.0", "4.1.0"));
        Assert.AreEqual(true, CodeAltaReleaseNotes.IsLatest("4.1.1-alpha.0.3", "4.1.0"), "a build after the latest release is not behind it");
        Assert.IsNull(CodeAltaReleaseNotes.IsLatest("development", "4.1.0"));
        Assert.IsTrue(CodeAltaReleaseNotes.SameVersion("v4.1.0", "4.1.0"));
        Assert.IsFalse(CodeAltaReleaseNotes.SameVersion("4.1.0", "4.1.1"));
    }

    [TestMethod]
    public async Task ListAsync_AsksGitHubOnce_ForAWhile_AndWithTheHeadersItRequires()
    {
        var handler = new Answers(HttpStatusCode.OK, Releases);
        var time = new ManualTime();
        using var notes = new CodeAltaReleaseNotes("4.1.0", handler, time);

        var first = await notes.ListAsync(refresh: false, CancellationToken.None);
        Assert.HasCount(3, first);
        Assert.AreEqual("4.1.0", (await notes.FindAsync("v4.1.0", CancellationToken.None))?.Version);
        Assert.IsNull(await notes.FindAsync("3.0.0", CancellationToken.None));
        await notes.ListAsync(refresh: true, CancellationToken.None);
        Assert.AreEqual(1, handler.Requests.Count, "a refresh right after a read asks nothing");

        time.Advance(CodeAltaReleaseNotes.MinimumAge + TimeSpan.FromSeconds(1));
        await notes.ListAsync(refresh: true, CancellationToken.None);
        Assert.AreEqual(2, handler.Requests.Count, "an asked refresh reads again after a minute");
        time.Advance(CodeAltaReleaseNotes.MaximumAge + TimeSpan.FromSeconds(1));
        await notes.ListAsync(refresh: false, CancellationToken.None);
        Assert.AreEqual(3, handler.Requests.Count, "an old list is read again");

        var request = handler.Requests[0];
        Assert.AreEqual("https://api.github.com/repos/CodeAlta/CodeAlta/releases?per_page=100", request.RequestUri!.ToString());
        StringAssert.StartsWith(request.Headers.UserAgent.ToString(), "CodeAlta/4.1.0");
        StringAssert.Contains(request.Headers.Accept.ToString(), "application/vnd.github+json");
    }

    [TestMethod]
    public async Task ListAsync_ThatFails_SaysWhy_AndIsTriedAgainAtTheNextCall()
    {
        var handler = new Answers(HttpStatusCode.Forbidden, "{}") { RateLimited = true };
        using var notes = new CodeAltaReleaseNotes("4.1.0", handler, new ManualTime());
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => notes.ListAsync(refresh: false, CancellationToken.None));
        StringAssert.Contains(error.Message, "limits the requests");
        handler.Status = HttpStatusCode.OK;
        handler.Body = Releases;
        Assert.HasCount(3, await notes.ListAsync(refresh: false, CancellationToken.None));
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ReadPictureAsync_ReadsImagesOfGitHubAndOfTheSite_Only_AndKeepsThem()
    {
        Assert.IsTrue(CodeAltaReleaseNotes.IsPictureAddress("https://raw.githubusercontent.com/CodeAlta/CodeAlta/4.0.0/site/img/a.webp"));
        Assert.IsTrue(CodeAltaReleaseNotes.IsPictureAddress("https://codealta.io/img/a.png"));
        Assert.IsFalse(CodeAltaReleaseNotes.IsPictureAddress("http://codealta.io/img/a.png"), "HTTPS only");
        Assert.IsFalse(CodeAltaReleaseNotes.IsPictureAddress("https://github.com.example.org/a.png"));
        Assert.IsFalse(CodeAltaReleaseNotes.IsPictureAddress("file:///C:/a.png"));

        var handler = new Answers(HttpStatusCode.OK, "PNG") { MediaType = "image/png" };
        using var notes = new CodeAltaReleaseNotes("4.1.0", handler, new ManualTime());
        const string address = "https://raw.githubusercontent.com/CodeAlta/CodeAlta/4.0.0/site/img/a.png";
        var picture = await notes.ReadPictureAsync(address, CancellationToken.None);
        Assert.AreEqual("image/png", picture?.MediaType);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("PNG"), picture!.Data);
        Assert.AreSame(picture, await notes.ReadPictureAsync(address, CancellationToken.None), "a picture read is kept");
        Assert.HasCount(1, handler.Requests);

        handler.MediaType = "text/html";
        Assert.IsNull(await notes.ReadPictureAsync("https://codealta.io/not-a-picture", CancellationToken.None), "what is not an image is not drawn");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => notes.ReadPictureAsync("https://example.com/a.png", CancellationToken.None));
    }

    private sealed class Answers(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public string Body { get; set; } = body;
        public bool RateLimited { get; set; }
        public string MediaType { get; set; } = "application/json";
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, MediaType) };
            if (RateLimited && Status != HttpStatusCode.OK) response.Headers.Add("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
