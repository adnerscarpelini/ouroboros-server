using Ouroboros.Modules.Identity.Domain.Users;

namespace Ouroboros.Modules.Identity.Application.Security;

// Verifica a senha de um usuário e, quando o hash armazenado usa parâmetros antigos, refaz o hash
// com os atuais. Quem chama é responsável por gravar o usuário depois.
internal sealed class UserPasswordVerifier(IPasswordHasher passwordHasher)
{
    public bool Verify(User user, string providedPassword)
    {
        var result = passwordHasher.Verify(user.PasswordHash, providedPassword);
        if (result == PasswordVerificationOutcome.SucceededRehashNeeded)
        {
            user.ChangePasswordHash(passwordHasher.Hash(providedPassword));
        }

        return result != PasswordVerificationOutcome.Failed;
    }
}
