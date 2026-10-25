using System.Security.Cryptography;
using Ouroboros.Identity.Domain.Users;

namespace Ouroboros.Identity.Application.Security;

// Verifica a senha de um usuário e, quando o hash armazenado usa parâmetros antigos, refaz o hash
// com os atuais. Quem chama é responsável por gravar o usuário depois.
internal sealed class UserPasswordVerifier(IPasswordHasher passwordHasher)
{
    // Hash de uma senha aleatória, gerado com os parâmetros atuais na criação do verificador (singleton),
    // e não no primeiro uso: assim o primeiro login com e-mail inexistente não demora mais que os outros.
    private readonly string _dummyHash = passwordHasher.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    public bool Verify(User user, string providedPassword)
    {
        var result = passwordHasher.Verify(user.PasswordHash, providedPassword);
        if (result == PasswordVerificationOutcome.SucceededRehashNeeded)
        {
            user.ChangePasswordHash(passwordHasher.Hash(providedPassword));
        }

        return result != PasswordVerificationOutcome.Failed;
    }

    // Executa uma verificação que sempre falha, com o custo de uma verificação real, para que um e-mail
    // inexistente não responda mais rápido que uma senha errada.
    public void VerifyWithoutUser(string providedPassword) => passwordHasher.Verify(_dummyHash, providedPassword);
}
