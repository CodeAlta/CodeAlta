using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.OpenAI.Codex;

internal static class OpenAICodexSubscriptionIdTokenValidator
{
    // OpenAI discovery advertises RS256 only. Use BCL crypto and explicit claim checks
    // to keep this small, fixed-issuer public client NativeAOT-compatible.
    public static OpenAICodexSubscriptionIdentity Validate(string token, JsonElement jwks, string clientId, string? nonce)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                throw new InvalidOperationException("Invalid ChatGPT ID token.");
            }

            using var header = JsonDocument.Parse(Decode(parts[0]));
            var root = header.RootElement;
            if (OpenAICodexSubscriptionOAuthClient.RequiredString(root, "alg") != "RS256" || root.TryGetProperty("crit", out _))
            {
                throw new InvalidOperationException("Unsupported ChatGPT ID-token signing algorithm.");
            }

            var kid = OpenAICodexSubscriptionOAuthClient.RequiredString(root, "kid");
            var key = jwks.GetProperty("keys").EnumerateArray().SingleOrDefault(key =>
                key.TryGetProperty("kid", out var value) && value.GetString() == kid);
            if (key.ValueKind != JsonValueKind.Object || key.GetProperty("kty").GetString() != "RSA" ||
                (key.TryGetProperty("use", out var use) && use.GetString() != "sig") ||
                (key.TryGetProperty("alg", out var alg) && alg.GetString() != "RS256"))
            {
                throw new InvalidOperationException("ChatGPT ID-token signing key was not found.");
            }

            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Decode(key.GetProperty("n").GetString()!),
                Exponent = Decode(key.GetProperty("e").GetString()!),
            });
            if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                throw new InvalidOperationException("ChatGPT ID-token signature validation failed.");
            }

            using var payload = JsonDocument.Parse(Decode(parts[1]));
            var claims = payload.RootElement;
            var audience = claims.GetProperty("aud");
            var validAudience = audience.ValueKind == JsonValueKind.String ? audience.GetString() == clientId :
                audience.ValueKind == JsonValueKind.Array && audience.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == clientId);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (claims.GetProperty("iss").GetString() != OpenAICodexSubscriptionOAuthDefaults.Issuer || !validAudience ||
                !claims.GetProperty("exp").TryGetInt64(out var expiry) || expiry <= now - 5 ||
                !claims.GetProperty("iat").TryGetInt64(out var issuedAt) || issuedAt > now + 5 ||
                (claims.TryGetProperty("nbf", out var nbf) && (!nbf.TryGetInt64(out var notBefore) || notBefore > now + 5)) ||
                (claims.TryGetProperty("azp", out var azp) && azp.GetString() != clientId) ||
                (audience.ValueKind == JsonValueKind.Array && audience.GetArrayLength() > 1 && !claims.TryGetProperty("azp", out _)))
            {
                throw new InvalidOperationException("ChatGPT ID-token issuer, audience, or lifetime validation failed.");
            }

            if (nonce is not null)
            {
                OpenAICodexSubscriptionOAuthClient.ValidateState(nonce, OpenAICodexSubscriptionOAuthClient.RequiredString(claims, "nonce"));
            }

            var subject = OpenAICodexSubscriptionOAuthClient.RequiredString(claims, "sub");
            var email = claims.TryGetProperty("email", out var emailClaim) && emailClaim.ValueKind == JsonValueKind.String ? emailClaim.GetString() : null;
            var accountId = claims.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object &&
                auth.TryGetProperty("chatgpt_account_id", out var account) && account.ValueKind == JsonValueKind.String ? account.GetString() : null;
            return new OpenAICodexSubscriptionIdentity(subject, email, accountId);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or KeyNotFoundException or ArgumentException)
        {
            throw new InvalidOperationException("ChatGPT ID-token validation failed.", ex);
        }
    }

    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}

internal sealed record OpenAICodexSubscriptionIdentity(string Subject, string? Email, string? AccountId);
