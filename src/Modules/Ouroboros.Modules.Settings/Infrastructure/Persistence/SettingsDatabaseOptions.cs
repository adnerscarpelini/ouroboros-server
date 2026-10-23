namespace Ouroboros.Modules.Settings.Infrastructure.Persistence;

// Mesma connection string dos demais módulos: um banco, um schema por módulo.
internal sealed class SettingsDatabaseOptions
{
    public const string ConnectionStringName = "Ouroboros";

    public string? ConnectionString { get; set; }
}
