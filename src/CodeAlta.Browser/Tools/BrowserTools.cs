using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CodeAlta.Browser.Drivers;
using CodeAlta.Browser.Performance;

namespace CodeAlta.Browser.Tools;

/// <summary>
/// The tools of a browser session. Their names, arguments and result texts follow the tools of Chrome DevTools MCP, so
/// that a caller that knows those tools uses these unchanged; the ones that need the DevTools Protocol are offered for
/// Chromium browsers only.
/// </summary>
internal static class BrowserTools
{
    private const int InlineImageLimit = 2_000_000;
    private const int DefaultPageSize = 20;
    private const int BodyLimit = 10_000;
    private const string LastPageMessage = "The last open page cannot be closed. It is fine to keep it open.";

    private static readonly string[] ConsoleTypes =
    [
        "log", "debug", "info", "error", "warn", "dir", "dirxml", "table", "trace", "clear", "startGroup", "startGroupCollapsed",
        "endGroup", "assert", "profile", "profileEnd", "count", "timeEnd", "verbose", "issue",
    ];

    private static readonly string[] ResourceTypes =
    [
        "document", "stylesheet", "image", "media", "font", "script", "texttrack", "xhr", "fetch", "prefetch", "eventsource",
        "websocket", "manifest", "signedexchange", "ping", "cspviolationreport", "preflight", "fedcm", "other",
    ];

    private static readonly string[] NetworkPresets = ["Offline", "Slow 3G", "Fast 3G", "Slow 4G", "Fast 4G"];

    /// <summary>Describes the tools, sorted by name.</summary>
    public static IReadOnlyList<BrowserToolDefinition> Create(bool allowScriptEvaluation)
    {
        const string uid = "The uid of an element on the page from the page content snapshot";
        const string includeSnapshot = "Whether to include a snapshot in the response. Default is false.";
        const string dblClick = "Set to true for double clicks. Default is false.";
        const string timeout = "Maximum wait time in milliseconds. If set to 0, the default timeout will be used.";

        var tools = new List<BrowserToolDefinition>
        {
            Tool("click", "input", "Clicks on the provided element", false,
                BrowserToolSchema.ForPage().String("uid", uid, required: true).Boolean("dblClick", dblClick).Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var state = await PageAsync(call);
                    var twice = call.OptionalBoolean("dblClick") ?? false;
                    var element = call.RequiredString("uid");
                    var outcome = await call.Session.ActAsync(state, async () =>
                    {
                        var prepared = await BrowserSession.CallOnElementAsync(state, element, "prepareClick", twice ? ",\"dblClick\":true" : string.Empty, call.CancellationToken);
                        if (prepared.TryGetProperty("done", out _)) return null;
                        var (x, y, obscured) = await BrowserSession.PointAsync(state, element, prepared, call.CancellationToken);
                        await state.Page.ClickAsync(x, y, twice ? 2 : 1, call.CancellationToken);
                        return Obscured(obscured);
                    }, call.CancellationToken);
                    AppendAction(call, outcome,
                        twice ? "Successfully double clicked on the element" : "Successfully clicked on the element",
                        twice ? "The element was double clicked and it opened a dialog." : "The element was clicked and it opened a dialog.");
                    await MaybeSnapshotAsync(call, state);
                }),

            Tool("click_at", "input", "Clicks at the provided coordinates", false,
                BrowserToolSchema.ForPage().Number("x", "The x coordinate", required: true).Number("y", "The y coordinate", required: true).Boolean("dblClick", dblClick).Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var state = await PageAsync(call);
                    var twice = call.OptionalBoolean("dblClick") ?? false;
                    var x = call.RequiredNumber("x");
                    var y = call.RequiredNumber("y");
                    var outcome = await call.Session.ActAsync(state, async () =>
                    {
                        await state.Page.ClickAsync(x, y, twice ? 2 : 1, call.CancellationToken);
                        return null;
                    }, call.CancellationToken);
                    AppendAction(call, outcome, twice ? "Successfully double clicked at the coordinates" : "Successfully clicked at the coordinates", "The click opened a dialog.");
                    await MaybeSnapshotAsync(call, state);
                }),

            Tool("drag", "input", "Drag an element onto another element", false,
                BrowserToolSchema.ForPage().String("from_uid", "The uid of the element to drag", required: true).String("to_uid", "The uid of the element to drop into", required: true).Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var state = await PageAsync(call);
                    var from = call.RequiredString("from_uid");
                    var to = call.RequiredString("to_uid");
                    var outcome = await call.Session.ActAsync(state, async () =>
                    {
                        var start = await BrowserSession.PointAsync(state, from, await BrowserSession.CallOnElementAsync(state, from, "point", string.Empty, call.CancellationToken), call.CancellationToken);
                        var end = await BrowserSession.PointAsync(state, to, await BrowserSession.CallOnElementAsync(state, to, "point", string.Empty, call.CancellationToken), call.CancellationToken);
                        await state.Page.DragAsync(start.X, start.Y, end.X, end.Y, call.CancellationToken);
                        return null;
                    }, call.CancellationToken);
                    AppendAction(call, outcome, "Successfully dragged an element", "The drag opened a dialog.");
                    await MaybeSnapshotAsync(call, state);
                }),

            Tool("fill", "input", "Type text into an input, text area or select an option from a <select> element.", false,
                BrowserToolSchema.ForPage().String("uid", uid, required: true)
                    .String("value", "The value to fill in. \"true\" or \"false\" for checkboxes and toggles, \"true\" for radio buttons.", required: true)
                    .Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var state = await PageAsync(call);
                    var element = call.RequiredString("uid");
                    var value = call.RequiredString("value");
                    var outcome = await call.Session.ActAsync(state, async () =>
                    {
                        await FillAsync(state, element, value, call.CancellationToken);
                        return null;
                    }, call.CancellationToken);
                    AppendAction(call, outcome, "Successfully filled out the element", "The element was filled out and it opened a dialog.");
                    await MaybeSnapshotAsync(call, state);
                }),

            Tool("fill_form", "input",
                "Fill out multiple form elements (inputs, selects, checkboxes, radios) at once. ALWAYS prefer this tool over multiple individual 'fill' or 'click' calls when interacting with forms. " +
                "It is significantly faster, more reliable, and reduces turn count. Example: Fill username, password, and check \"Remember Me\" in one call.", false,
                BrowserToolSchema.ForPage().Array("elements", "Elements from snapshot to fill out.", required: true, items: writer =>
                {
                    writer.WriteString("type", "object");
                    writer.WriteString("description", "An element to fill out");
                    writer.WriteStartObject("properties");
                    BrowserToolSchema.WriteProperty(writer, "uid", "string", "The uid of the element to fill out");
                    BrowserToolSchema.WriteProperty(writer, "value", "string", "Value for the element. \"true\" or \"false\" for checkboxes and toggles, \"true\" for radio buttons.");
                    writer.WriteEndObject();
                    writer.WriteStartArray("required");
                    writer.WriteStringValue("uid");
                    writer.WriteStringValue("value");
                    writer.WriteEndArray();
                    writer.WriteBoolean("additionalProperties", false);
                }).Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var state = await PageAsync(call);
                    var elements = new List<(string Uid, string Value)>();
                    foreach (var element in call.RequiredArray("elements").EnumerateArray())
                    {
                        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("uid", out var elementUid) || elementUid.ValueKind != JsonValueKind.String
                            || !element.TryGetProperty("value", out var elementValue) || elementValue.ValueKind != JsonValueKind.String)
                        {
                            throw BrowserToolCall.Invalid("elements", "an array of objects with a string \"uid\" and a string \"value\"");
                        }

                        elements.Add((elementUid.GetString()!, elementValue.GetString()!));
                    }

                    if (elements.Count == 0) throw BrowserToolCall.Invalid("elements", "a non-empty array");
                    var outcome = await call.Session.ActAsync(state, async () =>
                    {
                        foreach (var (element, value) in elements)
                        {
                            // A dialog that a field opens stops the form there.
                            if (state.Page.Dialog is not null) break;
                            await FillAsync(state, element, value, call.CancellationToken);
                        }

                        return null;
                    }, call.CancellationToken);
                    AppendAction(call, outcome, "Successfully filled out the form", "Filling out the form opened a dialog.");
                    await MaybeSnapshotAsync(call, state);
                }),

            Tool("handle_dialog", "input", "If a browser dialog was opened, use this command to handle it", false,
                BrowserToolSchema.ForPage().Enum("action", "Whether to dismiss or accept the dialog", ["accept", "dismiss"], required: true).String("promptText", "Optional prompt text to enter into the dialog."),
                async call =>
                {
                    var state = await call.Session.ResolvePageAsync(call);
                    var action = call.RequiredString("action");
                    if (action is not ("accept" or "dismiss")) throw BrowserToolCall.Invalid("action", "\"accept\" or \"dismiss\"");
                    await state.Page.HandleDialogAsync(action == "accept", call.OptionalString("promptText"), call.CancellationToken);
                    call.Lines.Add(action == "accept" ? "Successfully accepted the dialog" : "Successfully dismissed the dialog");
                    call.Page = state;
                    call.IncludePages = true;
                }),

            Tool("hover", "input", "Hover over the provided element", false,
                BrowserToolSchema.ForPage().String("uid", uid, required: true).Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var state = await PageAsync(call);
                    var element = call.RequiredString("uid");
                    var outcome = await call.Session.ActAsync(state, async () =>
                    {
                        var (x, y, obscured) = await BrowserSession.PointAsync(state, element, await BrowserSession.CallOnElementAsync(state, element, "point", string.Empty, call.CancellationToken), call.CancellationToken);
                        await state.Page.MouseMoveAsync(x, y, call.CancellationToken);
                        return Obscured(obscured);
                    }, call.CancellationToken);
                    AppendAction(call, outcome, "Successfully hovered over the element", "The element was hovered and it opened a dialog.");
                    await MaybeSnapshotAsync(call, state);
                }),

            Tool("press_key", "input",
                "Press a key or key combination. Use this when other input methods like fill() cannot be used (e.g., keyboard shortcuts, navigation keys, or special key combinations).", false,
                BrowserToolSchema.ForPage().String("key", "A key or a combination (e.g., \"Enter\", \"Control+A\", \"Control++\", \"Control+Shift+R\"). Modifiers: Control, Shift, Alt, Meta", required: true)
                    .Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var state = await PageAsync(call);
                    var key = call.RequiredString("key");
                    var (pressed, modifiers) = BrowserKeys.ParseCombination(key);
                    var outcome = await call.Session.ActAsync(state, async () =>
                    {
                        await PressAsync(state.Page, pressed, modifiers, call.CancellationToken);
                        return null;
                    }, call.CancellationToken);
                    AppendAction(call, outcome, $"Successfully pressed key: {key}", $"The key {key} was pressed and it opened a dialog.");
                    await MaybeSnapshotAsync(call, state);
                }),

            Tool("type_text", "input", "Type text using keyboard into a previously focused input", false,
                BrowserToolSchema.ForPage().String("text", "The text to type", required: true).String("submitKey", "Optional key to press after typing. E.g., \"Enter\", \"Tab\", \"Escape\""),
                async call =>
                {
                    var state = await PageAsync(call);
                    var text = call.RequiredString("text");
                    var submitKey = call.OptionalString("submitKey");
                    var submit = string.IsNullOrEmpty(submitKey) ? ((BrowserKey Key, IReadOnlyList<BrowserKey> Modifiers)?)null : BrowserKeys.ParseCombination(submitKey);
                    var outcome = await call.Session.ActAsync(state, async () =>
                    {
                        await TypeAsync(state.Page, text, call.CancellationToken);
                        if (submit is { } press) await PressAsync(state.Page, press.Key, press.Modifiers, call.CancellationToken);
                        return null;
                    }, call.CancellationToken);
                    var typed = $"Typed text \"{text}{(string.IsNullOrEmpty(submitKey) ? string.Empty : $" + {submitKey}")}\"";
                    AppendAction(call, outcome, typed, typed + " and it opened a dialog.");
                }),

            Tool("upload_file", "input", "Upload a file through a provided element.", false,
                BrowserToolSchema.ForPage().String("uid", "The uid of the file input element or an element that holds one on the page from the page content snapshot", required: true)
                    .Array("filePaths", "One or more files paths to upload. File paths are absolute or relative to the folder of the session.", required: true, items: writer => writer.WriteString("type", "string"), minItems: 1)
                    .Boolean("includeSnapshot", includeSnapshot),
                async call =>
                {
                    var state = await PageAsync(call);
                    var paths = call.OptionalStrings("filePaths") ?? throw BrowserToolCall.Missing("filePaths");
                    if (paths.Count == 0) throw BrowserToolCall.Invalid("filePaths", "a non-empty array");
                    var files = await call.Session.Options.Files(call.CancellationToken);
                    var resolved = paths.Select(path => files.ResolveRead(path, "filePaths")).ToArray();
                    foreach (var path in resolved)
                    {
                        if (!File.Exists(path)) throw new BrowserToolException("no-file", $"The file {path} does not exist.");
                    }

                    var element = call.RequiredString("uid");
                    var outcome = await call.Session.ActAsync(state, async () =>
                    {
                        await state.Page.SetFilesAsync(BrowserSession.FrameOf(state, element), element, resolved, call.CancellationToken);
                        return null;
                    }, call.CancellationToken);
                    AppendAction(call, outcome, $"File uploaded from {string.Join(", ", paths)}.", "The upload opened a dialog.");
                    await MaybeSnapshotAsync(call, state);
                }),

            Tool("close_page", "navigation", "Closes the page by its index. The last open page cannot be closed.", false,
                new BrowserToolSchema().Number("pageId", "The ID of the page to close. Call list_pages to list pages.", required: true),
                async call =>
                {
                    await call.Session.ContextAsync(call.CancellationToken);
                    var page = call.Session.PageById(call.OptionalInteger("pageId") ?? throw BrowserToolCall.Missing("pageId"));
                    if (!await call.Session.ClosePageAsync(page, call.CancellationToken)) call.Lines.Add(LastPageMessage);
                    call.IncludePages = true;
                }),

            Tool("list_pages", "navigation", "Get a list of pages open in the browser.", true, new BrowserToolSchema(),
                async call =>
                {
                    await call.Session.ContextAsync(call.CancellationToken);
                    call.IncludePages = true;
                }),

            Tool("navigate_page", "navigation", "Go to a URL, or back, forward, or reload. Use project URL if not specified otherwise.", false,
                NavigateSchema(allowScriptEvaluation, timeout),
                async call =>
                {
                    var state = await PageAsync(call);
                    var type = call.OptionalString("type");
                    var url = call.OptionalString("url");
                    if (type is null && url is null) throw new BrowserToolException("invalid-arguments", "Either URL or a type is required.");
                    type ??= "url";
                    if (type is not ("url" or "back" or "forward" or "reload")) throw BrowserToolCall.Invalid("type", "\"url\", \"back\", \"forward\", or \"reload\"");
                    var beforeUnload = call.OptionalString("handleBeforeUnload") ?? "accept";
                    if (beforeUnload is not ("accept" or "dismiss")) throw BrowserToolCall.Invalid("handleBeforeUnload", "\"accept\" or \"dismiss\"");
                    var limit = call.Timeout("timeout", Scaled(call.Session.Options.NavigationTimeout, state));
                    Uri? target = null;
                    if (type == "url")
                    {
                        if (url is null) throw new BrowserToolException("invalid-arguments", "A URL is required for navigation of type=url.");
                        target = ParseUrl(url);
                        await call.Session.CheckNavigationAsync(target, call.CancellationToken);
                    }

                    var handledBeforeUnload = false;
                    using var answer = state.Page.AnswerDialogs(dialog =>
                    {
                        if (dialog.Type != "beforeunload") return null;
                        handledBeforeUnload = true;
                        return (beforeUnload == "accept", null);
                    });
                    IAsyncDisposable? initScript = null;
                    if (call.OptionalString("initScript") is { Length: > 0 } script && allowScriptEvaluation) initScript = await state.Page.AddInitScriptAsync(script, call.CancellationToken);
                    try
                    {
                        switch (type)
                        {
                            case "url":
                                await state.Page.NavigateAsync(target!.AbsoluteUri, limit, call.CancellationToken);
                                call.Lines.Add($"Successfully navigated to {url}.");
                                break;
                            case "back":
                                await state.Page.TraverseAsync(-1, limit, call.CancellationToken);
                                call.Lines.Add($"Successfully navigated back to {state.Page.Url}.");
                                break;
                            case "forward":
                                await state.Page.TraverseAsync(1, limit, call.CancellationToken);
                                call.Lines.Add($"Successfully navigated forward to {state.Page.Url}.");
                                break;
                            default:
                                await state.Page.ReloadAsync(call.OptionalBoolean("ignoreCache") ?? false, limit, call.CancellationToken);
                                call.Lines.Add("Successfully reloaded the page.");
                                break;
                        }
                    }
                    catch (BrowserToolException exception) when (exception.Code is "timeout" or "navigation" or "no-history" or "browser")
                    {
                        call.Lines.Add(type switch
                        {
                            "url" => $"Unable to navigate in the selected page: {exception.Message}.",
                            "back" => $"Unable to navigate back in the selected page: {exception.Message}.",
                            "forward" => $"Unable to navigate forward in the selected page: {exception.Message}.",
                            _ => $"Unable to reload the selected page: {exception.Message}.",
                        });
                    }
                    finally
                    {
                        if (initScript is not null) await initScript.DisposeAsync();
                    }

                    if (handledBeforeUnload) call.Lines.Add(beforeUnload == "dismiss" ? "Dismissed a beforeunload dialog." : "Accepted a beforeunload dialog.");
                    state.Snapshot = null;
                    call.Page = state;
                    call.IncludePages = true;
                }),

            Tool("new_page", "navigation", "Open a new tab and load a URL. Use project URL if not specified otherwise.", false,
                new BrowserToolSchema().String("url", "URL to load in a new page.", required: true)
                    .Boolean("background", "Whether to open the page in the background without bringing it to the front. Default is false (foreground).")
                    .Integer("timeout", timeout),
                async call =>
                {
                    var url = ParseUrl(call.RequiredString("url"));
                    var state = await call.Session.NewPageAsync(url, call.OptionalBoolean("background") ?? false, call.Timeout("timeout", call.Session.Options.NavigationTimeout), call.CancellationToken);
                    call.Page = state;
                    call.IncludePages = true;
                }),

            Tool("select_page", "navigation", "Select a page as a context for future tool calls.", true,
                new BrowserToolSchema().Number("pageId", "The ID of the page to select. Call list_pages to get available pages.", required: true)
                    .Boolean("bringToFront", "Whether to focus the page and bring it to the top."),
                async call =>
                {
                    await call.Session.ContextAsync(call.CancellationToken);
                    var page = call.Session.PageById(call.OptionalInteger("pageId") ?? throw BrowserToolCall.Missing("pageId"));
                    call.Session.Select(page);
                    if (call.OptionalBoolean("bringToFront") == true) await page.Page.BringToFrontAsync(call.CancellationToken);
                    call.Page = page;
                    call.IncludePages = true;
                }),

            Tool("wait_for", "navigation", "Wait for the specified text to appear on the selected page.", true,
                BrowserToolSchema.ForPage().Array("text", "Non-empty list of texts. Resolves when any value appears on the page.", required: true, items: writer => writer.WriteString("type", "string"), minItems: 1)
                    .Integer("timeout", timeout),
                async call =>
                {
                    var state = await PageAsync(call);
                    var texts = call.OptionalStrings("text") ?? throw BrowserToolCall.Missing("text");
                    if (texts.Count == 0 || texts.Any(string.IsNullOrEmpty)) throw BrowserToolCall.Invalid("text", "a non-empty array of non-empty texts");
                    var limit = call.Timeout("timeout", Scaled(call.Session.Options.WaitTimeout, state));
                    var options = $$"""{"texts":{{JsonSerializer.Serialize(texts.ToArray(), BrowserJsonContext.Default.StringArray)}}}""";
                    var deadline = DateTime.UtcNow + limit;
                    while (true)
                    {
                        string? found = null;
                        try
                        {
                            var answer = await state.Page.CallAgentAsync(state.Page.MainFrame, "hasText", options, call.CancellationToken);
                            found = answer.GetProperty("text").GetString();
                        }
                        catch (BrowserToolException exception) when (exception.Code is "browser" or "no-agent")
                        {
                            // The page is between two documents.
                        }

                        if (found is not null) break;
                        if (DateTime.UtcNow >= deadline)
                        {
                            throw new BrowserToolException("timeout", $"Timed out after {(int)limit.TotalMilliseconds} ms waiting for one of {Json(texts)} to appear on the page.");
                        }

                        await Task.Delay(100, call.CancellationToken);
                    }

                    call.Lines.Add($"Element matching one of {Json(texts)} found.");
                    call.Page = state;
                    call.Snapshot = await call.Session.TakeSnapshotAsync(state, verbose: false, call.CancellationToken);
                    call.CarriesPageContent = true;
                }),

            Tool("resize_page", "emulation", "Resizes the page's window so that the page has specified dimension", false,
                BrowserToolSchema.ForPage().Number("width", "Page width", required: true).Number("height", "Page height", required: true),
                async call =>
                {
                    var state = await PageAsync(call);
                    var width = call.OptionalInteger("width") ?? throw BrowserToolCall.Missing("width");
                    var height = call.OptionalInteger("height") ?? throw BrowserToolCall.Missing("height");
                    if (width <= 0 || height <= 0) throw new BrowserToolException("invalid-arguments", "The page width and height must be positive.");
                    // A window stops at its screen and at the least size of its frame, which the caller cannot know.
                    if (await state.Page.ResizeAsync(width, height, call.CancellationToken) is { } shown && (shown.Width != width || shown.Height != height))
                    {
                        call.Lines.Add($"The page is {shown.Width}x{shown.Height}, not {width}x{height}: its window did not take the size for it.");
                    }

                    call.Page = state;
                    call.IncludePages = true;
                }),

            Tool("emulate", "emulation",
                "Emulates various features on the target page. Each call replaces all emulation settings (except extraHttpHeaders), so pass all desired settings in a single call.", false,
                BrowserToolSchema.ForPage()
                    .Enum("networkConditions", "Throttle network. Omit to disable throttling. Only Offline is available outside Chromium browsers.", NetworkPresets)
                    .Number("cpuThrottlingRate", "Represents the CPU slowdown factor. Omit or set the rate to 1 to disable throttling. Chromium browsers only.", minimum: 1, maximum: 20)
                    .String("geolocation", "Geolocation (`<latitude>,<longitude>`) to emulate. Latitude between -90 and 90. Longitude between -180 and 180. Omit to clear the geolocation override.")
                    .String("userAgent", "User agent to emulate. Omit or set to empty string to clear the user agent override.")
                    .Enum("colorScheme", "Emulate the dark or the light mode. Omit or set to \"auto\" to reset to the default.", ["dark", "light", "auto"])
                    .Enum("forcedColors", "Emulate the `forced-colors` CSS media feature. Omit or set to \"auto\" to reset to the default.", ["active", "none", "auto"])
                    .Enum("prefersContrast", "Emulate the `prefers-contrast` CSS media feature. Omit or set to \"auto\" to reset to the default.", ["more", "less", "custom", "no-preference", "auto"])
                    .Enum("prefersReducedMotion", "Emulate the `prefers-reduced-motion` CSS media feature. Omit or set to \"auto\" to reset to the default.", ["reduce", "no-preference", "auto"])
                    .String("viewport", "Emulate device viewports '<width>x<height>x<devicePixelRatio>[,mobile][,touch][,landscape]'. 'touch' and 'mobile' to emulate mobile devices. 'landscape' to emulate landscape mode. Omit to clear the viewport override.")
                    .String("extraHttpHeaders", "Extra HTTP headers as a JSON string object, e.g. {\"X-Custom\": \"value\", \"Authorization\": \"Bearer token\"}. Headers are included into every HTTP request originating from the page and persist across navigations until cleared. Pass an empty string to clear all extra headers."),
                async call =>
                {
                    var state = await PageAsync(call);
                    var context = await call.Session.ContextAsync(call.CancellationToken);
                    var network = call.OptionalString("networkConditions");
                    if (network is not null && !NetworkPresets.Contains(network)) throw BrowserToolCall.Invalid("networkConditions", string.Join(", ", NetworkPresets));
                    if (network is not null && network != "Offline" && !context.Capabilities.NetworkThrottling)
                    {
                        throw new BrowserToolException("not-supported", $"{context.Browser.Name} cannot throttle the network: only Chromium browsers can. Offline is available.");
                    }

                    var cpu = call.OptionalNumber("cpuThrottlingRate") ?? 1;
                    if (cpu is < 1 or > 20) throw BrowserToolCall.Invalid("cpuThrottlingRate", "a number from 1 to 20");
                    if (cpu > 1 && !context.Capabilities.CpuThrottling) throw new BrowserToolException("not-supported", $"{context.Browser.Name} cannot slow down the CPU: only Chromium browsers can.");
                    IReadOnlyDictionary<string, string>? headers = state.ExtraHeaders;
                    if (call.OptionalString("extraHttpHeaders") is { } headerText) headers = ParseHeaders(headerText);
                    var settings = new EmulationSettings
                    {
                        NetworkConditions = network,
                        CpuThrottlingRate = cpu,
                        Geolocation = call.OptionalString("geolocation") is { } location ? ParseGeolocation(location) : null,
                        UserAgent = call.OptionalString("userAgent") is { Length: > 0 } agent ? agent : null,
                        ColorScheme = Auto(call.OptionalString("colorScheme")),
                        ForcedColors = Auto(call.OptionalString("forcedColors")),
                        PrefersContrast = Auto(call.OptionalString("prefersContrast")),
                        PrefersReducedMotion = Auto(call.OptionalString("prefersReducedMotion")),
                        Viewport = call.OptionalString("viewport") is { } viewport ? ParseViewport(viewport) : null,
                        ExtraHeaders = headers,
                    };
                    await state.Page.EmulateAsync(settings, call.CancellationToken);
                    state.Emulation = settings;
                    state.ExtraHeaders = headers;
                    call.Lines.Add("Emulation configured successfully");
                    call.Page = state;
                }),

            Tool("get_network_request", "network",
                "Gets a network request by its reqid. Useful for inspecting the request and response headers and bodies that the page itself can see.", true,
                BrowserToolSchema.ForPage().Number("reqid", "The reqid of the network request, from list_network_requests.", required: true)
                    .String("requestFilePath", "The absolute or relative path to a .network-request file to save the request body to. If omitted, the body is returned inline.")
                    .String("responseFilePath", "The absolute or relative path to a .network-response file to save the response body to. If omitted, the body is returned inline."),
                async call =>
                {
                    var state = await call.Session.ResolvePageAsync(call);
                    var id = call.OptionalInteger("reqid") ?? throw BrowserToolCall.Missing("reqid");
                    var request = state.Page.Log.Request(id) ?? throw new BrowserToolException("no-request", $"No network request with reqid {id} was found. Call list_network_requests to list the requests.");
                    call.Sections.Add(await FormatRequestAsync(call, state, request));
                    call.Page = state;
                    call.CarriesPageContent = true;
                }),

            Tool("list_network_requests", "network", "Lists the most recent requests for the target page since the last navigation.", true,
                BrowserToolSchema.ForPage().Integer("pageSize", "Maximum number of requests to return. When omitted, returns all requests.", minimum: 1)
                    .Integer("pageIdx", "Page number to return (0-based). When omitted, returns the first page.", minimum: 0)
                    .Array("resourceTypes", "Filter requests to only return requests of the specified resource types. When omitted or empty, returns all requests.", required: false, items: writer => BrowserToolSchema.WriteEnum(writer, ResourceTypes))
                    .Boolean("includePreservedRequests", "Set to true to return the preserved requests over the last 3 navigations."),
                async call =>
                {
                    var state = await call.Session.ResolvePageAsync(call);
                    var requests = state.Page.Log.Requests(call.OptionalBoolean("includePreservedRequests") ?? false);
                    if (call.OptionalStrings("resourceTypes") is { Count: > 0 } types) requests = [.. requests.Where(request => types.Contains(request.ResourceType))];
                    var section = new StringBuilder("## Network requests\n");
                    if (requests.Count == 0) section.Append("No requests found.");
                    else
                    {
                        var (start, end, info) = Paginate(requests.Count, PositiveInteger(call, "pageSize"), call.OptionalInteger("pageIdx"));
                        foreach (var line in info) section.Append(line).Append('\n');
                        for (var index = start; index < end; index++)
                        {
                            var request = requests[index];
                            var address = request.Url.Length > 1000 ? request.Url[..1000] + "... <truncated>" : request.Url;
                            section.Append("reqid=").Append(request.Id).Append(' ').Append(request.Method).Append(' ').Append(address).Append(" [").Append(request.StatusText).Append("]\n");
                        }
                    }

                    call.Sections.Add(section.ToString().TrimEnd('\n'));
                    call.Page = state;
                }),

            Tool("get_console_message", "debugging", "Gets a console message by its ID. You can get all messages by calling list_console_messages.", true,
                BrowserToolSchema.ForPage().Number("msgid", "The msgid of a console message on the page from the listed console messages", required: true),
                async call =>
                {
                    var state = await call.Session.ResolvePageAsync(call);
                    var id = call.OptionalInteger("msgid") ?? throw BrowserToolCall.Missing("msgid");
                    var message = state.Page.Log.Message(id) ?? throw new BrowserToolException("no-message", $"No console message with msgid {id} was found. Call list_console_messages to list the messages.");
                    call.Sections.Add(FormatMessage(message));
                    call.Page = state;
                    call.CarriesPageContent = true;
                }),

            Tool("list_console_messages", "debugging", "List all console messages for the target page since the last navigation.", true,
                BrowserToolSchema.ForPage().Integer("pageSize", "Maximum number of messages to return. When omitted, returns all messages.", minimum: 1)
                    .Integer("pageIdx", "Page number to return (0-based). When omitted, returns the first page.", minimum: 0)
                    .Array("types", "Filter messages to only return messages of the specified resource types. When omitted or empty, returns all messages.", required: false, items: writer => BrowserToolSchema.WriteEnum(writer, ConsoleTypes))
                    .Boolean("includePreservedMessages", "Set to true to return the preserved messages over the last 3 navigations.")
                    .Boolean("includeStackTraces", "Set to true to include the stack trace for each message when available. Increases the response size."),
                async call =>
                {
                    var state = await call.Session.ResolvePageAsync(call);
                    var messages = state.Page.Log.Messages(call.OptionalBoolean("includePreservedMessages") ?? false);
                    if (call.OptionalStrings("types") is { Count: > 0 } types) messages = [.. messages.Where(message => types.Contains(message.Type))];
                    var section = new StringBuilder("## Console messages\n");
                    if (messages.Count == 0) section.Append("<no console messages found>");
                    else
                    {
                        // Consecutive messages that repeat are shown once with their count, as the console does.
                        var groups = new List<(ConsoleEntry Message, int Count)>();
                        foreach (var message in messages)
                        {
                            if (groups.Count != 0 && groups[^1].Message is var previous && previous.Type == message.Type && previous.Text == message.Text && previous.Arguments.Count == message.Arguments.Count)
                            {
                                groups[^1] = (previous, groups[^1].Count + 1);
                            }
                            else groups.Add((message, 1));
                        }

                        var stacks = call.OptionalBoolean("includeStackTraces") ?? false;
                        var (start, end, info) = Paginate(groups.Count, PositiveInteger(call, "pageSize"), call.OptionalInteger("pageIdx"));
                        foreach (var line in info) section.Append(line).Append('\n');
                        var anyStack = false;
                        for (var index = start; index < end; index++)
                        {
                            var (message, count) = groups[index];
                            section.Append("msgid=").Append(message.Id).Append(" [").Append(message.Type).Append("] ").Append(BrowserSession.Truncate(message.Text, 2000))
                                .Append(" (").Append(message.Arguments.Count).Append(" args)");
                            if (count > 1) section.Append(" [").Append(count).Append(" times]");
                            section.Append('\n');
                            if (stacks && message.StackTrace is { } stack)
                            {
                                anyStack = true;
                                section.Append(stack).Append('\n');
                            }
                        }

                        if (anyStack) section.Append("Note: stack trace line and column numbers use 1-based indexing\n");
                    }

                    call.Sections.Add(section.ToString().TrimEnd('\n'));
                    call.Page = state;
                    call.CarriesPageContent = messages.Count != 0;
                }),

            Tool("take_screenshot", "debugging", "Take a screenshot of the page or element.", false,
                BrowserToolSchema.ForPage().Enum("format", "Type of format to save the screenshot as. Default is \"png\"", ["png", "jpeg", "webp"])
                    .Number("quality", "Compression quality for JPEG and WebP formats (0-100). Higher values mean better quality but larger file sizes. Ignored for PNG format.", minimum: 0, maximum: 100)
                    .String("uid", "The uid of an element on the page from the page content snapshot. If omitted, takes a page screenshot.")
                    .Boolean("fullPage", "If set to true takes a screenshot of the full page instead of the currently visible viewport. Incompatible with uid.")
                    .String("filePath", "The absolute path, or a path relative to the folder of the session, to save the screenshot to instead of attaching it to the response."),
                async call =>
                {
                    var state = await PageAsync(call);
                    var context = await call.Session.ContextAsync(call.CancellationToken);
                    var format = call.OptionalString("format") ?? "png";
                    if (format is not ("png" or "jpeg" or "webp")) throw BrowserToolCall.Invalid("format", "\"png\", \"jpeg\" or \"webp\"");
                    if (format == "webp" && context.Browser.Engine != Discovery.BrowserEngine.Chromium) throw new BrowserToolException("not-supported", $"{context.Browser.Name} does not take WebP screenshots. Use \"png\" or \"jpeg\".");
                    var element = call.OptionalString("uid");
                    var fullPage = call.OptionalBoolean("fullPage") ?? false;
                    if (element is not null && fullPage) throw new BrowserToolException("invalid-arguments", "Providing both \"uid\" and \"fullPage\" is not allowed.");
                    int? quality = format != "png" && call.OptionalNumber("quality") is { } requested ? Math.Clamp((int)Math.Round(requested), 1, 100) : null;
                    var files = await call.Session.Options.Files(call.CancellationToken);
                    var filePath = call.OptionalString("filePath") is { } path ? files.ResolveWrite(path, "filePath") : null;
                    (double, double, double, double)? clip = null;
                    if (element is not null)
                    {
                        var rect = await BrowserSession.CallOnElementAsync(state, element, "rect", string.Empty, call.CancellationToken);
                        var (dx, dy) = await BrowserSession.FrameOffsetAsync(state, BrowserSession.FrameOf(state, element), call.CancellationToken);
                        var main = await state.Page.CallAgentAsync(state.Page.MainFrame, "info", "{}", call.CancellationToken);
                        var viewport = main.GetProperty("viewport");
                        clip = (rect.GetProperty("x").GetDouble() + dx + viewport.GetProperty("scrollX").GetDouble(), rect.GetProperty("y").GetDouble() + dy + viewport.GetProperty("scrollY").GetDouble(),
                            rect.GetProperty("width").GetDouble(), rect.GetProperty("height").GetDouble());
                    }

                    var image = await state.Page.ScreenshotAsync(new ScreenshotRequest { Format = format, Quality = quality, Clip = clip, FullPage = fullPage }, call.CancellationToken);
                    call.Lines.Add(element is not null ? $"Took a screenshot of node with uid \"{element}\"."
                        : fullPage ? "Took a screenshot of the full current page."
                        : "Took a screenshot of the current page's viewport.");
                    if (filePath is not null)
                    {
                        await File.WriteAllBytesAsync(filePath, image, call.CancellationToken);
                        call.Lines.Add($"Saved screenshot to {filePath}.");
                    }
                    else if (image.Length >= InlineImageLimit)
                    {
                        // A large image is handed over as a file, where a caller can read the part it needs.
                        Directory.CreateDirectory(files.TemporaryDirectory);
                        var saved = Path.Combine(files.TemporaryDirectory, $"screenshot-{Guid.NewGuid():N}.{format}");
                        await File.WriteAllBytesAsync(saved, image, call.CancellationToken);
                        call.Lines.Add($"Saved screenshot to {saved}.");
                    }
                    else call.Images.Add(new BrowserImage(image, $"image/{format}"));
                    call.Page = state;
                }),

            Tool("take_snapshot", "debugging",
                "Take a text snapshot of the target page based on the a11y tree. The snapshot lists page elements along with a unique\n" +
                "identifier (uid). Always use the latest snapshot. Prefer taking a snapshot over taking a screenshot.", false,
                BrowserToolSchema.ForPage().Boolean("verbose", "Whether to include all possible information available in the full a11y tree. Default is false.")
                    .String("filePath", "The absolute path, or a path relative to the folder of the session, to save the snapshot to instead of attaching it to the response."),
                async call =>
                {
                    var state = await PageAsync(call);
                    var files = await call.Session.Options.Files(call.CancellationToken);
                    var filePath = call.OptionalString("filePath") is { } path ? files.ResolveWrite(path, "filePath") : null;
                    var snapshot = await call.Session.TakeSnapshotAsync(state, call.OptionalBoolean("verbose") ?? false, call.CancellationToken);
                    if (filePath is null)
                    {
                        call.Snapshot = snapshot;
                        call.CarriesPageContent = true;
                    }
                    else
                    {
                        await File.WriteAllTextAsync(filePath, snapshot.ToString(), call.CancellationToken);
                        call.Lines.Add($"Saved snapshot to {filePath}.");
                    }

                    call.Page = state;
                }),
        };

        if (allowScriptEvaluation)
        {
            tools.Add(Tool("evaluate_script", "debugging",
                "Evaluate JavaScript inside the target page. The source can be provided inline or loaded from a local file. Returns the response as JSON, so returned values have to be JSON-serializable.", false,
                BrowserToolSchema.ForPage().String("function",
                        "JavaScript source to execute in the target page. Provide either this or sourcePath, but not both. The source is interpreted according to format.\n" +
                        "Example without arguments: `() => document.title` or `async () => await fetch(\"example.com\")`.\n" +
                        "Example with arguments: `(el) => el.innerText`\n")
                    .String("sourcePath", "The absolute or relative path to a JavaScript file on the local filesystem. Provide either this or function, but not both.")
                    .Enum("format",
                        "How to interpret the source. \"function\" treats it as a function declaration and supports args. \"script\" evaluates it as classic JavaScript and does not support args. " +
                        "Defaults to \"function\". ECMAScript modules are not supported.", ["function", "script"])
                    .Array("args", "An optional list of arguments to pass to the function.", required: false, items: writer =>
                    {
                        writer.WriteString("type", "string");
                        writer.WriteString("description", uid);
                    })
                    .String("filePath", "The absolute or relative path to a file to save the script output to. If omitted, the output is returned inline.")
                    .String("dialogAction", "Handle dialogs while execution. \"accept\", \"dismiss\", or string for response of window.prompt. Defaults to accept.")
                    .Boolean("waitForStableDom", "Whether to wait for the DOM to settle. Pass false if the script only reads data. Defaults to true."),
                async call =>
                {
                    var state = await PageAsync(call);
                    var inline = call.OptionalString("function");
                    var sourcePath = call.OptionalString("sourcePath");
                    if ((inline is null) == (sourcePath is null)) throw new BrowserToolException("invalid-arguments", "Specify exactly one of function or sourcePath.");
                    var format = call.OptionalString("format") ?? "function";
                    if (format is not ("function" or "script")) throw BrowserToolCall.Invalid("format", "\"function\" or \"script\"");
                    var args = call.OptionalStrings("args") ?? [];
                    if (format == "script" && args.Count > 0) throw new BrowserToolException("invalid-arguments", "args cannot be used when format is \"script\".");
                    var files = await call.Session.Options.Files(call.CancellationToken);
                    var filePath = call.OptionalString("filePath") is { } path ? files.ResolveWrite(path, "filePath") : null;
                    string source;
                    if (inline is not null) source = inline;
                    else
                    {
                        var full = files.ResolveRead(sourcePath!, "sourcePath");
                        try { source = await File.ReadAllTextAsync(full, call.CancellationToken); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            throw new BrowserToolException("no-file", $"Unable to read script source from {sourcePath}: {exception.Message}", exception);
                        }
                    }

                    var arguments = args.Select(argument => (BrowserSession.FrameOf(state, argument), argument)).ToArray();
                    var dialogAction = call.OptionalString("dialogAction") ?? "accept";
                    using var answer = state.Page.AnswerDialogs(dialog => dialogAction switch
                    {
                        "accept" => (true, null),
                        "dismiss" => (false, null),
                        var text => (true, text),
                    });
                    string? output = null;
                    var outcome = await call.Session.ActAsync(state, async () =>
                    {
                        output = await state.Page.EvaluateAsync(source, format == "function", arguments, call.CancellationToken);
                        return null;
                    }, call.CancellationToken, waitForStableDom: call.OptionalBoolean("waitForStableDom") ?? true);
                    var text = output ?? "undefined";
                    if (filePath is not null)
                    {
                        await File.WriteAllTextAsync(filePath, text, call.CancellationToken);
                        call.Lines.Add($"Script ran on page. Output saved to {filePath}.");
                    }
                    else
                    {
                        call.Lines.Add("Script ran on page and returned:");
                        call.Lines.Add("```json");
                        call.Lines.Add(text);
                        call.Lines.Add("```");
                        call.CarriesPageContent = true;
                    }

                    if (outcome.NavigatedTo is { } url) call.Lines.Add($"Page navigated to {url}.");
                    call.Page = state;
                }));
        }

        tools.AddRange(PerformanceTools());
        tools.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return tools;
    }

    private static IEnumerable<BrowserToolDefinition> PerformanceTools()
    {
        yield return Tool("performance_start_trace", "performance",
            "Start a performance trace on the selected webpage. Use to find frontend performance issues, Core Web Vitals (LCP, INP, CLS), and improve page load speed.", false,
            BrowserToolSchema.ForPage().Boolean("reload", "Determines if, once tracing has started, the current selected page should be automatically reloaded. Navigate the page to the right URL using the navigate_page tool BEFORE starting the trace if reload or autoStop is set to true.")
                .Boolean("autoStop", "Determines if the trace recording should be automatically stopped.")
                .String("filePath", "The absolute or relative path to a file to save the raw trace data to. The trace is saved as JSON, or gzipped JSON when the name ends with .gz."),
            async call =>
            {
                var state = await PageAsync(call);
                if (state.Page is not IChromiumPage chromium) throw new BrowserToolException("not-supported", "Performance traces need a Chromium browser.");
                var reload = call.OptionalBoolean("reload") ?? true;
                var autoStop = call.OptionalBoolean("autoStop") ?? true;
                var files = await call.Session.Options.Files(call.CancellationToken);
                var filePath = call.OptionalString("filePath") is { } path ? files.ResolveWrite(path, "filePath") : null;
                var url = state.Page.Url;
                if (reload)
                {
                    // The trace starts on a blank page, so that the load it records is the whole of the page's.
                    await state.Page.NavigateAsync("about:blank", call.Session.Options.NavigationTimeout, call.CancellationToken);
                }

                await chromium.StartTraceAsync(call.CancellationToken);
                if (reload && url != "about:blank") await state.Page.NavigateAsync(url, Scaled(call.Session.Options.NavigationTimeout, state), call.CancellationToken);
                if (!autoStop)
                {
                    call.Lines.Add("The performance trace is being recorded. Use performance_stop_trace to stop it.");
                    call.Page = state;
                    return;
                }

                // What a page does right after its load belongs to it too.
                await Task.Delay(TimeSpan.FromSeconds(2), call.CancellationToken);
                await StopTraceAsync(call, state, chromium, filePath, files);
            }, BrowserToolScope.Chromium);

        yield return Tool("performance_stop_trace", "performance",
            "Stop the active performance trace recording on the selected webpage.", false,
            BrowserToolSchema.ForPage().String("filePath", "The absolute or relative path to a file to save the raw trace data to. The trace is saved as JSON, or gzipped JSON when the name ends with .gz."),
            async call =>
            {
                var state = await PageAsync(call);
                if (state.Page is not IChromiumPage chromium) throw new BrowserToolException("not-supported", "Performance traces need a Chromium browser.");
                var files = await call.Session.Options.Files(call.CancellationToken);
                var filePath = call.OptionalString("filePath") is { } path ? files.ResolveWrite(path, "filePath") : null;
                await StopTraceAsync(call, state, chromium, filePath, files);
            }, BrowserToolScope.Chromium);

        yield return Tool("take_heapsnapshot", "memory",
            "Take a heap snapshot of the JavaScript of the selected page, to look for memory leaks. The file opens in the Memory panel of Chrome DevTools.", false,
            BrowserToolSchema.ForPage().String("filePath", "The absolute or relative path of the .heapsnapshot file to save the snapshot to.", required: true),
            async call =>
            {
                var state = await PageAsync(call);
                if (state.Page is not IChromiumPage chromium) throw new BrowserToolException("not-supported", "Heap snapshots need a Chromium browser.");
                var files = await call.Session.Options.Files(call.CancellationToken);
                var filePath = files.ResolveWrite(call.RequiredString("filePath"), "filePath");
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                await using (var stream = File.Create(filePath)) await chromium.TakeHeapSnapshotAsync(stream, call.CancellationToken);
                call.Lines.Add($"Heap snapshot saved to {filePath} ({new FileInfo(filePath).Length.ToString("N0", CultureInfo.InvariantCulture)} bytes).");
                call.Page = state;
            }, BrowserToolScope.Chromium);
    }

    private static async Task StopTraceAsync(BrowserToolCall call, BrowserPageState state, IChromiumPage chromium, string? filePath, BrowserFiles files)
    {
        Directory.CreateDirectory(files.TemporaryDirectory);
        var target = filePath ?? Path.Combine(files.TemporaryDirectory, $"trace-{DateTime.Now:yyyyMMdd-HHmmss}.json.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var buffer = new MemoryStream();
        await chromium.StopTraceAsync(buffer, call.CancellationToken);
        buffer.Position = 0;
        await using (var file = File.Create(target))
        {
            if (target.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
                await buffer.CopyToAsync(gzip, call.CancellationToken);
            }
            else await buffer.CopyToAsync(file, call.CancellationToken);
        }

        buffer.Position = 0;
        call.Sections.Add(TraceSummary.Summarize(buffer, state.Page.Url).ToText());
        call.Lines.Add($"The performance trace has been stopped and saved to {target}: open it in the Performance panel of Chrome DevTools for the full analysis.");
        call.Page = state;
    }

    // -------------------------------------------------------------------------------------------------------------
    // Shared steps
    // -------------------------------------------------------------------------------------------------------------

    private static BrowserToolDefinition Tool(string name, string category, string description, bool readOnly, BrowserToolSchema schema, Func<BrowserToolCall, Task> handler, BrowserToolScope scope = BrowserToolScope.All)
        => new(name, category, description, readOnly, schema.Build(), scope, handler);

    /// <summary>The page of a call that acts on it: there must be no open dialog.</summary>
    private static async Task<BrowserPageState> PageAsync(BrowserToolCall call)
    {
        var state = await call.Session.ResolvePageAsync(call);
        BrowserSession.EnsureNoDialog(state);
        return state;
    }

    private static TimeSpan Scaled(TimeSpan timeout, BrowserPageState state) => timeout * (state.NetworkMultiplier * state.CpuMultiplier);

    private static string? Obscured(string? by) => by is null ? null : $"Note: the element was covered by {by}, which got the pointer.";

    private static void AppendAction(BrowserToolCall call, BrowserSession.ActionOutcome outcome, string succeeded, string openedDialog)
    {
        if (outcome.OpenedDialog)
        {
            call.Lines.Add(openedDialog);
            return;
        }

        call.Lines.Add(succeeded);
        if (outcome.Note is not null) call.Lines.Add(outcome.Note);
        if (outcome.NavigatedTo is { } url) call.Lines.Add($"Page navigated to {url}.");
    }

    private static async Task MaybeSnapshotAsync(BrowserToolCall call, BrowserPageState state)
    {
        call.Page = state;
        if (call.OptionalBoolean("includeSnapshot") != true || state.Page.Dialog is not null) return;
        call.Snapshot = await call.Session.TakeSnapshotAsync(state, verbose: false, call.CancellationToken);
        call.CarriesPageContent = true;
    }

    private static async Task FillAsync(BrowserPageState state, string uid, string value, CancellationToken cancellationToken)
    {
        var prepared = await BrowserSession.CallOnElementAsync(state, uid, "prepareFill", ",\"value\":" + BrowserSession.JsonText(value), cancellationToken);
        switch (prepared.GetProperty("action").GetString())
        {
            case "click":
                var (x, y, _) = await BrowserSession.PointAsync(state, uid, prepared, cancellationToken);
                await state.Page.ClickAsync(x, y, 1, cancellationToken);
                break;
            case "type":
                // The agent selected what the element holds: typing replaces it, and Delete clears it.
                if (value.Length != 0) await state.Page.InsertTextAsync(value, cancellationToken);
                else if (!(prepared.TryGetProperty("empty", out var empty) && empty.ValueKind == JsonValueKind.True))
                {
                    await PressAsync(state.Page, BrowserKeys.Resolve("Delete"), [], cancellationToken);
                }

                break;
        }
    }

    private static async Task PressAsync(IBrowserPage page, BrowserKey key, IReadOnlyList<BrowserKey> modifiers, CancellationToken cancellationToken)
    {
        var held = 0;
        try
        {
            foreach (var modifier in modifiers)
            {
                held |= modifier.ModifierBit;
                await page.KeyAsync(modifier, down: true, held, cancellationToken);
            }

            await page.KeyAsync(key, down: true, held, cancellationToken);
            await page.KeyAsync(key, down: false, held, cancellationToken);
        }
        finally
        {
            // A modifier is never left held, whatever failed.
            for (var index = modifiers.Count - 1; index >= 0; index--)
            {
                held &= ~modifiers[index].ModifierBit;
                try { await page.KeyAsync(modifiers[index], down: false, held, CancellationToken.None); }
                catch (Exception exception) when (exception is BrowserToolException or Cdp.CdpException or WebDriverBiDi.WebDriverBiDiException) { /* the page is gone */ }
            }
        }
    }

    // Each character of the keyboard is typed with its key, so that the page sees the key events of a user; any other
    // character is entered as an input method enters it.
    private static async Task TypeAsync(IBrowserPage page, string text, CancellationToken cancellationToken)
    {
        var shift = BrowserKeys.Resolve("Shift");
        foreach (var character in text)
        {
            if (BrowserKeys.TryResolveCharacter(character, out var key))
            {
                var modifiers = key.Shift ? shift.ModifierBit : 0;
                if (key.Shift) await page.KeyAsync(shift, down: true, modifiers, cancellationToken);
                await page.KeyAsync(key, down: true, modifiers, cancellationToken);
                await page.KeyAsync(key, down: false, modifiers, cancellationToken);
                if (key.Shift) await page.KeyAsync(shift, down: false, 0, cancellationToken);
            }
            else if (character == '\n')
            {
                await PressAsync(page, BrowserKeys.Resolve("Enter"), [], cancellationToken);
            }
            else await page.InsertTextAsync(character.ToString(), cancellationToken);
        }
    }

    private static BrowserToolSchema NavigateSchema(bool allowScriptEvaluation, string timeout)
    {
        var schema = BrowserToolSchema.ForPage()
            .Enum("type", "Navigate the page by URL, back or forward in history, or reload.", ["url", "back", "forward", "reload"])
            .String("url", "Target URL (only type=url)")
            .Boolean("ignoreCache", "Whether to ignore cache on reload.")
            .Enum("handleBeforeUnload", "Whether to auto accept or beforeunload dialogs triggered by this navigation. Default is accept.", ["accept", "dismiss"]);
        if (allowScriptEvaluation) schema.String("initScript", "A JavaScript script to be executed on each new document before any other scripts for the next navigation.");
        return schema.Integer("timeout", timeout);
    }

    internal static Uri ParseUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed
            : throw new BrowserToolException("invalid-arguments", $"Invalid URL: \"{url}\". URLs must be valid according to the URL standard.");

    private static int? PositiveInteger(BrowserToolCall call, string name)
    {
        if (call.OptionalInteger(name) is not { } value) return null;
        if (value <= 0) throw BrowserToolCall.Invalid(name, "a positive integer");
        return value;
    }

    private static string? Auto(string? value) => value is null or "auto" ? null : value;

    /// <summary>Reads <c>&lt;width&gt;x&lt;height&gt;x&lt;devicePixelRatio&gt;[,mobile][,touch][,landscape]</c>.</summary>
    internal static (int, int, double, bool, bool, bool) ParseViewport(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var size = parts.Length == 0 ? [] : parts[0].Split('x');
        if (size.Length is < 2 or > 3 || !int.TryParse(size[0], CultureInfo.InvariantCulture, out var width) || !int.TryParse(size[1], CultureInfo.InvariantCulture, out var height) || width <= 0 || height <= 0)
        {
            throw BrowserToolCall.Invalid("viewport", "'<width>x<height>x<devicePixelRatio>[,mobile][,touch][,landscape]'");
        }

        var scale = size.Length == 3 && double.TryParse(size[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) && ratio > 0 ? ratio : 1;
        var flags = parts.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (width, height, scale, flags.Contains("mobile"), flags.Contains("touch"), flags.Contains("landscape"));
    }

    /// <summary>Reads <c>&lt;latitude&gt;,&lt;longitude&gt;</c>.</summary>
    internal static (double, double) ParseGeolocation(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
            || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw BrowserToolCall.Invalid("geolocation", "'<latitude>,<longitude>' with a latitude between -90 and 90 and a longitude between -180 and 180");
        }

        return (latitude, longitude);
    }

    private static Dictionary<string, string> ParseHeaders(string text)
    {
        if (text.Length == 0) return [];
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new BrowserToolException("invalid-arguments", "Invalid JSON for headers: Headers must be a JSON object");
            return document.RootElement.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            throw new BrowserToolException("invalid-arguments", $"Invalid JSON for headers: {exception.Message}", exception);
        }
    }

    private static string Json(IReadOnlyList<string> values) => BrowserToolSchema.Readable(writer =>
    {
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    });

    private static (int Start, int End, List<string> Info) Paginate(int total, int? pageSize, int? pageIndex)
    {
        var info = new List<string>();
        if (pageSize is null && pageIndex is null)
        {
            info.Add($"Showing {(total == 0 ? 0 : 1)}-{total} of {total} (Page 1 of 1).");
            return (0, total, info);
        }

        var size = pageSize ?? DefaultPageSize;
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        var current = pageIndex ?? 0;
        if (current < 0 || current >= pages)
        {
            info.Add("Invalid page number provided. Showing first page.");
            current = 0;
        }

        var start = current * size;
        var end = Math.Min(total, start + size);
        info.Add($"Showing {(total == 0 ? 0 : start + 1)}-{end} of {total} (Page {current + 1} of {pages}).");
        if (current < pages - 1) info.Add($"Next page: {current + 1}");
        if (current > 0) info.Add($"Previous page: {current - 1}");
        return (start, end, info);
    }

    private static async Task<string> FormatRequestAsync(BrowserToolCall call, BrowserPageState state, NetworkEntry request)
    {
        var bodies = await state.Page.ReadBodiesAsync(request, call.CancellationToken);
        var files = await call.Session.Options.Files(call.CancellationToken);
        var builder = new StringBuilder();
        builder.Append("## Request ").Append(request.Url).Append('\n');
        builder.Append("Status: ").Append(request.StatusText).Append('\n');
        builder.Append("Resource type: ").Append(request.ResourceType).Append('\n');
        builder.Append("### Request Headers\n");
        foreach (var header in request.RequestHeaders) builder.Append("- ").Append(header.Key).Append(':').Append(header.Value).Append('\n');
        if (bodies.Request is { } requestBody)
        {
            builder.Append("### Request Body\n");
            if (call.OptionalString("requestFilePath") is { } requestPath)
            {
                var path = files.ResolveWrite(requestPath, "requestFilePath");
                await File.WriteAllTextAsync(path, requestBody, call.CancellationToken);
                builder.Append("Saved to ").Append(path).Append(".\n");
            }
            else builder.Append(Body(requestBody)).Append('\n');
        }

        if (request.ResponseHeaders is { } responseHeaders)
        {
            builder.Append("### Response Headers\n");
            foreach (var header in responseHeaders) builder.Append("- ").Append(header.Key).Append(':').Append(header.Value).Append('\n');
        }

        if (bodies.Response is { } responseBody)
        {
            builder.Append("### Response Body\n");
            if (call.OptionalString("responseFilePath") is { } responsePath)
            {
                var path = files.ResolveWrite(responsePath, "responseFilePath");
                await File.WriteAllTextAsync(path, responseBody, call.CancellationToken);
                builder.Append("Saved to ").Append(path).Append(".\n");
            }
            else builder.Append(responseBody.Length == 0 ? "<empty response>" : Body(responseBody)).Append('\n');
        }

        if (request.Failure is { } failure) builder.Append("### Request failed with\n").Append(failure).Append('\n');
        return builder.ToString().TrimEnd('\n');
    }

    private static string Body(string body) => body.Length <= BodyLimit ? body : body[..BodyLimit] + "... <truncated>";

    private static string FormatMessage(ConsoleEntry message)
    {
        var builder = new StringBuilder();
        builder.Append("ID: ").Append(message.Id).Append('\n');
        builder.Append("Message: ").Append(message.Type).Append("> ").Append(message.Text).Append('\n');
        if (message.Url is { Length: > 0 } url)
        {
            builder.Append("Source: ").Append(url);
            if (message.Line > 0) builder.Append(':').Append(message.Line).Append(':').Append(message.Column);
            builder.Append('\n');
        }

        if (message.Arguments.Count != 0)
        {
            builder.Append("### Arguments\n");
            for (var index = 0; index < message.Arguments.Count; index++) builder.Append("Arg #").Append(index).Append(": ").Append(BrowserSession.Truncate(message.Arguments[index], 2000)).Append('\n');
        }

        if (message.StackTrace is { } stack) builder.Append("### Stack trace\n").Append(stack).Append('\n').Append("Note: line and column numbers use 1-based indexing\n");
        return builder.ToString().TrimEnd('\n');
    }
}
