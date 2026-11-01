using System.Reflection;
using CodeAlta.Desktop.Rpc;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>The channels the services of the window offer the page, which has a budget for them.</summary>
[TestClass]
public sealed class RpcChannelBudgetTests
{
    /// <summary>
    /// The channels the page may hold at once. The calls of plugin canvases (<c>alta.rpc</c>) ride the single
    /// <c>canvases.watch</c> channel instead of adding channels, so that the budget does not grow with plugins.
    /// </summary>
    private const int PageBudget = 35;

    /// <summary>The channel methods the services offer today. A new one takes room from the budget: count it here.</summary>
    private const int Offered = 31;

    [TestMethod]
    public void TheChannelsOfTheServices_AreCounted_AndStayWithinThePageBudget()
    {
        var channels = typeof(ReviewsService).Assembly.GetTypes()
            .Where(static type => type.GetCustomAttribute<NeoRpcServiceAttribute>() is not null)
            .SelectMany(static type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Where(static method => method.GetCustomAttribute<NeoRpcMethodAttribute>() is not null
                && method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(NeoRpcChannel<>))
            .Select(static method => method.DeclaringType!.Name + "." + method.Name)
            .ToArray();

        Assert.AreEqual(Offered, channels.Length, "A channel was added or removed: " + string.Join(", ", channels.Order(StringComparer.Ordinal)));
        Assert.IsLessThanOrEqualTo(PageBudget, channels.Length);
    }
}
