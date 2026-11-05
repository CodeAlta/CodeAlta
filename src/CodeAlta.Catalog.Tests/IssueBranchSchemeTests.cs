using CodeAlta.Catalog.Issues;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class IssueBranchSchemeTests
{
    [TestMethod]
    public void WithoutFiles_TheSchemeIsTheDefault()
    {
        using var project = new Project();

        var reading = IssueBranchSchemes.Read(project.Root);

        Assert.AreSame(IssueBranchScheme.Default, reading.Scheme);
        Assert.IsNull(reading.Problem);
        Assert.IsFalse(reading.Configured);
        Assert.AreEqual("{type}/{id}-{title}", reading.Scheme.Template);
    }

    [TestMethod]
    public void TheUsersFile_SaysTheLastWord_KeyByKey_AndTheTypesWhole()
    {
        using var project = new Project();
        project.Write(".alta/issues.toml", """
            [azure_devops.views.Mine.assigned_to]
            value = "@me"

            [branch]
            template = "{type}/{id}-{title}"
            title_length = 30

            [branch.types]
            Bug = "fix"
            "User Story" = "feature"
            "*" = "chore"
            """);
        project.Write(".alta/local/issues.toml", """
            [branch]
            title_length = 20

            [branch.types]
            Bug = "bugfix"
            "*" = "feature"
            """);

        var reading = IssueBranchSchemes.Read(project.Root);

        Assert.IsNull(reading.Problem);
        Assert.AreEqual(("{type}/{id}-{title}", 20), (reading.Scheme.Template, reading.Scheme.TitleLength));
        CollectionAssert.AreEqual(new[] { new IssueBranchType("Bug", "bugfix"), new IssueBranchType("*", "feature") }, reading.Scheme.Types.ToArray());
    }

    [TestMethod]
    public void TheTeamsFile_Alone_GivesTheScheme()
    {
        using var project = new Project();
        project.Write(".alta/issues.toml", """
            [branch]
            template = "users/{id}-{title}"
            """);

        var reading = IssueBranchSchemes.Read(project.Root);

        Assert.AreEqual("users/{id}-{title}", reading.Scheme.Template);
        Assert.IsTrue(reading.Configured, "The team's file alone says the project has a convention.");
        Assert.AreEqual(IssueBranchScheme.DefaultTitleLength, reading.Scheme.TitleLength);
        Assert.AreSame(IssueBranchScheme.Default.Types, reading.Scheme.Types);
    }

    [TestMethod]
    public void WhatIsWrong_IsSaidWithItsFile_AndTheRestIsUsed()
    {
        using var project = new Project();
        project.Write(".alta/issues.toml", """
            [branch]
            template = "{type}/{nummer}-{title}"
            title_length = 500

            [branch.types]
            Bug = "fix it"
            Task = "task"
            """);
        project.Write(".alta/local/issues.toml", "[branch\ntemplate = ");

        var reading = IssueBranchSchemes.Read(project.Root);

        // The template is kept: the name it gives shows the mistake, and the problem says it.
        Assert.AreEqual("{type}/{nummer}-{title}", reading.Scheme.Template);
        Assert.AreEqual(IssueBranchScheme.DefaultTitleLength, reading.Scheme.TitleLength);
        CollectionAssert.AreEqual(new[] { new IssueBranchType("Task", "task") }, reading.Scheme.Types.ToArray());
        Assert.IsNotNull(reading.Problem);
        StringAssert.Contains(reading.Problem, "{nummer}");
        StringAssert.Contains(reading.Problem, "title_length");
        StringAssert.Contains(reading.Problem, "types.Bug");
        StringAssert.Contains(reading.Problem, Path.Combine(project.Root, ".alta", "local", "issues.toml") + "' is not valid TOML");
    }

    [TestMethod]
    public void ATemplate_HoldsOnlyKnownPlaceholders_InClosedBraces()
    {
        Assert.IsNull(IssueBranchScheme.TemplateProblem("{type}/{id}-{title}"));
        Assert.IsNull(IssueBranchScheme.TemplateProblem("users/bwi/{id}"));
        StringAssert.Contains(IssueBranchScheme.TemplateProblem("{type}/{user}"), "{user}");
        StringAssert.Contains(IssueBranchScheme.TemplateProblem("{type/{id}"), "{type/{id}");
        StringAssert.Contains(IssueBranchScheme.TemplateProblem("{type}/{id"), "no '}' closes");
        StringAssert.Contains(IssueBranchScheme.TemplateProblem("type}/{id}"), "no '{' opens");
        StringAssert.Contains(IssueBranchScheme.TemplateProblem("  "), "empty");
    }

    [TestMethod]
    public void Save_WritesTheWholeTable_AndLeavesTheViewsOfTheFile()
    {
        using var project = new Project();
        project.Write(".alta/issues.toml", """
            [azure_devops.views.Bugs.type]
            value = "Bug"

            [branch]
            template = "old/{id}"

            [branch.types]
            Epic = "epic"
            """);

        IssueBranchSchemes.Save(IssueViewSource.Team, project.Root, new("{type}/{id}-{title}", 25, [new("Bug", "fix"), new("User Story", "story"), new("*", "feature")]));

        // The save keeps the line endings of the file, which are those of this source file's checkout (CRLF with autocrlf).
        var text = File.ReadAllText(Path.Combine(project.Root, ".alta", "issues.toml")).ReplaceLineEndings("\n");
        StringAssert.Contains(text, "[azure_devops.views.Bugs.type]\nvalue = \"Bug\"");
        Assert.IsFalse(text.Contains("old/{id}", StringComparison.Ordinal) || text.Contains("Epic", StringComparison.Ordinal), text);
        var reading = IssueBranchSchemes.Read(project.Root);
        Assert.IsNull(reading.Problem);
        Assert.AreEqual(("{type}/{id}-{title}", 25), (reading.Scheme.Template, reading.Scheme.TitleLength));
        CollectionAssert.AreEqual(new[] { new IssueBranchType("Bug", "fix"), new IssueBranchType("User Story", "story"), new IssueBranchType("*", "feature") }, reading.Scheme.Types.ToArray());
        Assert.AreEqual(1, new IssueViewStore().ReadViews("azure_devops", project.Root).Count, "The view is still read.");

        IssueBranchSchemes.Save(IssueViewSource.Team, project.Root, null);

        Assert.IsFalse(File.ReadAllText(Path.Combine(project.Root, ".alta", "issues.toml")).Contains("branch", StringComparison.Ordinal));
        Assert.AreSame(IssueBranchScheme.Default, IssueBranchSchemes.Read(project.Root).Scheme);
    }

    [TestMethod]
    public void SavingAgainAndRemoving_GivesTheFileBackAsItWas()
    {
        using var project = new Project();
        const string views = "[azure_devops]\ncurrent = \"Mine\"\n\n[azure_devops.views.Mine.assigned_to]\nvalue = \"@me\"\n";
        project.Write(".alta/local/issues.toml", views);

        IssueBranchSchemes.Save(IssueViewSource.Personal, project.Root, IssueBranchScheme.Default);
        var once = File.ReadAllText(Path.Combine(project.Root, ".alta", "local", "issues.toml"));
        IssueBranchSchemes.Save(IssueViewSource.Personal, project.Root, IssueBranchScheme.Default);
        Assert.AreEqual(once, File.ReadAllText(Path.Combine(project.Root, ".alta", "local", "issues.toml")), "Saving the same scheme again changes nothing.");

        IssueBranchSchemes.Save(IssueViewSource.Personal, project.Root, null);
        Assert.AreEqual(views, File.ReadAllText(Path.Combine(project.Root, ".alta", "local", "issues.toml")));
    }

    [TestMethod]
    public void ASchemeWrittenInline_OrWithDottedKeys_IsNotSaidToBeRemoved()
    {
        foreach (var text in new[] { "branch = { template = \"team/{id}\" }\n", "branch.template = \"team/{id}\"\n" })
        {
            using var project = new Project();
            project.Write(".alta/issues.toml", text);

            var refused = Assert.ThrowsExactly<InvalidDataException>(() => IssueBranchSchemes.Save(IssueViewSource.Team, project.Root, null), text);

            StringAssert.Contains(refused.Message, Path.Combine(project.Root, ".alta", "issues.toml"));
            Assert.AreEqual(text, File.ReadAllText(Path.Combine(project.Root, ".alta", "issues.toml")), "The file is left as it is.");
            Assert.AreEqual("team/{id}", IssueBranchSchemes.Read(project.Root).Scheme.Template);
            Assert.ThrowsExactly<InvalidDataException>(() => IssueBranchSchemes.Save(IssueViewSource.Team, project.Root, IssueBranchScheme.Default), text);
        }
    }

    [TestMethod]
    public void Save_ForTheUser_KeepsTheFolderOutOfGit()
    {
        using var project = new Project();

        IssueBranchSchemes.Save(IssueViewSource.Personal, project.Root, IssueBranchScheme.Default with { Template = "{id}" });

        Assert.AreEqual("{id}", IssueBranchSchemes.Read(project.Root).Scheme.Template);
        Assert.AreEqual("*\n", File.ReadAllText(Path.Combine(project.Root, ".alta", "local", ".gitignore")));
    }

    [TestMethod]
    public void TheForm_ShowsTheFile_ElseWhatAppliesBelowIt()
    {
        using var project = new Project();
        project.Write(".alta/issues.toml", "[branch]\ntemplate = \"team/{id}\"\n");

        var team = IssueBranchSchemes.ReadForm(IssueViewSource.Team, project.Root);
        var mine = IssueBranchSchemes.ReadForm(IssueViewSource.Personal, project.Root);

        Assert.AreEqual(("team/{id}", true), (team.Scheme.Template, team.Own));
        Assert.AreEqual(("team/{id}", false), (mine.Scheme.Template, mine.Own), "Without a table of its own, the user's form starts from the team's.");

        project.Write(".alta/local/issues.toml", "[branch]\ntitle_length = 10\n");
        mine = IssueBranchSchemes.ReadForm(IssueViewSource.Personal, project.Root);
        Assert.AreEqual(("team/{id}", 10, true), (mine.Scheme.Template, mine.Scheme.TitleLength, mine.Own));
        Assert.AreEqual(IssueBranchScheme.DefaultTitleLength, IssueBranchSchemes.ReadForm(IssueViewSource.Team, project.Root).Scheme.TitleLength);
    }

    [TestMethod]
    public void AScheme_ThatCouldNotBeReadBack_IsNotWritten()
    {
        using var project = new Project();
        var good = IssueBranchScheme.Default;

        Assert.IsNull(IssueBranchSchemes.SchemeProblem(good));
        StringAssert.Contains(IssueBranchSchemes.SchemeProblem(good with { Template = "{nummer}" }), "{nummer}");
        StringAssert.Contains(IssueBranchSchemes.SchemeProblem(good with { TitleLength = 0 }), "length");
        StringAssert.Contains(IssueBranchSchemes.SchemeProblem(good with { Types = [new("Bug", "fix"), new("bug", "hotfix")] }), "twice");
        StringAssert.Contains(IssueBranchSchemes.SchemeProblem(good with { Types = [new(" ", "fix")] }), "no name");
        StringAssert.Contains(IssueBranchSchemes.SchemeProblem(good with { Types = [new("Bug", "fix it")] }), "Bug");
        Assert.ThrowsExactly<ArgumentException>(() => IssueBranchSchemes.Save(IssueViewSource.Team, project.Root, good with { Template = "{nummer}" }));
        Assert.IsFalse(File.Exists(Path.Combine(project.Root, ".alta", "issues.toml")));
    }

    private sealed class Project : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("alta-branch-scheme-").FullName;

        public void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
