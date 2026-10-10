using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Documentation;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>The Documentation tab of the window: a real guide in a temporary folder; the host of the question is a list of calls.</summary>
[TestClass]
public sealed class DocumentationRpcTests
{
    private const string Epoch = "6f1d2c3b-4a59-4e68-9b7a-0c1d2e3f4a5b";
    private const string OtherEpoch = "00000000-1111-4222-8333-444444444444";

    [TestMethod]
    public void Menu_ListsTheNavigationInOrder_WithEveryPage()
    {
        using var fixture = Fixture.Create();
        var menu = fixture.Service().Menu(new(Epoch));
        Assert.AreEqual("ok", menu.Status);
        Assert.AreEqual("readme.md", menu.Home);
        Assert.IsFalse(menu.CanAsk, "Nothing asks in a host that has no session to create.");
        CollectionAssert.AreEqual(new[]
        {
            new DocumentationMenuItem("readme.md", "User Guide", "book", 0, null),
            new DocumentationMenuItem("sessions.md", "Sessions", null, 0, null),
            new DocumentationMenuItem("plugins/readme.md", "Plugins", "puzzle", 0, null),
            new DocumentationMenuItem("plugins/git.md", "Git", null, 1, "plugins/readme.md"),
        }, menu.Items.ToArray());
        CollectionAssert.AreEqual(new[] { "orphan.md", "plugins/git.md", "plugins/readme.md", "readme.md", "sessions.md" }, menu.Pages.Select(static page => page.Path).ToArray());
        Assert.IsTrue(fixture.Service(asker: fixture.Asker()).Menu(new(Epoch)).CanAsk);
    }

    [TestMethod]
    public void EveryRequest_NamesTheHostItTalksTo_AndAHostWithoutAGuideAnswersNothing()
    {
        using var fixture = Fixture.Create();
        var service = fixture.Service(asker: fixture.Asker());
        Assert.AreEqual("stale_epoch", service.Menu(new(OtherEpoch)).Status);
        Assert.AreEqual("stale_epoch", service.Page(new(OtherEpoch, "readme.md")).Status);
        Assert.AreEqual("stale_epoch", service.Image(new(OtherEpoch, "alta-desktop-home.webp")).Status);
        Assert.AreEqual("stale_epoch", service.Search(new(OtherEpoch, "guide")).Status);
        Assert.AreEqual("stale_epoch", service.AskAsync(new(OtherEpoch, "What is a session?", null), default).GetAwaiter().GetResult().Status);
        Assert.AreEqual(0, fixture.Created.Count);

        foreach (var unavailable in new[] { new DocumentationService(), new DocumentationService(new ShippedDocumentation(Path.Combine(fixture.Root, "nowhere")), Epoch, asker: fixture.Asker()) })
        {
            Assert.AreEqual("unavailable", unavailable.Menu(new(Epoch)).Status);
            Assert.AreEqual("unavailable", unavailable.Page(new(Epoch, "readme.md")).Status);
            Assert.AreEqual("unavailable", unavailable.Image(new(Epoch, "alta-desktop-home.webp")).Status);
            Assert.AreEqual("unavailable", unavailable.Search(new(Epoch, "guide")).Status);
            Assert.AreEqual("unavailable", unavailable.AskAsync(new(Epoch, "What is a session?", null), default).GetAwaiter().GetResult().Status);
        }

        Assert.AreEqual(0, fixture.Created.Count);
    }

    [TestMethod]
    public void Page_IsItsTextsAndItsPictures_AndOnlyAPageOfTheGuideIsRead()
    {
        using var fixture = Fixture.Create();
        var service = fixture.Service();
        var page = service.Page(new(Epoch, "readme.md"));
        Assert.AreEqual("ok", page.Status);
        Assert.AreEqual("readme.md", page.Path);
        Assert.AreEqual("User Guide", page.Title);
        Assert.AreEqual(3, page.Blocks.Count);
        Assert.AreEqual("markdown", page.Blocks[0].Kind);
        StringAssert.Contains(page.Blocks[0].Markdown, "[Sessions](sessions.md#queue)");
        Assert.AreEqual(new DocumentationBlock("figure", null, "alta-desktop-home.webp", null, "The workspace", "The main workspace."), page.Blocks[1]);
        Assert.AreEqual("figure", page.Blocks[2].Kind);
        Assert.IsNull(page.Blocks[2].Image);
        StringAssert.StartsWith(page.Blocks[2].Svg, "<svg ");
        // The page as the guide writes it, whatever the case of the request.
        Assert.AreEqual("plugins/git.md", service.Page(new(Epoch, "Plugins/Git.md")).Path);

        var outside = Path.Combine(fixture.Root, "outside.md");
        File.WriteAllText(outside, "# Outside\n\nsecret");
        foreach (var path in new string?[] { null, "", "../outside.md", outside, "file:///" + outside.Replace('\\', '/'), "plugins\\git.md", "menu.yml", "img/alta-desktop-home.webp", "https://example.com/readme.md", "nowhere.md" })
        {
            var refused = service.Page(new(Epoch, path));
            Assert.AreEqual("not_found", refused.Status, path);
            Assert.IsNull(refused.Path, path);
            Assert.AreEqual(0, refused.Blocks.Count, path);
        }
    }

    [TestMethod]
    public void Image_IsAPictureOfTheGuide_AsBase64()
    {
        using var fixture = Fixture.Create();
        var service = fixture.Service();
        var image = service.Image(new(Epoch, "alta-desktop-home.webp"));
        Assert.AreEqual(new DocumentationImageResponse("ok", "image/webp", Convert.ToBase64String(new byte[] { 82, 73, 70, 70 })), image);
        File.WriteAllBytes(Path.Combine(fixture.Root, "secret.png"), [1, 2, 3]);
        foreach (var name in new string?[] { null, "", "../secret.png", Path.Combine(fixture.Root, "secret.png"), "img/alta-desktop-home.webp", "readme.md", "nowhere.webp" })
        {
            Assert.AreEqual(new DocumentationImageResponse("not_found", null, null), service.Image(new(Epoch, name)), name);
        }
    }

    [TestMethod]
    public void Search_FindsATextInTheGuide()
    {
        using var fixture = Fixture.Create();
        var service = fixture.Service();
        var found = service.Search(new(Epoch, "prompt queue"));
        Assert.AreEqual("ok", found.Status);
        Assert.AreEqual(new DocumentationSearchHit("sessions.md", "Sessions", "Queue", "A session keeps a prompt queue while it runs."), found.Hits.Single());
        Assert.AreEqual(0, service.Search(new(Epoch, "x")).Hits.Count);
        Assert.AreEqual(0, service.Search(new(Epoch, null)).Hits.Count);
    }

    [TestMethod]
    public async Task Ask_CreatesAChatWithTheDefaultProvider_AndSendsTheQuestionWithWhereTheGuideIs()
    {
        using var fixture = Fixture.Create();
        var asked = 0;
        var service = fixture.Service(asker: fixture.Asker(), asked: () => asked++);

        var reply = await service.AskAsync(new(Epoch, "  How do I queue a prompt?\nWhile a session runs.  ", "Sessions.md"), default);
        Assert.AreEqual(new DocumentationAskResponse("ok", "chat-1", null), reply);
        Assert.AreEqual(1, asked, "The window reads its sessions again.");
        // The default provider of the configuration, whatever the order of the providers; the chat is named after the question.
        Assert.AreEqual(("beta", "How do I queue a prompt?"), fixture.Created.Single());
        var send = fixture.Sent.Single();
        Assert.AreEqual("chat-1", send.SessionId);
        StringAssert.StartsWith(send.ClientRequestId, "documentation:");
        // No model and no effort: the chat starts with what its provider is configured with, and nothing is saved.
        Assert.AreEqual(new OwnedSessionSelection("beta", "default", null, null), send.Selection);
        Assert.IsNull(send.References);
        Assert.IsNull(send.Images);
        StringAssert.StartsWith(send.Text, "How do I queue a prompt?\nWhile a session runs.\n\n---\n");
        StringAssert.Contains(send.Text, "on the page \"Sessions\" (`sessions.md`)");
        StringAssert.Contains(send.Text, $"in `{fixture.GuideRoot}`");
        StringAssert.Contains(send.Text, $"Start with `{Path.Combine(fixture.GuideRoot, "sessions.md")}`");
        StringAssert.Contains(send.Text, "`alta documentation search <text>`");
        StringAssert.Contains(send.Text, "each as a Markdown link to its file");

        // A question about the whole guide names no page.
        reply = await service.AskAsync(new(Epoch, "What is CodeAlta?", null), default);
        Assert.AreEqual("ok", reply.Status);
        Assert.AreEqual("chat-2", reply.SessionId);
        StringAssert.Contains(fixture.Sent[1].Text, "Asked from the Documentation of CodeAlta. Answer from the user guide");
        Assert.IsFalse(fixture.Sent[1].Text.Contains("Start with", StringComparison.Ordinal));
        Assert.AreEqual(2, asked);
    }

    [TestMethod]
    public async Task Ask_TakesTheFirstDefaultProvider_WhenTheConfigurationNamesNoneThatIsEnabled()
    {
        using var fixture = Fixture.Create();
        foreach (var configured in new[] { null, "gone", " " })
        {
            fixture.Created.Clear();
            fixture.Configured = configured;
            Assert.AreEqual("ok", (await fixture.Service(asker: fixture.Asker()).AskAsync(new(Epoch, "Why?", null), default)).Status);
            Assert.AreEqual("gamma", fixture.Created.Single().Provider, configured);
        }

        fixture.Providers = [new ModelProviderDescriptor(new ModelProviderId("only"), "Only")];
        fixture.Created.Clear();
        await fixture.Service(asker: fixture.Asker()).AskAsync(new(Epoch, "Why?", null), default);
        Assert.AreEqual("only", fixture.Created.Single().Provider);
    }

    [TestMethod]
    public async Task Ask_SaysWhyItDidNotAsk_AndLeavesNoChatBehind()
    {
        using var fixture = Fixture.Create();
        var service = fixture.Service(asker: fixture.Asker());

        // What is no question, and what is no page of the guide.
        foreach (var question in new string?[] { null, "", "   \n ", "bell\a", new string('q', DocumentationAsker.MaximumQuestionLength + 1) })
        {
            Assert.AreEqual("invalid_request", (await service.AskAsync(new(Epoch, question, null), default)).Status, question);
        }

        foreach (var page in new[] { "nowhere.md", "../outside.md", Path.Combine(fixture.GuideRoot, "sessions.md"), "" })
        {
            Assert.AreEqual("invalid_request", (await service.AskAsync(new(Epoch, "Why?", page), default)).Status, page);
        }

        fixture.Providers = [];
        Assert.AreEqual(new DocumentationAskResponse("no_provider", null, null), await service.AskAsync(new(Epoch, "Why?", null), default));
        fixture.Providers = Fixture.DefaultProviders;

        fixture.HasDefaultPrompt = null;
        Assert.AreEqual("closing", (await service.AskAsync(new(Epoch, "Why?", null), default)).Status);
        fixture.HasDefaultPrompt = false;
        Assert.AreEqual(new DocumentationAskResponse("failed", null, "There is no agent prompt 'default'."), await service.AskAsync(new(Epoch, "Why?", null), default));
        fixture.HasDefaultPrompt = true;

        fixture.HasCapacity = false;
        Assert.AreEqual("busy", (await service.AskAsync(new(Epoch, "Why?", null), default)).Status);
        fixture.HasCapacity = true;

        // The folder a failure names stays in the host.
        fixture.CreateFails = new IOException(@"C:\Users\someone\.alta\sessions is read-only");
        Assert.AreEqual(new DocumentationAskResponse("failed", null, null), await service.AskAsync(new(Epoch, "Why?", null), default));
        fixture.CreateFails = new ObjectDisposedException("host");
        Assert.AreEqual("closing", (await service.AskAsync(new(Epoch, "Why?", null), default)).Status);
        fixture.CreateFails = null;
        Assert.AreEqual(0, fixture.Created.Count);
        Assert.AreEqual(0, fixture.Sent.Count);

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.AskAsync(new(Epoch, "Why?", null), canceled.Token));
        Assert.AreEqual(0, fixture.Created.Count);

        // A chat whose prompt was refused is shown, with the word of the refusal.
        fixture.SendRefusal = "capacity";
        Assert.AreEqual(new DocumentationAskResponse("not_sent", "chat-1", "capacity"), await service.AskAsync(new(Epoch, "Why?", null), default));
    }

    [TestMethod]
    public async Task Ask_IsOneAtATime()
    {
        using var fixture = Fixture.Create();
        var gate = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.PromptGate = gate.Task;
        var service = fixture.Service(asker: fixture.Asker());
        var first = service.AskAsync(new(Epoch, "First?", null), default);
        Assert.AreEqual("busy", (await service.AskAsync(new(Epoch, "Second?", null), default)).Status);
        gate.SetResult(true);
        Assert.AreEqual("ok", (await first).Status);
        fixture.PromptGate = null;
        Assert.AreEqual("ok", (await service.AskAsync(new(Epoch, "Third?", null), default)).Status);
        CollectionAssert.AreEqual(new[] { "First?", "Third?" }, fixture.Created.Select(static chat => chat.Title).ToArray());
    }

    [TestMethod]
    public void TheChat_IsNamedAfterTheFirstLineOfTheQuestion_Shortened()
    {
        using var fixture = Fixture.Create();
        var asker = fixture.Asker();
        asker.AskAsync(new string('w', 100) + "\nsecond line", null, default).GetAwaiter().GetResult();
        var title = fixture.Created.Single().Title;
        Assert.AreEqual(72, title.Length);
        Assert.IsTrue(title.EndsWith('…') && title.StartsWith("wwww", StringComparison.Ordinal));
        Assert.ThrowsExactly<ArgumentException>(() => asker.AskAsync(" ", null, default).GetAwaiter().GetResult());
        Assert.ThrowsExactly<ArgumentException>(() => asker.AskAsync("Why?", "nowhere.md", default).GetAwaiter().GetResult());
    }

    [TestMethod]
    public async Task Watch_CarriesTheRequestsToShowAPage_OfTheSameHost()
    {
        using var fixture = Fixture.Create();
        var view = new DesktopDocumentationView();
        Assert.IsFalse(view.Show("sessions.md", null), "No window is there to show it.");
        var service = fixture.Service(view: view);

        await using (var stale = service.WatchAsync(new(OtherEpoch), default).GetAsyncEnumerator())
        {
            Assert.IsFalse(await stale.MoveNextAsync());
        }

        using var stop = new CancellationTokenSource();
        await using var events = service.WatchAsync(new(Epoch), stop.Token).GetAsyncEnumerator();
        var next = events.MoveNextAsync();
        for (var attempt = 0; attempt < 200 && !view.Show("sessions.md", "queue"); attempt++) await Task.Delay(10);
        Assert.IsTrue(await next);
        Assert.AreEqual(new DocumentationShowEvent("sessions.md", "queue"), events.Current);
        // No page: the tab shows the page it has; an anchor without a page names nothing.
        Assert.IsTrue(view.Show(" ", "queue"));
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.AreEqual(new DocumentationShowEvent(null, null), events.Current);
        await stop.CancelAsync();
    }

    [TestMethod]
    public async Task ALinkToAPageOfTheGuide_OpensTheDocumentation_AtItsHeading()
    {
        using var fixture = Fixture.Create();
        var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "global")).FullName });
        var editor = new DesktopEditorView();
        var opened = new List<ProjectFileShowEvent>();
        using var watching = editor.Watch(opened.Add);
        var view = new DesktopDocumentationView();
        var shown = new List<DocumentationShowEvent>();
        using var watchingGuide = view.Watch(shown.Add);
        var documentation = fixture.Documentation;
        var links = new DesktopFileLinks(projects, new DiskFolders(), editor, (_, _) => ValueTask.FromResult<string?>(null),
            (_, _) => Task.FromResult<(string Status, string? Root)>(("unknown_project", null)), static _ => false, Path.Combine(fixture.Root, "home"))
        {
            GuidePage = (file, anchor) => documentation.FindPage(file) is { } page && view.Show(page, anchor),
        };

        async Task<string> OpenAsync(string target)
        {
            Assert.IsTrue(DesktopFileLink.TryParse(target, out var link), target);
            return await links.OpenAsync(link, null, null, null, default);
        }

        var sessions = Path.Combine(fixture.GuideRoot, "sessions.md");
        Assert.AreEqual("ok", await OpenAsync(sessions));
        Assert.AreEqual("ok", await OpenAsync(sessions.Replace('\\', '/') + "#queue"));
        Assert.AreEqual("ok", await OpenAsync(new Uri(Path.Combine(fixture.GuideRoot, "plugins", "git.md")).AbsoluteUri + "#sign-in"));
        // A place in the file is no heading.
        Assert.AreEqual("ok", await OpenAsync(sessions + "#L3"));
        CollectionAssert.AreEqual(new[]
        {
            new DocumentationShowEvent("sessions.md", null), new DocumentationShowEvent("sessions.md", "queue"),
            new DocumentationShowEvent("plugins/git.md", "sign-in"), new DocumentationShowEvent("sessions.md", null),
        }, shown);
        Assert.AreEqual(0, opened.Count, "The code editor is not asked for a page of the guide.");

        // Any other file goes to the code editor, as before: a file beside the guide, and a file of the guide that is no page.
        var outside = Path.Combine(fixture.Root, "outside.md");
        File.WriteAllText(outside, "# Outside\n");
        Assert.AreEqual("ok", await OpenAsync(outside));
        Assert.AreEqual("ok", await OpenAsync(Path.Combine(fixture.GuideRoot, "menu.yml")));
        Assert.AreEqual(4, shown.Count);
        Assert.AreEqual(2, opened.Count);
    }

    [TestMethod]
    [DataRow("docs/sessions.md#queue", "queue")]
    [DataRow("docs/sessions.md#prompt-queue_2", "prompt-queue_2")]
    [DataRow("docs/sessions.md#L12", null)]
    [DataRow("docs/sessions.md#12", null)]
    [DataRow("docs/sessions.md", null)]
    [DataRow("docs/sessions.md#a b", null)]
    [DataRow("docs/sessions.md#a\"]", null)]
    public void TheFragmentOfALink_NamesAHeading_WhenItIsNoPlaceInTheFile(string target, string? anchor)
    {
        Assert.IsTrue(DesktopFileLink.TryParse(target, out var link), target);
        Assert.AreEqual(anchor, link.Anchor);
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly ModelProviderDescriptor[] DefaultProviders =
        [
            new(new ModelProviderId("alpha"), "Alpha"),
            new(new ModelProviderId("beta"), "Beta"),
            new(new ModelProviderId("gamma"), "Gamma") { IsDefault = true },
        ];

        private Fixture(string root)
        {
            Root = root;
            GuideRoot = Path.Combine(root, "user-guide");
            Documentation = new ShippedDocumentation(GuideRoot);
        }

        public string Root { get; }

        public string GuideRoot { get; }

        public ShippedDocumentation Documentation { get; }

        public IReadOnlyList<ModelProviderDescriptor> Providers { get; set; } = DefaultProviders;

        public string? Configured { get; set; } = "Beta";

        public bool? HasDefaultPrompt { get; set; } = true;

        public Task<bool?>? PromptGate { get; set; }

        public bool HasCapacity { get; set; } = true;

        public Exception? CreateFails { get; set; }

        public string? SendRefusal { get; set; }

        public List<(string Provider, string Title)> Created { get; } = [];

        public List<OwnedTextSendRequest> Sent { get; } = [];

        public static Fixture Create()
        {
            var fixture = new Fixture(Path.Combine(Path.GetTempPath(), "CodeAlta.DocumentationRpcTests", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path.Combine(fixture.GuideRoot, "plugins"));
            Directory.CreateDirectory(Path.Combine(fixture.GuideRoot, "img"));
            File.WriteAllText(Path.Combine(fixture.GuideRoot, "menu.yml"), "doc:\n  - {path: readme.md, title: \"<i class='bi bi-book'></i> User Guide\"}\n  - {path: sessions.md, title: \"Sessions\"}\n  - {path: plugins/readme.md, title: \"<i class='bi bi-puzzle'></i> Plugins\", folder: true}\n");
            File.WriteAllText(Path.Combine(fixture.GuideRoot, "plugins", "menu.yml"), "doc:\n  - {path: readme.md, title: \"Overview\"}\n  - {path: git.md, title: \"Git\"}\n");
            File.WriteAllText(Path.Combine(fixture.GuideRoot, "readme.md"), "---\ntitle: User Guide\n---\n\n# User Guide\n\nSee [Sessions]({{site.basepath}}/docs/sessions/#queue).\n\n{{ alta_shot \"alta-desktop-home.webp\" \"alta-home.png\" \"The workspace\" \"The main workspace.\" }}\n\n<figure>\n  <svg viewBox=\"0 0 1 1\" xmlns=\"http://www.w3.org/2000/svg\"><title>Flow</title></svg>\n</figure>\n");
            File.WriteAllText(Path.Combine(fixture.GuideRoot, "sessions.md"), "---\ntitle: Sessions\n---\n\n# Sessions\n\n## Queue\n\nA session keeps a **prompt queue** while it runs.\n");
            File.WriteAllText(Path.Combine(fixture.GuideRoot, "orphan.md"), "# Not in the menu\n");
            File.WriteAllText(Path.Combine(fixture.GuideRoot, "plugins", "readme.md"), "---\ntitle: Plugins\n---\n\n# Plugins\n");
            File.WriteAllText(Path.Combine(fixture.GuideRoot, "plugins", "git.md"), "---\ntitle: Git\n---\n\n# Git\n\n## Sign in\n");
            File.WriteAllBytes(Path.Combine(fixture.GuideRoot, "img", "alta-desktop-home.webp"), [82, 73, 70, 70]);
            return fixture;
        }

        public DocumentationService Service(DesktopDocumentationView? view = null, DocumentationAsker? asker = null, Action? asked = null)
            => new(Documentation, Epoch, view, asker, asked);

        public DocumentationAsker Asker() => new(Documentation, new DocumentationAskOperations(
            () => Providers,
            () => Configured,
            _ => PromptGate ?? Task.FromResult(HasDefaultPrompt),
            () => HasCapacity,
            (provider, title) =>
            {
                if (CreateFails is { } failure) throw failure;
                Created.Add((provider.ProviderId.Value, title));
                return Task.FromResult("chat-" + Created.Count);
            },
            request =>
            {
                Sent.Add(request);
                return SendRefusal;
            }));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A fixture that the system still holds is left to the temporary folder.
            }
        }
    }
}
