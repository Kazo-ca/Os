using System.Security.Cryptography;
using System.Text;

namespace Kazo.Os.Platform.Core.CloudAccounts;

/// <summary>
/// The PKCE pair of one authorization (RFC 7636, method <c>S256</c>), with the random
/// <c>state</c> that ties the callback page's answer to this request (ADR 0267 section 5).
/// </summary>
/// <param name="Verifier">
/// The code verifier: 43 characters of base64url from 32 random bytes. It never leaves the OS
/// until the token request, so a code stolen from the callback address is useless without it.
/// </param>
/// <param name="Challenge">Base64url of the SHA-256 of <see cref="Verifier"/>, sent with the authorization request.</param>
/// <param name="State">43 characters of base64url from 32 other random bytes.</param>
public sealed record OAuthPkce(string Verifier, string Challenge, string State)
{
    /// <summary>The only challenge method Kazo OS sends: <c>S256</c> (never <c>plain</c>).</summary>
    public const string ChallengeMethod = "S256";

    private const int RandomByteCount = 32;

    /// <summary>Draws a new verifier and state from the system's random number generator.</summary>
    public static OAuthPkce Create()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(RandomByteCount));
        return new OAuthPkce(verifier, ComputeChallenge(verifier), Base64Url(RandomNumberGenerator.GetBytes(RandomByteCount)));
    }

    /// <summary>Computes the <c>S256</c> challenge of a verifier: base64url, without padding, of its SHA-256.</summary>
    /// <param name="verifier">The code verifier (ASCII).</param>
    public static string ComputeChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>Base64url without padding (RFC 4648 section 5), as PKCE requires.</summary>
    /// <param name="bytes">Bytes to encode.</param>
    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
