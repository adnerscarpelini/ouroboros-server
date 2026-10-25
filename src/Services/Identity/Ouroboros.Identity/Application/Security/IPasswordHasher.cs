namespace Ouroboros.Identity.Application.Security;

// Porta do módulo para hash de senha; a implementação fica em Infrastructure.
internal interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerificationOutcome Verify(string passwordHash, string providedPassword);
}

internal enum PasswordVerificationOutcome
{
    Failed,
    Succeeded,

    // A senha confere, mas o hash usa parâmetros mais fracos que os atuais e deve ser refeito.
    SucceededRehashNeeded,
}
