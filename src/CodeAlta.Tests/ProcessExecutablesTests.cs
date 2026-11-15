using CodeAlta.Core.Processes;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ProcessExecutablesTests
{
    [TestMethod]
    public void TheExecutablesOfTheRunningProcesses_IncludeTheOneOfThisProcess()
    {
        var listed = ProcessExecutables.List();

        CollectionAssert.Contains(listed.ToArray(), Environment.ProcessPath, string.Join(Environment.NewLine, listed.Take(20)));
    }
}
