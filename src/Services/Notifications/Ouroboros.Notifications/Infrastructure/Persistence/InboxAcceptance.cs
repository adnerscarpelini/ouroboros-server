using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ouroboros.Messaging;
using Ouroboros.Notifications.Domain;

namespace Ouroboros.Notifications.Infrastructure.Persistence;

internal enum AcceptanceResult { Accepted, Duplicate, Expired, Superseded, Rejected, Conflict }

internal sealed class InboxAcceptance(NotificationsDbContext context, MessageFingerprints fingerprints, TimeProvider clock)
{
    internal async Task<AcceptanceResult> AcceptAsync(ReadOnlyMemory<byte> body, string? transportId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var valid = EnvelopeCodec.TryDecode(body, "notification.email.requested", 1, "identity", out var message, out var code);
        if (valid && transportId is not null && (!Guid.TryParse(transportId, out var headerId) || headerId != message!.MessageId))
        { valid = false; code = "invalid-contract"; }
        ReadOnlyMemory<byte> canonical = valid ? EnvelopeCodec.Encode(message!) : body;
        var fingerprint = fingerprints.Compute(canonical.Span);
        var id = message?.MessageId ?? SafeMessageId(body, transportId) ?? new Guid(fingerprint.Value.AsSpan(0, 16));
        var expiry = message?.ExpiresAt ?? SafeExpiry(body) ?? now;
        var retainUntil = InboxPersistence.RetainUntil(expiry);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext('notifications:message:' || {id.ToString("D")}))", cancellationToken);
        var existing = await context.Inbox.AsNoTracking().SingleOrDefaultAsync(row => row.MessageId == id, cancellationToken);
        if (existing is not null)
        {
            var comparison = fingerprints.Compute(canonical.Span, existing.FingerprintKeyId);
            if (CryptographicOperations.FixedTimeEquals(existing.Fingerprint, comparison.Value))
            {
                if (existing.ExpiresAt <= now)
                { await InboxPersistence.TerminalAsync(context, id, null, InboxState.Expired, null, now, cancellationToken); }
                await transaction.CommitAsync(cancellationToken);
                return AcceptanceResult.Duplicate;
            }
            // Não sobrescrever a identidade/conteúdo aceitos; interromper trabalho ainda não concluído.
            await InboxPersistence.TerminalAsync(context, id, null, InboxState.Failed, "content-conflict", now, cancellationToken);
            await InboxPersistence.AddFailureAsync(context, id, "content-conflict", comparison.Value, existing.ExpiresAt, now, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AcceptanceResult.Conflict;
        }
        var row = new InboxMessage
        {
            MessageId = id,
            Fingerprint = fingerprint.Value,
            FingerprintKeyId = fingerprint.KeyId,
            OccurredAt = valid ? message!.OccurredAt : now,
            ExpiresAt = expiry,
            AcceptedAt = now,
            UpdatedAt = now,
            State = valid ? InboxState.Accepted : InboxState.Failed,
            LastFailureCode = valid ? null : code,
        };
        var result = valid ? AcceptanceResult.Accepted : AcceptanceResult.Rejected;
        if (!valid)
        { await InboxPersistence.AddFailureAsync(context, id, code, fingerprint.Value, expiry, now, cancellationToken); }
        else
        {
            var expired = expiry <= now;
            if (expired) { row.State = InboxState.Expired; result = AcceptanceResult.Expired; }
            var alias = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message!.SupersessionKey)));
            await context.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext('notifications:supersession:' || {alias}))", cancellationToken);
            var cursor = await context.Supersessions.SingleOrDefaultAsync(item => item.Id == alias, cancellationToken);
            var occurred = new DateTimeOffset(message.OccurredAt.UtcTicks - message.OccurredAt.UtcTicks % 10, TimeSpan.Zero);
            var newer = cursor is null || occurred > cursor.LastOccurredAt
                || (occurred == cursor.LastOccurredAt && string.CompareOrdinal(id.ToString("N"), cursor.LastMessageId.ToString("N")) > 0);
            if (!newer)
            {
                if (!expired) { row.State = InboxState.Cancelled; result = AcceptanceResult.Superseded; }
            }
            else
            {
                await InboxPersistence.CancelOlderAsync(context, alias, now, cancellationToken);
                if (!expired)
                {
                    row.SupersessionId = alias;
                    row.ProtectedEnvelope = canonical.ToArray();
                }
            }
            if (cursor is null)
            { context.Supersessions.Add(new SupersessionCursor { Id = alias, LastOccurredAt = occurred, LastMessageId = id, RetainUntil = retainUntil }); }
            else
            {
                if (newer) { cursor.LastOccurredAt = occurred; cursor.LastMessageId = id; }
                if (retainUntil > cursor.RetainUntil) { cursor.RetainUntil = retainUntil; }
            }
        }
        context.Inbox.Add(row);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static Guid? SafeMessageId(ReadOnlyMemory<byte> body, string? header)
    {
        if (Guid.TryParse(header, out var transport) && transport != Guid.Empty) { return transport; }
        if (body.Length > EnvelopeCodec.MaximumEnvelopeBytes) { return null; }
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("messageId", out var value) && value.ValueKind == JsonValueKind.String
                && value.TryGetGuid(out var id) && id != Guid.Empty ? id : null;
        }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? SafeExpiry(ReadOnlyMemory<byte> body)
    {
        if (body.Length > EnvelopeCodec.MaximumEnvelopeBytes) { return null; }
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("expiresAt", out var value) && value.ValueKind == JsonValueKind.String
                && value.TryGetDateTimeOffset(out var date) && date.Offset == TimeSpan.Zero ? date : null;
        }
        catch (JsonException) { return null; }
    }
}
