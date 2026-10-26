-- Executar com a credencial autorizada do banco próprio e -v message_id=<UUID>.
-- Corrigir primeiro a configuração. Não reabre terminais, não zera tentativas nem amplia prazo.
WITH resumed AS (
    UPDATE notifications.inbox
    SET suspended=false, last_failure_code=NULL, next_attempt_at=NULL, updated_at=clock_timestamp()
    WHERE message_id=:'message_id'::uuid AND state='Accepted' AND suspended AND attempts<5
      AND expires_at>clock_timestamp() AND protected_envelope IS NOT NULL AND lease_id IS NULL
    RETURNING message_id, expires_at
)
INSERT INTO notifications.processing_recoveries (id, message_id, actor, occurred_at, retain_until)
SELECT gen_random_uuid(), message_id, current_user, clock_timestamp(), expires_at+interval '30 days' FROM resumed
RETURNING id, message_id, actor, occurred_at;
