-- Operação explícita, depois de corrigir credenciais/configuração do publicador.
-- psql com a credencial autorizada de Identity e -v message_id=<UUID>, sem ler o payload.
-- Preserva identidade, tentativas, prazo e conteúdo protegido; não ressuscita terminais.
UPDATE identity.email_intents
SET last_failure_code = NULL, next_attempt_at = NULL, updated_at = clock_timestamp()
WHERE external_id = :'message_id'::uuid AND state = 'Pending'
  AND last_failure_code = 'configuration-suspended' AND expires_at > clock_timestamp()
  AND protected_envelope IS NOT NULL AND lease_id IS NULL
RETURNING external_id, state, attempts, expires_at;
