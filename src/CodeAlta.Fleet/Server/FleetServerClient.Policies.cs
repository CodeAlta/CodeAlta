using System.Globalization;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Fleet.Server;

// The signed policy and its kill switch (doc/fleet/security-and-privacy.md, policies and the kill switch): read by every device
// of the organization, which verifies it; appended by a signer's controller device.
public sealed partial class FleetServerClient
{
    /// <summary>Reads the versions of the signed policy after a number.</summary>
    /// <param name="after">The number after which to list; 0 for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The versions, which the caller verifies.</returns>
    /// <exception cref="FleetServerException">The server refused the call.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<PolicyDocumentListDto> ListPolicyDocumentsAsync(long after = 0, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, "api/v1/policies?after=" + Math.Max(0, after).ToString(CultureInfo.InvariantCulture), null,
            FleetPolicyDocumentJsonContext.Default.PolicyDocumentListDto, cancellationToken);

    /// <summary>Appends a version of the policy this device signed.</summary>
    /// <param name="signed">The signed version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The version as stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signed"/> is null.</exception>
    /// <exception cref="FleetServerException">The server refused it.</exception>
    /// <exception cref="HttpRequestException">The server does not answer.</exception>
    public Task<PolicyDocumentDto> AppendPolicyDocumentAsync(FleetSignedPolicyDocument signed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signed);
        return SendAsync(HttpMethod.Post, "api/v1/policies", Body(new AppendPolicyDocumentRequest(signed), FleetPolicyDocumentJsonContext.Default.AppendPolicyDocumentRequest),
            FleetPolicyDocumentJsonContext.Default.PolicyDocumentDto, cancellationToken);
    }
}
