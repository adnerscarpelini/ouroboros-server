namespace Ouroboros.Identity.Contracts.Registrations;

// Caso de uso: iniciar o cadastro de um cliente provando a posse do e-mail. O resultado é o mesmo
// exista ou não uma conta com o e-mail, para não revelar quais contas existem; só um formato inválido
// é informado.
public interface IRequestRegistration
{
    Task<RequestRegistrationResult> ExecuteAsync(RequestRegistrationRequest request, CancellationToken cancellationToken);
}

public sealed record RequestRegistrationRequest(string Email);

public enum RequestRegistrationResult
{
    // Aceita, com ou sem envio do link.
    Accepted,

    // Formato fora da regra de e-mail das contas.
    InvalidEmail,
}
