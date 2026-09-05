using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CodeAlta.Tui.Views;
using NuGet.Versioning;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaUpdateCheckerTests
{
    [TestMethod]
    public async Task CheckNuGetOrgAsync_ReadsGzippedRegistrationIndex()
    {
        var responseJson = """
            {
              "items": [
                {
                  "items": [
                    { "catalogEntry": { "version": "0.8.0", "listed": true } },
                    { "catalogEntry": { "version": "0.9.0", "listed": true } }
                  ]
                }
              ]
            }
            """;
        using var httpClient = new HttpClient(new StaticResponseHandler(CreateGzipJsonResponse(responseJson)));

        var result = await CodeAltaUpdateChecker.CheckNuGetOrgAsync(
            CodeAltaUpdateChecker.PackageId,
            NuGetVersion.Parse("0.8.0"),
            includePrerelease: false,
            httpClient);

        Assert.IsTrue(result.PackageFound);
        Assert.IsTrue(result.HasNewerVersion);
        Assert.AreEqual("0.9.0", result.LatestVersionText);
    }

    private static HttpResponseMessage CreateGzipJsonResponse(string json)
    {
        using var compressedStream = new MemoryStream();
        using (var gzipStream = new GZipStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            gzipStream.Write(bytes, 0, bytes.Length);
        }

        var content = new ByteArrayContent(compressedStream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
