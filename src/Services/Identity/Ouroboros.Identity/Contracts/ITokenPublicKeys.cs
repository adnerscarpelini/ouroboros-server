namespace Ouroboros.Identity.Contracts;

public interface ITokenPublicKeys
{
    PublicKeySet Get();
}

public sealed record PublicKeySet(IReadOnlyList<PublicTokenKey> Keys);
public sealed record PublicTokenKey(string Kty, string Use, string Alg, string Kid, string N, string E);
