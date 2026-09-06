using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionUserInputPolicyTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CreateResponse_EmptyForm_ReturnsEmptyAnswers(bool autoApprove)
    {
        Assert.AreEqual(0, SessionUserInputPolicy.CreateResponse(CreateRequest([]), autoApprove).Answers.Count);
    }

    [TestMethod]
    [DataRow(false, false, "")]
    [DataRow(false, true, "No preference. Use your best judgment and continue.")]
    [DataRow(true, false, "")]
    [DataRow(true, true, "")]
    public void CreateResponse_NoOptions_PreservesSecretAndFreeformPolicy(bool secret, bool freeform, string expected)
    {
        var request = CreateRequest([
            new AgentUserInputPrompt("absent", "Question", AllowFreeform: freeform, IsSecret: secret),
            new AgentUserInputPrompt("empty", "Question", Options: [], AllowFreeform: freeform, IsSecret: secret),
        ]);

        var response = SessionUserInputPolicy.CreateResponse(request, true);

        Assert.AreEqual(expected, response.Answers["absent"]);
        Assert.AreEqual(expected, response.Answers["empty"]);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void CreateResponse_OptionsTakePrecedenceOverSecretAndFreeform(bool secret, bool freeform)
    {
        var request = CreateRequest([
            new AgentUserInputPrompt("choice", "Question", Options: [new("Reject"), new("Continue")],
                AllowFreeform: freeform, IsSecret: secret),
        ]);

        Assert.AreEqual("Continue", SessionUserInputPolicy.CreateResponse(request, true).Answers["choice"]);
        Assert.AreEqual("", SessionUserInputPolicy.CreateResponse(request, false).Answers["choice"]);
    }

    [TestMethod]
    [DataRow("Pick", "alpha", "beta", "alpha")]
    [DataRow("Pick", "approve", "yes", "approve")]
    [DataRow("Pick", "cancel", "stop", "cancel")]
    [DataRow("Pick", "yes", "continue inspect search", "continue inspect search")]
    [DataRow("Pick", "continue cancel", "alpha", "continue cancel")]
    [DataRow("Pick", "different path", "alpha", "alpha")]
    [DataRow("Pick", "specify a different path", "alpha", "alpha")]
    [DataRow("Pick", "provide instructions", "alpha", "alpha")]
    [DataRow("Pick", "inspect locally", "alpha", "inspect locally")]
    [DataRow("Pick", "alpha", "yesterday", "yesterday")]
    [DataRow("Pick", "knowledge", "alpha", "alpha")]
    [DataRow("Pick", "yes yes yes", "approve continue", "approve continue")]
    [DataRow("Pick", "reject", " \tCoNtInUe\r\n", " \tCoNtInUe\r\n")]
    [DataRow("Pick", "yes", "inspect", "yes")]
    [DataRow("  WHICH OPTION?  ", "yes", "inspect", "inspect")]
    [DataRow("How should I proceed?", "yes", "run", "run")]
    [DataRow("Do you want me to act?", "yes", "use", "use")]
    [DataRow("Which option?", "yes cancel", "alpha", "alpha")]
    [DataRow(null, "yes", "inspect", "yes")]
    [DataRow("", "yes", "inspect", "yes")]
    public void CreateResponse_ScoresSubstringsAndQuestionHeuristicsWithFirstTieAndLiteralLabels(
        string? question, string first, string second, string expected)
    {
        // Null questions are accepted by the existing scoring policy despite the record annotation.
        var request = CreateRequest([
            new AgentUserInputPrompt("choice", question!, Options: [
                new(first, Description: "Continue proceed inspect search"),
                new(second, Description: "Reject cancel stop"),
            ]),
        ]);

        Assert.AreEqual(expected, SessionUserInputPolicy.CreateResponse(request, true).Answers["choice"]);
    }

    [TestMethod]
    public void CreateResponse_Disabled_ReturnsEmptyForEveryPromptWithoutInspectingOptions()
    {
        var request = CreateRequest([
            new AgentUserInputPrompt("freeform", "Question"),
            new AgentUserInputPrompt("secret", "Question", IsSecret: true),
            new AgentUserInputPrompt("closed", "Question", AllowFreeform: false),
            // Intentionally malformed records characterize the disabled branch's validation boundary.
            new AgentUserInputPrompt("options", "Question", Options: [null!, new(null!), new(" ")]),
        ]);

        var response = SessionUserInputPolicy.CreateResponse(request, false);

        Assert.AreEqual(4, response.Answers.Count);
        Assert.IsTrue(response.Answers.Values.All(static answer => answer == string.Empty));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CreateResponse_AnswerIdsAreOrdinalAndNotTrimmedOrValidatedForWhitespace(bool autoApprove)
    {
        var request = CreateRequest([
            new AgentUserInputPrompt("answer", "Question"),
            new AgentUserInputPrompt("Answer", "Question"),
            new AgentUserInputPrompt(" answer ", "Question"),
            new AgentUserInputPrompt("", "Question"),
            new AgentUserInputPrompt(" ", "Question"),
        ]);

        var response = SessionUserInputPolicy.CreateResponse(request, autoApprove);

        CollectionAssert.AreEquivalent(new[] { "answer", "Answer", " answer ", "", " " }, response.Answers.Keys.ToArray());
        Assert.IsFalse(response.Answers.ContainsKey("ANSWER"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CreateResponse_DuplicateIds_ThrowsRegardlessOfAutoApprove(bool autoApprove)
    {
        var request = CreateRequest([new("same", "First"), new("same", "Second")]);

        Assert.ThrowsExactly<ArgumentException>(() => SessionUserInputPolicy.CreateResponse(request, autoApprove));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CreateResponse_NullInputs_PreserveExistingExceptionTypes(bool autoApprove)
    {
        // Records do not validate these annotated inputs. Preserve the helper's actual failure behavior.
        var exception = Assert.ThrowsExactly<ArgumentNullException>(() => SessionUserInputPolicy.CreateResponse(null!, autoApprove));
        Assert.AreEqual("request", exception.ParamName);
        Assert.ThrowsExactly<NullReferenceException>(() => SessionUserInputPolicy.CreateResponse(CreateRequest([]) with { Form = null! }, autoApprove));
        Assert.ThrowsExactly<ArgumentNullException>(() => SessionUserInputPolicy.CreateResponse(CreateRequest(null!), autoApprove));
        Assert.ThrowsExactly<NullReferenceException>(() => SessionUserInputPolicy.CreateResponse(CreateRequest([null!]), autoApprove));
        Assert.ThrowsExactly<ArgumentNullException>(() => SessionUserInputPolicy.CreateResponse(CreateRequest([new(null!, "Question")]), autoApprove));
    }

    [TestMethod]
    public void CreateResponse_Enabled_ValidatesEveryOptionLabelEvenAfterPreferredChoice()
    {
        Assert.ThrowsExactly<NullReferenceException>(() => SessionUserInputPolicy.CreateResponse(
            CreateRequest([new("choice", "Question", Options: [new("continue"), null!])]), true));
        Assert.ThrowsExactly<ArgumentNullException>(() => SessionUserInputPolicy.CreateResponse(
            CreateRequest([new("choice", "Question", Options: [new("continue"), new(null!)])]), true));
        foreach (var label in new[] { "", " ", "\t\r\n" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => SessionUserInputPolicy.CreateResponse(
                CreateRequest([new("choice", "Question", Options: [new("continue"), new(label)])]), true));
        }
    }

    [TestMethod]
    public void CreateResponse_DoesNotMutateOrRetainSuppliedFormCollections()
    {
        var options = new List<AgentUserInputOption> { new("cancel"), new(" Continue ", "literal description") };
        var prompt = new AgentUserInputPrompt("choice", "Question", "Header", options, false, true);
        var prompts = new List<AgentUserInputPrompt> { prompt };
        var request = CreateRequest(prompts);
        var originalOptions = options.ToArray();

        var response = SessionUserInputPolicy.CreateResponse(request, true);

        Assert.AreSame(prompts, request.Form.Prompts);
        Assert.AreSame(prompt, prompts.Single());
        Assert.AreSame(options, prompt.Options);
        CollectionAssert.AreEqual(originalOptions, options);
        options.Clear();
        prompts.Clear();
        Assert.AreEqual(" Continue ", response.Answers["choice"]);
        Assert.AreEqual(1, response.Answers.Count);
        Assert.AreEqual(0, SessionUserInputPolicy.CreateResponse(request, true).Answers.Count);
    }

    private static AgentUserInputRequest CreateRequest(IReadOnlyList<AgentUserInputPrompt> prompts)
        => new(ModelProviderIds.Copilot, "session", DateTimeOffset.UnixEpoch, null, "interaction", new(prompts));
}
