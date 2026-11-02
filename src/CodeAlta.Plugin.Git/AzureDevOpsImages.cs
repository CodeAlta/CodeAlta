using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Git;

/// <summary>The images of work items and pull requests of Azure DevOps: attachments that only the signed-in user reads.</summary>
internal sealed partial class AzureDevOpsApi
{
    public override async Task<TrackedImage?> ReadImageAsync(GitIssueQuery query, Uri address, CancellationToken cancellationToken)
    {
        if (!IsAttachment(query.Repository, address)) return null;
        // The body is read here, up to the largest image: an attachment of any size is never taken whole in memory.
        using var response = await query.GetUnbufferedAsync(address.AbsoluteUri, "image/*", cancellationToken).ConfigureAwait(false);
        // An answer from elsewhere (a redirection the sign-in did not follow) is not the attachment.
        if (!response.IsSuccessStatusCode || !string.Equals(response.RequestMessage?.RequestUri?.Host, address.Host, StringComparison.OrdinalIgnoreCase)) return null;
        return await ReadImageAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // An attachment of a work item or of a pull request of the organization of the repository, on its host: the
    // credentials of the organization go to no other address.
    internal static bool IsAttachment(GitRepositoryReference repository, Uri address)
    {
        if (address.Scheme != Uri.UriSchemeHttps || !address.IsDefaultPort || address.UserInfo.Length > 0
            || !string.Equals(address.Host, repository.Host, StringComparison.OrdinalIgnoreCase)) return false;
        var segments = address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 5 || !string.Equals(Uri.UnescapeDataString(segments[0]), repository.Owner, StringComparison.OrdinalIgnoreCase)) return false;
        // {org}/_apis/wit/attachments/{id}: the address the service gives an attachment it was sent, without a project.
        if (segments.Length == 5 && Is(segments[1], "_apis") && Is(segments[2], "wit") && Is(segments[3], "attachments")) return true;
        // {org}/{project}/_apis/wit/attachments/{id}
        if (segments.Length == 6 && Is(segments[2], "_apis") && Is(segments[3], "wit") && Is(segments[4], "attachments")) return true;
        // {org}/{project}/_apis/git/repositories/{repository}/pullRequests/{id}/attachments/{file}
        return segments.Length == 10 && Is(segments[2], "_apis") && Is(segments[3], "git") && Is(segments[4], "repositories")
            && Is(segments[6], "pullRequests") && int.TryParse(segments[7], out _) && Is(segments[8], "attachments");

        static bool Is(string segment, string name) => string.Equals(segment, name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>What the trackers of hosting services share to read an image.</summary>
internal abstract partial class GitHostApi
{
    /// <summary>Reads an image an item shows, from an address of the service; none by default.</summary>
    public virtual Task<TrackedImage?> ReadImageAsync(GitIssueQuery query, Uri address, CancellationToken cancellationToken)
        => Task.FromResult<TrackedImage?>(null);

    /// <summary>Reads the body of an answer as an image: at most <see cref="TrackedImage.MaximumBytes"/>, of a type its bytes say.</summary>
    protected static async Task<TrackedImage?> ReadImageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > TrackedImage.MaximumBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > TrackedImage.MaximumBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        return TrackedImage.Detect(bytes) is { } mediaType ? new TrackedImage(mediaType, bytes) : null;
    }
}
