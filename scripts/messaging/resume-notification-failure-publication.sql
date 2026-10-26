-- Referências sanitizadas somente: corrigir o broker e indicar -v failure_id=<UUID>.
-- Não reprocessa o e-mail nem altera seu estado terminal.
WITH resumed AS (
    UPDATE notifications.failure_outbox SET suspended=false, next_attempt_at=NULL
    WHERE id=:'failure_id'::uuid AND suspended AND published_at IS NULL
      AND retain_until>clock_timestamp() AND lease_id IS NULL
    RETURNING id, retain_until
)
INSERT INTO notifications.processing_recoveries (id, message_id, actor, occurred_at, retain_until)
SELECT gen_random_uuid(), id, current_user, clock_timestamp(), retain_until FROM resumed
RETURNING id, message_id, actor, occurred_at;
