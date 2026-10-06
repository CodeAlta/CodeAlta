using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ColorSchemesRpcTests
{
    private static readonly ColorSchemeColors NoColors = new(null, null, null, null, null, null, null);

    [TestMethod]
    public void Schemes_AreEmptyUntilOneIsSaved_AndTheFolderIsNotCreatedByListing()
    {
        using var profile = new Profile();
        var listing = profile.Service.List(new());

        Assert.AreEqual("ok", listing.Status);
        Assert.AreEqual(profile.Folder, listing.Directory);
        Assert.IsEmpty(listing.Schemes);
        Assert.IsEmpty(listing.Problems);
        Assert.IsFalse(Directory.Exists(profile.Folder));
    }

    [TestMethod]
    public void NewScheme_IsWrittenAsAFileNamedAfterIt_WithOnlyTheColorsItChooses()
    {
        using var profile = new Profile();
        var saved = profile.Service.Save(new(null, "  Deep Sea  ", "plum", null,
            NoColors with { Background = "#0B1D2A", Accent = "#3CF" }, NoColors with { Background = " #05101a " }));

        Assert.AreEqual("ok", saved.Status);
        Assert.AreEqual("deep-sea", saved.Id);
        // The file is what someone would write by hand: the name, the base, and the colors that were chosen.
        Assert.AreEqual("""
            {
              "name": "Deep Sea",
              "base": "plum",
              "dark": {
                "background": "#0b1d2a",
                "accent": "#33ccff"
              },
              "darker": {
                "background": "#05101a"
              }
            }

            """.ReplaceLineEndings("\n"), File.ReadAllText(Path.Combine(profile.Folder, "deep-sea.json")).ReplaceLineEndings("\n"));

        var scheme = profile.Service.List(new()).Schemes.Single();
        Assert.AreEqual(new ColorSchemeDocument("deep-sea", "Deep Sea", "plum", NoColors,
            NoColors with { Background = "#0b1d2a", Accent = "#33ccff" }, NoColors with { Background = "#05101a" }), scheme);
        // Nothing is left of the file it was staged in.
        Assert.HasCount(1, Directory.GetFiles(profile.Folder));
    }

    [TestMethod]
    public void NewSchemes_GetDistinctFileNames_AndASavedOneIsReplacedInPlace()
    {
        using var profile = new Profile();
        Assert.AreEqual("night", profile.Service.Save(new(null, "Night", null, null, null, null)).Id);
        Assert.AreEqual("night-2", profile.Service.Save(new(null, "night", null, null, null, null)).Id);
        // A name without a letter or a digit of the file system still gets a file; so does a reserved one.
        Assert.AreEqual("scheme", profile.Service.Save(new(null, "夜", null, null, null, null)).Id);
        Assert.AreEqual("scheme-2", profile.Service.Save(new(null, "CON", null, null, null, null)).Id);
        Assert.AreEqual("ete-indien", profile.Service.Save(new(null, "Été indien", null, null, null, null)).Id);

        var replaced = profile.Service.Save(new("night", "Night sky", "kiwi", NoColors with { Text = "#102030" }, null, null));
        Assert.AreEqual("ok", replaced.Status);
        Assert.AreEqual("night", replaced.Id);
        var listed = profile.Service.List(new()).Schemes;
        Assert.AreEqual("ete-indien, night, night-2, scheme, scheme-2", string.Join(", ", listed.Select(static scheme => scheme.Id)));
        var night = listed.Single(static scheme => scheme.Id == "night");
        Assert.AreEqual("Night sky", night.Name);
        Assert.AreEqual("kiwi", night.Base);
        Assert.AreEqual("#102030", night.Light.Text);
        // The names are kept as they were typed, in the file too.
        Assert.AreEqual("夜", listed.Single(static scheme => scheme.Id == "scheme").Name);
        StringAssert.Contains(File.ReadAllText(Path.Combine(profile.Folder, "ete-indien.json")), "Été indien");
    }

    [TestMethod]
    [DataRow(null, "Name", "blueprint", "#12345", "dark.background is not a color such as #1a2b3c.")]
    [DataRow(null, "Name", "blueprint", "red", "dark.background is not a color such as #1a2b3c.")]
    [DataRow(null, "Name", "blueprint", "#12345678", "dark.background is not a color such as #1a2b3c.")]
    [DataRow(null, "", "blueprint", null, "A color scheme needs a name.")]
    [DataRow(null, "   ", "blueprint", null, "A color scheme needs a name.")]
    [DataRow(null, "two\nlines", "blueprint", null, "The name of a color scheme is at most 64 characters on one line.")]
    [DataRow(null, "Name", "Blue Print", null, "The base of a color scheme is the id of a built-in scheme, such as blueprint.")]
    [DataRow(null, "Name", "../cherry", null, "The base of a color scheme is the id of a built-in scheme, such as blueprint.")]
    [DataRow("../escape", "Name", "blueprint", null, null)]
    [DataRow("a/b", "Name", "blueprint", null, null)]
    [DataRow(".hidden", "Name", "blueprint", null, null)]
    [DataRow("", "Name", "blueprint", null, null)]
    [DataRow("nul", "Name", "blueprint", null, "This file name is reserved on Windows.")]
    [DataRow("COM1.backup", "Name", "blueprint", null, "This file name is reserved on Windows.")]
    public void Save_RefusesWhatIsNotAScheme_WithTheReason(string? id, string name, string basis, string? background, string? message)
    {
        using var profile = new Profile();
        var saved = profile.Service.Save(new(id, name, basis, null, NoColors with { Background = background }, null));

        Assert.AreEqual("invalid", saved.Status);
        Assert.IsNull(saved.Id);
        if (message is not null) Assert.AreEqual(message, saved.Message);
        else StringAssert.StartsWith(saved.Message, "The file name of a color scheme has 1 to 64 letters");
        // Nothing was written, inside the folder or outside it.
        Assert.IsFalse(Directory.Exists(profile.Folder) && Directory.EnumerateFileSystemEntries(profile.Folder).Any());
        Assert.HasCount(0, Directory.GetFiles(profile.Root));
    }

    [TestMethod]
    public void Save_RefusesANameThatIsTooLong()
    {
        using var profile = new Profile();
        Assert.AreEqual("ok", profile.Service.Save(new(null, new string('a', ColorSchemesService.MaximumNameLength), null, null, null, null)).Status);
        Assert.AreEqual("invalid", profile.Service.Save(new(null, new string('a', ColorSchemesService.MaximumNameLength + 1), null, null, null, null)).Status);
    }

    [TestMethod]
    public void FilesWrittenByHand_AreRead_WithCommentsShortColorsAndUnknownValues()
    {
        using var profile = new Profile();
        profile.Write("Solar.json", """
            // My own scheme.
            {
              "base": "pineapple",
              "light": { "background": "#FDF6E3", "text": "#333", "muted": null, "future": "#000000", },
              "notes": ["anything else is ignored"],
            }
            """);
        profile.Write("readme.txt", "not a scheme, and not reported as one");
        profile.Write(".Solar.json.swp", "an editor's own file");

        var listing = profile.Service.List(new());
        Assert.AreEqual("ok", listing.Status);
        Assert.IsEmpty(listing.Problems);
        // A file without a name is named after itself.
        Assert.AreEqual(new ColorSchemeDocument("Solar", "Solar", "pineapple",
            NoColors with { Background = "#fdf6e3", Text = "#333333" }, NoColors, NoColors), listing.Schemes.Single());
    }

    [TestMethod]
    public void FilesThatAreNotSchemes_AreReportedWithTheReason_AndTheOthersAreListed()
    {
        using var profile = new Profile();
        profile.Write("good.json", """{ "name": "Good", "dark": { "accent": "#ff8800" } }""");
        profile.Write("broken.json", "{\n  \"name\": \"Broken\",\n  \"dark\": {\n");
        profile.Write("list.json", "[1, 2, 3]");
        profile.Write("color.json", """{ "dark": { "accent": "orange" } }""");
        profile.Write("number.json", """{ "light": { "text": 12 } }""");
        profile.Write("theme.json", """{ "darker": "#000000" }""");
        profile.Write("named.json", """{ "name": 5 }""");
        profile.Write("based.json", """{ "base": "Not An Id" }""");
        profile.Write("large.json", "{ \"name\": \"" + new string('x', ColorSchemesService.MaximumFileBytes) + "\" }");
        profile.Write("my scheme.json", "{}");

        var listing = profile.Service.List(new());
        Assert.AreEqual("ok", listing.Status);
        Assert.AreEqual("good", listing.Schemes.Single().Id);
        var problems = listing.Problems.ToDictionary(static problem => problem.File, static problem => problem.Message);
        Assert.HasCount(9, problems);
        Assert.AreEqual("The file is not valid JSON (line 4).", problems["broken.json"]);
        Assert.AreEqual("A color scheme is a JSON object.", problems["list.json"]);
        Assert.AreEqual("dark.accent is not a color such as #1a2b3c.", problems["color.json"]);
        Assert.AreEqual("light.text is not a color such as #1a2b3c.", problems["number.json"]);
        Assert.AreEqual("darker is an object of colors.", problems["theme.json"]);
        Assert.AreEqual("name is a text of at most 64 characters.", problems["named.json"]);
        Assert.AreEqual("base is the id of a built-in scheme, such as blueprint.", problems["based.json"]);
        Assert.AreEqual("The file is larger than 16 KB.", problems["large.json"]);
        StringAssert.StartsWith(problems["my scheme.json"], "The file name of a color scheme has 1 to 64 letters");
    }

    [TestMethod]
    public void Listing_IsBounded()
    {
        using var profile = new Profile();
        for (var index = 0; index < ColorSchemesService.MaximumSchemes + 3; index++) profile.Write($"scheme-{index:000}.json", "{}");

        var listing = profile.Service.List(new());
        Assert.HasCount(ColorSchemesService.MaximumSchemes, listing.Schemes);
        Assert.AreEqual("scheme-000", listing.Schemes[0].Id);
        var problem = listing.Problems.Single();
        Assert.AreEqual("scheme-064.json", problem.File);
        StringAssert.StartsWith(problem.Message, "More than 64 color schemes");
    }

    [TestMethod]
    public void Delete_RemovesTheFile_AndSaysWhenThereIsNone()
    {
        using var profile = new Profile();
        var id = profile.Service.Save(new(null, "Gone soon", null, null, null, null)).Id;

        Assert.AreEqual("ok", profile.Service.Delete(new(id)).Status);
        Assert.IsFalse(File.Exists(Path.Combine(profile.Folder, id + ".json")));
        Assert.AreEqual("not_found", profile.Service.Delete(new(id)).Status);
        Assert.AreEqual("invalid", profile.Service.Delete(new("../" + id)).Status);
        Assert.AreEqual("invalid", profile.Service.Delete(new(null)).Status);
    }

    [TestMethod]
    public void Reveal_ShowsTheFileOfASchemeOrTheFolder()
    {
        using var profile = new Profile();
        // Before any scheme exists the folder is created, so that there is a place to put a file.
        Assert.AreEqual("ok", profile.Service.Reveal(new(null)).Status);
        Assert.IsTrue(Directory.Exists(profile.Folder));
        var id = profile.Service.Save(new(null, "Shown", null, null, null, null)).Id!;
        Assert.AreEqual("ok", profile.Service.Reveal(new(id)).Status);
        Assert.AreEqual("not_found", profile.Service.Reveal(new("missing")).Status);
        Assert.AreEqual("invalid", profile.Service.Reveal(new("..")).Status);

        CollectionAssert.AreEqual(new[] { profile.Folder, Path.Combine(profile.Folder, "Shown.json".ToLowerInvariant()) }, profile.Revealed);

        using var closed = new Profile(reveals: false);
        Assert.AreEqual("failed", closed.Service.Reveal(new(null)).Status);
    }

    [TestMethod]
    public void WindowWithoutARoot_HasNoSchemes()
    {
        var service = new ColorSchemesService();

        Assert.AreEqual("unavailable", service.List(new()).Status);
        Assert.AreEqual("unavailable", service.Save(new(null, "Name", null, null, null, null)).Status);
        Assert.AreEqual("unavailable", service.Delete(new("name")).Status);
        Assert.AreEqual("unavailable", service.Reveal(new(null)).Status);
    }

    [TestMethod]
    [DataRow("#1A2B3C", "#1a2b3c")]
    [DataRow("#abc", "#aabbcc")]
    [DataRow(" #FFF ", "#ffffff")]
    [DataRow("1a2b3c", null)]
    [DataRow("#1a2b3", null)]
    [DataRow("#1a2b3c4d", null)]
    [DataRow("#gggggg", null)]
    [DataRow("", null)]
    public void Color_IsHexadecimalWithThreeOrSixDigits(string value, string? expected)
    {
        Assert.AreEqual(expected is not null, ColorSchemesService.TryNormalizeColor(value, out var color));
        if (expected is not null) Assert.AreEqual(expected, color);
    }

    private sealed class Profile : IDisposable
    {
        internal Profile(bool reveals = true)
        {
            Directory.CreateDirectory(Root);
            Service = new ColorSchemesService(Root, path => { Revealed.Add(path); return reveals; });
        }

        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-color-schemes-" + Guid.NewGuid().ToString("N"));

        internal string Folder => Path.Combine(Root, ColorSchemesService.FolderName);

        internal ColorSchemesService Service { get; }

        internal List<string> Revealed { get; } = [];

        internal void Write(string file, string text)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, file), text);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
