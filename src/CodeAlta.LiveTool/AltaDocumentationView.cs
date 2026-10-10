namespace CodeAlta.LiveTool;

/// <summary>
/// Shows the user guide that ships with the application to the user, in a host that has a view for it. A host
/// without one registers no such service, and <c>alta documentation open</c> is then not part of its commands.
/// </summary>
public interface IAltaDocumentationView
{
    /// <summary>Asks the host to show the user guide, at a page of it.</summary>
    /// <param name="page">
    /// The page to show: its path below the folder of the guide, with forward slashes, as the guide writes it; null
    /// for the page the view shows already, or the first page.
    /// </param>
    /// <param name="anchor">The heading of the page to go to, as the address of the heading names it; null for the top of the page.</param>
    /// <returns>True when a window received the request; false when none is there to show it.</returns>
    bool Show(string? page, string? anchor);
}
