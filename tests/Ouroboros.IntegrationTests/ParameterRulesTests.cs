using Ouroboros.Modules.Settings.Domain.Parameters;

namespace Ouroboros.IntegrationTests;

// Testes de unidade das regras de parâmetro; não usam banco.
public sealed class ParameterRulesTests
{
    [Theory]
    [InlineData(nameof(ParameterDataType.String), "qualquer coisa")]
    [InlineData(nameof(ParameterDataType.Integer), "42")]
    [InlineData(nameof(ParameterDataType.Integer), "-7")]
    [InlineData(nameof(ParameterDataType.Boolean), "true")]
    [InlineData(nameof(ParameterDataType.Boolean), "false")]
    [InlineData(nameof(ParameterDataType.Email), "oi@adnerscarpelini.com")]
    [InlineData(nameof(ParameterDataType.EmailList), "oi@adnerscarpelini.com")]
    [InlineData(nameof(ParameterDataType.EmailList), "oi@adnerscarpelini.com, outro@adnerscarpelini.com")]
    [InlineData(nameof(ParameterDataType.Domain), "adnerscarpelini.com")]
    [InlineData(nameof(ParameterDataType.Domain), "rh.adnerscarpelini.com.br")]
    public void Valid_values_are_accepted(string dataType, string value) =>
        Assert.Null(ParameterValueRules.Validate(Enum.Parse<ParameterDataType>(dataType), null, value));

    [Theory]
    [InlineData(nameof(ParameterDataType.Integer), "4.2")]
    [InlineData(nameof(ParameterDataType.Integer), "dez")]
    [InlineData(nameof(ParameterDataType.Boolean), "True")]
    [InlineData(nameof(ParameterDataType.Boolean), "sim")]
    [InlineData(nameof(ParameterDataType.Email), "nao-e-email")]
    [InlineData(nameof(ParameterDataType.Email), " oi@adnerscarpelini.com")]
    [InlineData(nameof(ParameterDataType.EmailList), "oi@adnerscarpelini.com, invalido")]
    [InlineData(nameof(ParameterDataType.EmailList), "")]
    [InlineData(nameof(ParameterDataType.Domain), "@adnerscarpelini.com")]
    [InlineData(nameof(ParameterDataType.Domain), "AdnerScarpelini.com")]
    [InlineData(nameof(ParameterDataType.Domain), "localhost")]
    [InlineData(nameof(ParameterDataType.Domain), "adnerscarpelini.com\n")]
    [InlineData(nameof(ParameterDataType.Domain), "adnerscarpelini.com\r\n")]
    [InlineData(nameof(ParameterDataType.Email), "oi@adnerscarpelini.com\n")]
    [InlineData(nameof(ParameterDataType.Email), "Oi@adnerscarpelini.com")]
    [InlineData(nameof(ParameterDataType.Email), "oi@localhost")]
    [InlineData(nameof(ParameterDataType.EmailList), "oi@adnerscarpelini.com\n")]
    [InlineData(nameof(ParameterDataType.EmailList), "a@adnerscarpelini.com,,b@adnerscarpelini.com")]
    [InlineData(nameof(ParameterDataType.EmailList), "a@adnerscarpelini.com, a@adnerscarpelini.com")]
    [InlineData(nameof(ParameterDataType.String), "texto com\ttab")]
    [InlineData(nameof(ParameterDataType.Integer), "1\n")]
    [InlineData(nameof(ParameterDataType.Email), "o\u200Bi@adnerscarpelini.com")]
    [InlineData(nameof(ParameterDataType.Email), "\"a b\"@adnerscarpelini.com")]
    [InlineData(nameof(ParameterDataType.Email), "ação@adnerscarpelini.com")]
    [InlineData(nameof(ParameterDataType.String), "linha\u2029parágrafo")]
    [InlineData(nameof(ParameterDataType.String), "a\u2028b")]
    public void Invalid_values_are_rejected(string dataType, string value) =>
        Assert.NotNull(ParameterValueRules.Validate(Enum.Parse<ParameterDataType>(dataType), null, value));

    [Fact]
    public void Validation_pattern_must_match_the_whole_value()
    {
        Assert.Null(ParameterValueRules.Validate(ParameterDataType.String, "[A-Z]{3}", "BRL"));
        Assert.NotNull(ParameterValueRules.Validate(ParameterDataType.String, "[A-Z]{3}", "BRLX"));
        Assert.NotNull(ParameterValueRules.Validate(ParameterDataType.String, "[A-Z]{3}", "BRL\n"));
    }

    [Fact]
    public void Change_without_any_difference_is_rejected()
    {
        var (parameter, _) = Parameter.Create(
            "Teste:Chave", "a", "Descrição.", ParameterDataType.String, null, false, null, DateTimeOffset.UtcNow);

        Assert.Throws<InvalidParameterException>(() => parameter.Change(null, null, Guid.CreateVersion7(), DateTimeOffset.UtcNow));
        Assert.Throws<InvalidParameterException>(() => parameter.Change("a", "Descrição.", Guid.CreateVersion7(), DateTimeOffset.UtcNow));
        Assert.Equal(0, parameter.Revision);
    }

    [Fact]
    public void History_copies_type_pattern_and_sensitivity()
    {
        var (parameter, created) = Parameter.Create(
            "Teste:Moeda", "BRL", "Moeda padrão.", ParameterDataType.String, "[A-Z]{3}", true, null, DateTimeOffset.UtcNow);

        var changed = parameter.Change("USD", null, Guid.CreateVersion7(), DateTimeOffset.UtcNow);

        foreach (var history in new[] { created, changed })
        {
            Assert.Equal(ParameterDataType.String, history.DataType);
            Assert.Equal("[A-Z]{3}", history.ValidationPattern);
            Assert.True(history.IsSensitive);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parameter_requires_description(string description) =>
        Assert.Throws<InvalidParameterException>(() => Parameter.Create(
            "Teste:Chave", "valor", description, ParameterDataType.String, null, false, null, DateTimeOffset.UtcNow));

    [Fact]
    public void Change_increments_revision_and_returns_history()
    {
        var (parameter, created) = Parameter.Create(
            "Teste:Chave", "a", "Descrição.", ParameterDataType.String, null, false, null, DateTimeOffset.UtcNow);
        var author = Guid.CreateVersion7();

        var changed = parameter.Change("b", null, author, DateTimeOffset.UtcNow);

        Assert.Equal(0, created.Revision);
        Assert.Equal(1, parameter.Revision);
        Assert.Equal(1, changed.Revision);
        Assert.Equal("a", changed.PreviousValue);
        Assert.Equal("b", changed.NewValue);
        Assert.Equal(author, changed.ChangedBy);
    }
}
