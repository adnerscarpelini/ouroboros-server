using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ouroboros.Modules.Identity.Infrastructure.Persistence;

// Aplica snake_case a tabelas, colunas, chaves e índices do modelo do módulo. É feito no próprio
// modelo, e não por uma convenção global, para não renomear a tabela de histórico de migrations
// (__EFMigrationsHistory), cujas colunas já existem nos bancos criados.
internal static class SnakeCaseNaming
{
    // Deve ser a última chamada do OnModelCreating, depois de todas as configurações.
    public static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            // Um owned type na tabela do dono (OwnsOne) herda dela o nome e a chave; renomeá-los
            // separadamente faria a chave do owned divergir da chave do dono.
            var sharesOwnerTable = entity.FindOwnership() is { } ownership
                && ownership.PrincipalEntityType.GetTableName() == entity.GetTableName();

            if (!sharesOwnerTable && entity.GetTableName() is { } tableName)
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            // O nome da coluna é lido em relação à tabela para incluir o prefixo dos owned types
            // (por exemplo, HomeAddress_StreetName).
            var table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);
            foreach (var property in entity.GetProperties())
            {
                if (sharesOwnerTable && property.IsPrimaryKey())
                {
                    continue;
                }

                var column = table is { } storeObject ? property.GetColumnName(storeObject) : property.GetColumnName();
                if (column is not null)
                {
                    property.SetColumnName(ToSnakeCase(column));
                }
            }

            if (!sharesOwnerTable)
            {
                foreach (var key in entity.GetKeys())
                {
                    key.SetName(ToSnakeCase(key.GetName()!));
                }
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                foreignKey.SetConstraintName(ToSnakeCase(foreignKey.GetConstraintName()!));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()!));
            }
        }
    }

    // "ExternalId" vira "external_id"; siglas como "HTTPStatus" viram "http_status". Um "_" já
    // existente (como em "PK_Users") é mantido.
    public static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (char.IsUpper(current) && i > 0 && StartsWord(name, i))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }

    private static bool StartsWord(string name, int index)
    {
        var previous = name[index - 1];
        var nextIsLower = index + 1 < name.Length && char.IsLower(name[index + 1]);
        return char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower);
    }
}
