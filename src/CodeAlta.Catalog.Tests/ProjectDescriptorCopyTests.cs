using System.Collections;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProjectDescriptorCopyTests
{
    // Every editor of a project (CodeAlta TUI's details included) saves a copy: a value the copy forgets is erased from the file.
    [TestMethod]
    public void ACopy_KeepsEveryValue_AndSharesNoList()
    {
        var project = new ProjectDescriptor
        {
            Id = "019da767-146f-72c2-8781-b1ca95dedf10",
            Slug = "alpha",
            Name = "alpha",
            DisplayName = "Alpha",
            ProjectPath = @"C:\code\alpha",
            DefaultBranch = "trunk",
            Description = "A project.",
            Tags = ["one"],
            Spaces = ["work"],
            Labels = ["infra", "helm"],
            Archived = true,
            Sandbox = "strict",
            Checkout = new CheckoutRule { PathTemplate = "{projectName}-x", Depth = 1, Submodules = true },
            SourcePath = @"C:\alta\projects\alpha.md",
            MarkdownBody = "# Alpha",
        };

        var copy = project.Copy();
        foreach (var property in typeof(ProjectDescriptor).GetProperties().Where(static property => property.CanWrite))
        {
            var original = property.GetValue(project);
            var copied = property.GetValue(copy);
            if (original is IEnumerable list and not string)
            {
                CollectionAssert.AreEqual(list.Cast<object>().ToArray(), ((IEnumerable)copied!).Cast<object>().ToArray(), property.Name);
                Assert.AreNotSame(original, copied, property.Name);
            }
            else if (original is CheckoutRule rule)
            {
                var copiedRule = (CheckoutRule)copied!;
                Assert.AreNotSame(rule, copiedRule);
                Assert.AreEqual((rule.PathTemplate, rule.Depth, rule.Submodules), (copiedRule.PathTemplate, copiedRule.Depth, copiedRule.Submodules));
            }
            else
            {
                Assert.AreEqual(original, copied, property.Name);
            }
        }
    }
}
