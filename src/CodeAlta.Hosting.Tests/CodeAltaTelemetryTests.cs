using CodeAlta.Agent.Telemetry;

namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class CodeAltaTelemetryTests
{
    [TestMethod]
    public void StartFromEnvironment_CreatesNothing_WithoutAnOtlpEndpoint()
    {
        var environment = new Dictionary<string, string> { [OpenTelemetryEnvironment.ServiceNameVariable] = "ci" };

        Assert.IsNull(CodeAltaTelemetry.StartFromEnvironment("altacli", environment.GetValueOrDefault, configureExporter: null));
    }

    [TestMethod]
    public void StartFromEnvironment_CreatesNothing_WhenOpenTelemetryIsTurnedOff()
    {
        var environment = new Dictionary<string, string>
        {
            [OpenTelemetryEnvironment.EndpointVariable] = "http://localhost:4318",
            [OpenTelemetryEnvironment.SdkDisabledVariable] = "true",
        };

        Assert.IsNull(CodeAltaTelemetry.StartFromEnvironment("altacli", environment.GetValueOrDefault, configureExporter: null));
    }

    [TestMethod]
    public void StartFromEnvironment_ExportsTheTraces_OfAnEndpointOfTheTraces_AndEndsOnce()
    {
        var environment = new Dictionary<string, string> { [OpenTelemetryEnvironment.TracesEndpointVariable] = "http://localhost:4318/v1/traces" };

        var telemetry = CodeAltaTelemetry.StartFromEnvironment("altacli", environment.GetValueOrDefault, configureExporter: null);

        Assert.IsNotNull(telemetry);
        telemetry.Dispose();
        telemetry.Dispose();
    }

    [TestMethod]
    public void StartFromEnvironment_RejectsAnEmptyServiceName()
        => Assert.ThrowsExactly<ArgumentException>(() => CodeAltaTelemetry.StartFromEnvironment(" "));
}
