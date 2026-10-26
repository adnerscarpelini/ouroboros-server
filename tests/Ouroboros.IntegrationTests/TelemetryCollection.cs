namespace Ouroboros.IntegrationTests;

// ActivitySource/MeterListener são globais no processo: estes testes verificam contagens e limites de carga
// de um host por vez, sem que WebApplicationFactories concorrentes exportem os mesmos instrumentos.
[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection;
