using Ouroboros.Modules.Identity.Application.Security;
using Ouroboros.Modules.Identity.Domain.Users;
using Ouroboros.Modules.Identity.Infrastructure.Security;

namespace Ouroboros.IntegrationTests;

// Testes de unidade do hash de senha; não usam banco.
public sealed class PasswordHashingTests
{
    private const string Password = "uma frase longa o suficiente para a política";

    [Fact]
    public void Hasher_uses_configured_iteration_count()
    {
        var hash = new AspNetCorePasswordHasher().Hash(Password);

        Assert.Equal(AspNetCorePasswordHasher.IterationCount, IterationsOf(hash));
        Assert.Equal(210_000, AspNetCorePasswordHasher.IterationCount);
    }

    [Fact]
    public void Hash_verifies_only_the_original_password()
    {
        var hasher = new AspNetCorePasswordHasher();
        var hash = hasher.Hash(Password);

        Assert.Equal(PasswordVerificationOutcome.Succeeded, hasher.Verify(hash, Password));
        Assert.Equal(PasswordVerificationOutcome.Failed, hasher.Verify(hash, Password + "x"));
        Assert.NotEqual(hash, hasher.Hash(Password));
    }

    [Fact]
    public void Weaker_hash_is_rehashed_after_successful_verification()
    {
        var weakHash = new AspNetCorePasswordHasher(100_000).Hash(Password);
        var user = User.Create("cliente@example.com", weakHash, UserRole.Customer);
        var hasher = new AspNetCorePasswordHasher();
        var verifier = new UserPasswordVerifier(hasher);

        var verified = verifier.Verify(user, Password);

        Assert.True(verified);
        Assert.NotEqual(weakHash, user.PasswordHash);
        Assert.Equal(AspNetCorePasswordHasher.IterationCount, IterationsOf(user.PasswordHash));
        Assert.Equal(PasswordVerificationOutcome.Succeeded, hasher.Verify(user.PasswordHash, Password));
    }

    [Fact]
    public void Failed_verification_keeps_the_stored_hash()
    {
        var weakHash = new AspNetCorePasswordHasher(100_000).Hash(Password);
        var user = User.Create("cliente@example.com", weakHash, UserRole.Customer);

        var verified = new UserPasswordVerifier(new AspNetCorePasswordHasher()).Verify(user, "senha errada");

        Assert.False(verified);
        Assert.Equal(weakHash, user.PasswordHash);
    }

    // Formato V3: 1 byte de versão, PRF (4 bytes), iterações (4 bytes, big-endian), ...
    private static int IterationsOf(string hash)
    {
        var bytes = Convert.FromBase64String(hash);
        Assert.Equal(0x01, bytes[0]);
        return (bytes[5] << 24) | (bytes[6] << 16) | (bytes[7] << 8) | bytes[8];
    }
}
