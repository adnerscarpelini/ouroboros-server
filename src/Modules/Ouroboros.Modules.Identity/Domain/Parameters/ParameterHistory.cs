using Ouroboros.BuildingBlocks.Domain;

namespace Ouroboros.Modules.Identity.Domain.Parameters;

// Cópia (espelho) da linha do parâmetro a cada inclusão ou alteração: valores e descrição anteriores e
// novos, tipo, padrão e sensibilidade vigentes, o autor e o momento.
internal sealed class ParameterHistory : Entity
{
    private ParameterHistory(
        string parameterKey,
        int revision,
        string? previousValue,
        string newValue,
        string? previousDescription,
        string newDescription,
        ParameterDataType dataType,
        string? validationPattern,
        bool isSensitive,
        Guid? changedBy,
        DateTimeOffset changedAt)
    {
        ParameterKey = parameterKey;
        DataType = dataType;
        ValidationPattern = validationPattern;
        IsSensitive = isSensitive;
        Revision = revision;
        PreviousValue = previousValue;
        NewValue = newValue;
        PreviousDescription = previousDescription;
        NewDescription = newDescription;
        ChangedBy = changedBy;
        ChangedAt = changedAt;
    }

    public string ParameterKey { get; private set; }

    // Revisão do parâmetro depois desta alteração.
    public int Revision { get; private set; }

    public string? PreviousValue { get; private set; }

    public string NewValue { get; private set; }

    public string? PreviousDescription { get; private set; }

    public string NewDescription { get; private set; }

    public ParameterDataType DataType { get; private set; }

    public string? ValidationPattern { get; private set; }

    public bool IsSensitive { get; private set; }

    public Guid? ChangedBy { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public static ParameterHistory ForCreation(Parameter parameter, Guid? createdBy, DateTimeOffset createdAt) =>
        new(
            parameter.Key, 0, null, parameter.Value, null, parameter.Description,
            parameter.DataType, parameter.ValidationPattern, parameter.IsSensitive, createdBy, createdAt);

    public static ParameterHistory ForChange(
        Parameter parameter,
        string newValue,
        string newDescription,
        Guid changedBy,
        DateTimeOffset changedAt) =>
        new(
            parameter.Key, parameter.Revision + 1, parameter.Value, newValue, parameter.Description, newDescription,
            parameter.DataType, parameter.ValidationPattern, parameter.IsSensitive, changedBy, changedAt);
}
