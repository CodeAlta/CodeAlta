namespace CodeAlta.Browser.Drivers;

/// <summary>A console message of a page, or an error the page did not catch.</summary>
internal sealed record ConsoleEntry(int Id, string Type, string Text, IReadOnlyList<string> Arguments, string? Url, int Line, int Column, string? StackTrace);

/// <summary>A request of a page and, once it came, its response.</summary>
internal sealed class NetworkEntry
{
    /// <summary>Gets the id tools name it by (<c>reqid</c>).</summary>
    public required int Id { get; init; }

    /// <summary>Gets the id of the request in the protocol of the browser.</summary>
    public required string RequestId { get; init; }

    /// <summary>Gets the address.</summary>
    public required string Url { get; init; }

    /// <summary>Gets the method.</summary>
    public required string Method { get; init; }

    /// <summary>Gets or sets the resource type, with the names of Chrome DevTools (<c>document</c>, <c>xhr</c>, <c>fetch</c>…).</summary>
    public string ResourceType { get; set; } = "other";

    /// <summary>Gets the headers of the request.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders { get; init; } = [];

    /// <summary>Gets or sets whether the request has a body.</summary>
    public bool HasPostData { get; set; }

    /// <summary>Gets or sets the body of the request, when the browser gave it with the request.</summary>
    public string? PostData { get; set; }

    /// <summary>Gets or sets the status code, or null while no response came.</summary>
    public int? Status { get; set; }

    /// <summary>Gets or sets the headers of the response, or null while no response came.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? ResponseHeaders { get; set; }

    /// <summary>Gets or sets the media type of the response.</summary>
    public string? MimeType { get; set; }

    /// <summary>Gets or sets whether the response was read whole.</summary>
    public bool Finished { get; set; }

    /// <summary>Gets or sets why the request failed, or null.</summary>
    public string? Failure { get; set; }

    /// <summary>Gets the text of the status in a list: the code, <c>pending</c> or <c>failed - …</c>.</summary>
    public string StatusText => Failure is { } failure ? $"failed - {failure}" : Status is { } status ? status.ToString(System.Globalization.CultureInfo.InvariantCulture) : "pending";
}

/// <summary>The bodies of a request and of its response, as a tool shows them.</summary>
/// <param name="Request">The body of the request, or null.</param>
/// <param name="Response">The body of the response as text, <c>&lt;binary data&gt;</c>, or null when the browser no longer has it.</param>
internal sealed record NetworkBodies(string? Request, string? Response);

/// <summary>
/// The console messages and requests of a page, by navigation: the last <see cref="KeptNavigations"/> are kept, as
/// Chrome DevTools MCP keeps them, each with ids that never repeat on the page.
/// </summary>
/// <remarks>Written from the event loop of the driver and read by tools: every member takes the lock.</remarks>
internal sealed class PageLog
{
    /// <summary>How many navigations are kept.</summary>
    public const int KeptNavigations = 3;

    private const int MaximumEntries = 2000;

    private readonly Lock _gate = new();
    private readonly LinkedList<(List<ConsoleEntry> Console, List<NetworkEntry> Network)> _navigations = new();
    private readonly Dictionary<string, NetworkEntry> _requests = new(StringComparer.Ordinal);
    private int _nextMessage = 1;
    private int _nextRequest = 1;

    public PageLog() => _navigations.AddLast(([], []));

    /// <summary>A new document started: what follows belongs to it.</summary>
    public void StartNavigation()
    {
        lock (_gate) StartNavigationLocked();
    }

    /// <summary>Adds a console message.</summary>
    public ConsoleEntry AddConsole(string type, string text, IReadOnlyList<string> arguments, string? url, int line, int column, string? stackTrace)
    {
        lock (_gate)
        {
            var entry = new ConsoleEntry(_nextMessage++, type, text, arguments, url, line, column, stackTrace);
            var current = _navigations.Last!.Value.Console;
            if (current.Count < MaximumEntries) current.Add(entry);
            return entry;
        }
    }

    /// <summary>Adds a request. A redirect keeps the id of the protocol and is a request of its own, which the id then names.</summary>
    public NetworkEntry AddRequest(string requestId, string url, string method, string resourceType, IReadOnlyList<KeyValuePair<string, string>> headers, bool hasPostData, string? postData)
    {
        lock (_gate)
        {
            var entry = new NetworkEntry
            {
                Id = _nextRequest++, RequestId = requestId, Url = url, Method = method, ResourceType = resourceType, RequestHeaders = headers,
                HasPostData = hasPostData, PostData = postData,
            };
            var current = _navigations.Last!.Value.Network;
            if (current.Count < MaximumEntries) current.Add(entry);
            _requests[requestId] = entry;
            return entry;
        }
    }

    private void StartNavigationLocked()
    {
        _navigations.AddLast(([], []));
        while (_navigations.Count > KeptNavigations)
        {
            foreach (var request in _navigations.First!.Value.Network) _requests.Remove(request.RequestId);
            _navigations.RemoveFirst();
        }
    }

    /// <summary>Changes the request of an id of the protocol, if it is still kept.</summary>
    public void Update(string requestId, Action<NetworkEntry> change)
    {
        lock (_gate)
        {
            if (_requests.TryGetValue(requestId, out var entry)) change(entry);
        }
    }

    /// <summary>The messages of the current navigation, or of the kept ones.</summary>
    public IReadOnlyList<ConsoleEntry> Messages(bool includePreserved)
    {
        lock (_gate) return includePreserved ? [.. _navigations.SelectMany(static navigation => navigation.Console)] : [.. _navigations.Last!.Value.Console];
    }

    /// <summary>The requests of the current navigation, or of the kept ones.</summary>
    public IReadOnlyList<NetworkEntry> Requests(bool includePreserved)
    {
        lock (_gate) return includePreserved ? [.. _navigations.SelectMany(static navigation => navigation.Network)] : [.. _navigations.Last!.Value.Network];
    }

    /// <summary>A kept message, by its id.</summary>
    public ConsoleEntry? Message(int id)
    {
        lock (_gate) return _navigations.SelectMany(static navigation => navigation.Console).FirstOrDefault(entry => entry.Id == id);
    }

    /// <summary>A kept request, by its id.</summary>
    public NetworkEntry? Request(int id)
    {
        lock (_gate) return _navigations.SelectMany(static navigation => navigation.Network).FirstOrDefault(entry => entry.Id == id);
    }
}
