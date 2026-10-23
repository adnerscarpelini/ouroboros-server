using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ouroboros.BuildingBlocks.Persistence;
using Ouroboros.Modules.Settings.Domain.Parameters;

namespace Ouroboros.Modules.Settings.Infrastructure.Persistence.Configurations;

internal sealed class ParameterConfiguration : EntityConfiguration<Parameter>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Parameter> builder)
    {
        builder.ToTable("parameters");
        builder.Property(parameter => parameter.Key).HasMaxLength(Parameter.KeyMaxLength);
        builder.HasIndex(parameter => parameter.Key).IsUnique();
        builder.Property(parameter => parameter.Value);
        builder.Property(parameter => parameter.Description);
        builder.Property(parameter => parameter.DataType).HasConversion<string>().HasMaxLength(20);
        builder.Property(parameter => parameter.ValidationPattern);
        builder.Property(parameter => parameter.IsSensitive);
        builder.Property(parameter => parameter.Revision).IsConcurrencyToken();
        builder.Property(parameter => parameter.UpdatedBy);
    }
}

internal sealed class ParameterHistoryConfiguration : EntityConfiguration<ParameterHistory>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ParameterHistory> builder)
    {
        builder.ToTable("parameter_history");
        builder.Property(history => history.ParameterKey).HasMaxLength(Parameter.KeyMaxLength);
        builder.HasIndex(history => new { history.ParameterKey, history.Revision }).IsUnique();
        builder.Property(history => history.PreviousValue);
        builder.Property(history => history.NewValue);
        builder.Property(history => history.PreviousDescription);
        builder.Property(history => history.NewDescription);
        builder.Property(history => history.DataType).HasConversion<string>().HasMaxLength(20);
        builder.Property(history => history.ValidationPattern);
        builder.Property(history => history.IsSensitive);
        builder.Property(history => history.ChangedBy);
        builder.Property(history => history.ChangedAt).HasColumnType("timestamptz");
    }
}
