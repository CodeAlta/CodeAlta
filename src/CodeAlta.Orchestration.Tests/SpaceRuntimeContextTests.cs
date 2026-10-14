using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SpaceRuntimeContextTests
{
    [TestMethod]
    public void TheSpaceLine_NamesTheSpace_AndWhatItIsForOnOneShortLine()
    {
        Assert.AreEqual("- Space: Default (default)", SystemPromptBuilder.SpaceLine(SpaceDescriptor.CreateDefault()));
        var business = new SpaceDescriptor { Id = "business", Name = "Business", Description = "Projects of the Contoso contract.\r\n\r\n  No personal work.  " };
        Assert.AreEqual("- Space: Business (business): Projects of the Contoso contract. No personal work.", SystemPromptBuilder.SpaceLine(business));

        business.Description = new string('a', 1000);
        var line = SystemPromptBuilder.SpaceLine(business);
        Assert.IsTrue(line.EndsWith('…'));
        Assert.AreEqual("- Space: Business (business): ".Length + 300, line.Length, "A long description is cut: it is read on every turn.");
    }
}
