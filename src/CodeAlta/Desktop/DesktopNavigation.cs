using NeoAstra;

namespace CodeAlta.Desktop;

/// <summary>
/// Decides the navigations of the application's view: it loads the document the host shows, and no other.
/// </summary>
/// <remarks>
/// The view keeps a history of the documents the host showed in it (the start-up screen, then the
/// application), and the browser walks that history without the host: the back and forward buttons of a
/// mouse, a swipe, a key, <c>history.back()</c>. A navigation to an earlier document is canceled, so none of
/// them leaves the document that is shown; loading that document again is a reload and stays allowed.
/// </remarks>
internal sealed class DesktopNavigation
{
    private Uri? _document;

    /// <summary>Names the document the host navigates the view to; call it before that navigation.</summary>
    /// <param name="document">The start-up screen or the application's page.</param>
    /// <returns><paramref name="document"/>, for the navigation.</returns>
    /// <exception cref="ArgumentException"><paramref name="document"/> is not a document of the application.</exception>
    internal Uri Show(Uri document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!DesktopApplication.IsApplicationDocument(document))
            throw new ArgumentException($"'{document}' is not a document of the application.", nameof(document));
        Volatile.Write(ref _document, document);
        return document;
    }

    /// <summary>Allows the document the host shows, in the frame that asks, and cancels every other navigation.</summary>
    /// <param name="request">The navigation the view asks for.</param>
    internal NeoNavigationDecision Decide(NeoNavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shown = Volatile.Read(ref _document) is { } document && DesktopApplication.IsApplicationDocument(request.Uri) &&
            string.Equals(request.Uri.AbsolutePath, document.AbsolutePath, StringComparison.Ordinal);
        return shown ? NeoNavigationDecision.Allow : NeoNavigationDecision.Cancel;
    }
}
