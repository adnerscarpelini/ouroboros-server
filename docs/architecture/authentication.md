# Autenticação e autorização entre serviços

## Emissão do access token

Identity possui credenciais, sessões e emissão. A arquitetura usa assinatura assimétrica: chave privada exclusiva no emissor, chaves públicas identificadas nos consumidores. Rotação define janela e retirada de chaves sem implantação conjunta. Disponibilidade: #84 e [estado de implementação](../implementation-status.md).

## Validação do token

Cada serviço valida assinatura, issuer, audience, expiração e principal. Usuário, integrador e serviço não são intercambiáveis. Não distribuir chave privada aos consumidores.

Validação obrigatória indisponível não concede acesso por fallback. Chaves e tokens não aparecem em logs.

## Autorização

Negar por padrão; anonimato é explícito. Cada operação exige capacidade e público adequados no serviço, inclusive acesso direto. Administração exige papel e restrição de rede.

### Políticas por capacidade

Preservar políticas das funcionalidades. Identificar usuário por ExternalId e sessão por sid. Revalidar estado autoritativo quando exigido. Rate limiting considera réplicas e headers somente de proxies confiáveis.

## Sessões e revogação

Refresh token é opaco, rotacionado e armazenado por hash. Reuso, logout e redefinição preservam invariantes locais. Logout mantém access tokens válidos até expiração conforme contrato.

A #38 exige recusar integrador desativado na próxima validação: consultar estado autoritativo e recusar quando não for possível validar. Cache/eventos com atraso não garantem essa regra.

## Testes

Verificar rotação, issuer/audience, principais, públicos, concorrência e indisponibilidade. A porta da #71 não muda algoritmo por si só.

[Voltar à arquitetura](README.md)
