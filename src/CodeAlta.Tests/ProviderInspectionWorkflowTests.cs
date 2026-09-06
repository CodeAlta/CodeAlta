using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Models;

namespace CodeAlta.Tests;

// Original pre-move cache/lookup cases exercise only the thin TUI adapters, never a coordinator instance.
[TestClass]
public sealed class ProviderInspectionWorkflowTests
{
    [TestMethod]
    [DataRow(ModelProviderAvailability.Unknown)]
    [DataRow(ModelProviderAvailability.Probing)]
    [DataRow(ModelProviderAvailability.Ready)]
    [DataRow(ModelProviderAvailability.Disabled)]
    [DataRow(ModelProviderAvailability.Unsupported)]
    [DataRow(ModelProviderAvailability.Failed)]
    public void CachedAvailability_PreservesDifferentTestAndListPolicies(ModelProviderAvailability availability)
    {
        var definition = CreateDefinition();
        definition.SortModels = true;
        var state = CreateState(availability);
        var states = new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase)
        {
            [definition.ProviderKey] = state,
        };

        var testHandled = ProviderFrontendCoordinator.TryBuildActiveProviderTestResult(definition, states, out var test);
        var listHandled = ProviderFrontendCoordinator.TryBuildActiveProviderModelListResult(definition, states, out var list);

        Assert.AreEqual(availability is ModelProviderAvailability.Ready or ModelProviderAvailability.Probing
            or ModelProviderAvailability.Failed or ModelProviderAvailability.Unsupported, testHandled);
        Assert.AreEqual(availability == ModelProviderAvailability.Ready, listHandled);
        if (availability == ModelProviderAvailability.Ready)
        {
            Assert.IsTrue(test.Success);
            Assert.AreEqual(2, test.ModelCount);
            Assert.AreEqual(SR.T("Using active model provider · {0} model(s) discovered.", 2), test.Message);
            Assert.IsTrue(list.Success);
            Assert.AreEqual(SR.T("Using active model provider · {0} model(s) available.", 2), list.Message);
            Assert.AreSame(state.Models, list.Models);
            CollectionAssert.AreEqual(new[] { "z-model", "a-model" }, list.Models.Select(model => model.Id).ToArray());
        }
        else
        {
            Assert.AreEqual(default, list);
            if (testHandled)
            {
                Assert.IsFalse(test.Success);
                Assert.AreEqual(state.StatusMessage, test.Message);
                Assert.AreEqual(0, test.ModelCount); // Cached non-ready models are not counted.
            }
            else
            {
                Assert.AreEqual(default, test);
            }
        }
    }

    [TestMethod]
    public void MissingCachedState_FallsThroughForBothOperations()
    {
        var states = new Dictionary<string, ModelProviderState>(StringComparer.OrdinalIgnoreCase);
        Assert.IsFalse(ProviderFrontendCoordinator.TryBuildActiveProviderTestResult(CreateDefinition(), states, out var test));
        Assert.IsFalse(ProviderFrontendCoordinator.TryBuildActiveProviderModelListResult(CreateDefinition(), states, out var list));
        Assert.AreEqual(default, test);
        Assert.AreEqual(default, list);
    }

    [TestMethod]
    [DataRow(false, "fixture", true)]
    [DataRow(false, "FIXTURE", false)]
    [DataRow(true, "FIXTURE", true)]
    [DataRow(true, "other", false)]
    public void CachedLookup_UsesOnlyKeyAndSuppliedComparerDespiteChangedDisabledSettings(
        bool ignoreCase, string key, bool expectedHandled)
    {
        var definition = CreateDefinition();
        definition.ProviderKey = key;
        definition.ProviderType = "changed-fixture-type";
        definition.ApiUrl = "https://changed.invalid";
        definition.ApiKey = "changed-literal-fake-key";
        definition.Enabled = false;
        definition.SortModels = true;
        var state = CreateState(ModelProviderAvailability.Ready);
        var states = new Dictionary<string, ModelProviderState>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["fixture"] = state,
        };

        Assert.AreEqual(expectedHandled, ProviderFrontendCoordinator.TryBuildActiveProviderTestResult(definition, states, out var test));
        Assert.AreEqual(expectedHandled, ProviderFrontendCoordinator.TryBuildActiveProviderModelListResult(definition, states, out var list));
        if (expectedHandled)
        {
            Assert.IsTrue(test.Success);
            Assert.IsTrue(list.Success);
            Assert.AreSame(state.Models, list.Models);
            Assert.AreEqual("z-model", list.Models[0].Id);
        }
        else
        {
            Assert.AreEqual(default, test);
            Assert.AreEqual(default, list);
        }
    }

    private static CodeAltaProviderDocument CreateDefinition()
        => new() { ProviderKey = "fixture", ProviderType = "fixture-only", ApiKey = "literal-fake-key" };

    private static ModelProviderState CreateState(ModelProviderAvailability availability)
    {
        var state = new ModelProviderState(new ModelProviderId("fixture"), "Fixture")
        {
            Availability = availability,
            StatusMessage = "Existing state message, unchanged.",
        };
        state.Models.Add(new AgentModelInfo("z-model"));
        state.Models.Add(new AgentModelInfo("a-model"));
        return state;
    }
}
