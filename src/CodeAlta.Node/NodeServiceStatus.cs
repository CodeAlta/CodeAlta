using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Update;
using CodeAlta.Fleet.Update.Service;
using CodeAlta.Hosting;

namespace CodeAlta.Node;

/// <summary>
/// The service that runs the node of a profile and whether it still runs after a restart or a logout, as <c>altanode status</c>
/// says it, for the page of the machine in the desktop (<c>control.status</c>): the Windows service of <c>install --system</c>
/// when there is one, else the service of the user.
/// </summary>
internal static class NodeServiceStatus
{
    /// <summary>Reads the service of a profile.</summary>
    /// <param name="profile">The profile of the node.</param>
    /// <param name="environment">The machine; null for this one.</param>
    /// <returns>The service, or null on an OS without a service manager altanode knows.</returns>
    public static FleetNodeServiceStatus? Read(CodeAltaInstanceProfile profile, ServiceEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if ((environment ?? ServiceEnvironment.Current()) is not { } env) return null;
        var manager = new ServiceManager(env);
        var defaultProfile = string.Equals(Path.GetFullPath(profile.GlobalRoot), Path.GetFullPath(CodeAltaInstanceProfile.GetDefaultGlobalRoot()),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        var name = ServiceDefinition.ServiceName(UpdateProduct.Node, profile.StateRoot, defaultProfile, profile.IsDeveloper);
        ServiceFacts? facts = null;
        if (env.Platform == ServicePlatform.Windows && manager.QueryWindowsService(UpdateProduct.Node, name) is { Installed: true } service) facts = service;
        if (facts is null)
        {
            var plan = manager.Plan(new ServiceRequest
            {
                Product = UpdateProduct.Node, Name = name, Executable = new ServiceExecutable(ServiceExecutableKind.Executable, "altanode", []), RunArguments = ["run"],
                StateRoot = profile.StateRoot,
            });
            facts = manager.Query(plan);
        }

        var platform = env.Platform switch { ServicePlatform.Windows => "windows", ServicePlatform.MacOS => "macos", _ => "linux" };
        return new FleetNodeServiceStatus(facts.Installed, ServiceManager.StartsAtBoot(facts), ServiceManager.Describe(facts), platform, facts.User);
    }
}
