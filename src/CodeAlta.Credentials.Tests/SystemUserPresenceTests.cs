namespace CodeAlta.Credentials.Tests;

[TestClass]
public sealed class SystemUserPresenceTests
{
    [TestMethod]
    public void The_interface_id_of_a_parameterized_interface_is_the_one_of_the_Windows_Runtime()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            Assert.Inconclusive("Windows 11 only.");
            return;
        }

        // IAsyncOperation<bool>, as the Windows SDK names it.
        Assert.AreEqual(new Guid("cdb5efb3-5788-509d-9be1-71ccb8a3362a"), WindowsHelloVerifier.ParameterizedId("pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};b1)"));
    }

    [TestMethod]
    public async Task The_verification_of_the_system_says_why_it_cannot_ask()
    {
        var verifier = SystemUserPresence.ForThisSystem(() => 0);
        var availability = await verifier.GetAvailabilityAsync(CancellationToken.None);
        Assert.IsTrue(availability.Available || availability.Reason is { Length: > 0 }, "an availability that is not, says why");
        if (OperatingSystem.IsWindows()) Assert.AreEqual(UserPresenceResult.Unavailable, await verifier.VerifyAsync("x", CancellationToken.None), "without a window, nothing is asked");
    }
}
