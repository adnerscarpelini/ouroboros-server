namespace Ouroboros.Modules.Identity.Application.Sessions;

// Projeção da configuração validada pelo módulo, sem detalhes de JWT ou chave de assinatura.
internal sealed record SessionTokenOptions(TimeSpan RefreshTokenLifetime);
