using Ouroboros.BuildingBlocks.Domain;

namespace Ouroboros.Identity.Domain.Parameters;

// Parâmetro do sistema: chave e valor em texto, com descrição, tipo e padrão de validação na própria
// tabela. Toda inclusão ou alteração gera uma cópia no histórico, com o autor.
internal sealed class Parameter : Entity
{
    public const int KeyMaxLength = 200;

    private Parameter(
        string key,
        string value,
        string description,
        ParameterDataType dataType,
        string? validationPattern,
        bool isSensitive,
        Guid? updatedBy)
    {
        Key = key;
        Value = value;
        Description = description;
        DataType = dataType;
        ValidationPattern = validationPattern;
        IsSensitive = isSensitive;
        UpdatedBy = updatedBy;
    }

    public string Key { get; private set; }

    public string Value { get; private set; }

    public string Description { get; private set; }

    public ParameterDataType DataType { get; private set; }

    public string? ValidationPattern { get; private set; }

    public bool IsSensitive { get; private set; }

    // Começa em 0 e sobe a cada alteração; serve para detectar edições concorrentes.
    public int Revision { get; private set; }

    // ExternalId de quem fez a última alteração; nulo quando veio de migration.
    public Guid? UpdatedBy { get; private set; }

    public static (Parameter Parameter, ParameterHistory History) Create(
        string key,
        string value,
        string description,
        ParameterDataType dataType,
        string? validationPattern,
        bool isSensitive,
        Guid? createdBy,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > KeyMaxLength)
        {
            throw new InvalidParameterException(key, $"a chave excede {KeyMaxLength} caracteres");
        }

        RequireDescription(key, description);
        RequireValidValue(key, dataType, validationPattern, value);

        var parameter = new Parameter(key, value, description, dataType, validationPattern, isSensitive, createdBy);
        return (parameter, ParameterHistory.ForCreation(parameter, createdBy, createdAt));
    }

    // Altera valor e/ou descrição; null mantém o atual.
    public ParameterHistory Change(string? value, string? description, Guid changedBy, DateTimeOffset changedAt)
    {
        var newValue = value ?? Value;
        var newDescription = description ?? Description;
        if (newValue == Value && newDescription == Description)
        {
            throw new InvalidParameterException(Key, "nenhuma alteração foi informada");
        }

        RequireDescription(Key, newDescription);
        RequireValidValue(Key, DataType, ValidationPattern, newValue);

        var history = ParameterHistory.ForChange(this, newValue, newDescription, changedBy, changedAt);
        Value = newValue;
        Description = newDescription;
        UpdatedBy = changedBy;
        Revision++;
        return history;
    }

    private static void RequireDescription(string key, string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidParameterException(key, "a descrição é obrigatória");
        }
    }

    private static void RequireValidValue(string key, ParameterDataType dataType, string? validationPattern, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (ParameterValueRules.Validate(dataType, validationPattern, value) is { } rule)
        {
            throw new InvalidParameterException(key, $"o valor {rule}");
        }
    }
}

internal sealed class InvalidParameterException(string key, string rule)
    : Exception($"Parâmetro '{key}' inválido: {rule}.")
{
    public string Key { get; } = key;

    public string Rule { get; } = rule;
}
