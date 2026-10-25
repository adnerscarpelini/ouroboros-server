namespace Ouroboros.Modules.Identity.Domain.Parameters;

// Tipo do valor de um parâmetro, usado para validar a gravação. O valor é sempre texto na tabela.
internal enum ParameterDataType
{
    String,
    Integer,
    Boolean,
    Email,
    EmailList,
    Domain,
}
