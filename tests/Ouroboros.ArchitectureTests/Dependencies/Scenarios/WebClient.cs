using System.Data;

// Web client fictício para DependencyRuleScenarioTests: cada pasta do web client (Features/<Serviço>, Shared,
// Infrastructure e Shell) é um namespace sob Scenarios.WebClient. O IL real destes tipos contém exatamente a
// dependência indicada em cada comentário.
namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.WebClient.Features.Orders
{
    // Uma Feature usando outra Feature.
    public sealed class OrdersPageUsesPayments
    {
        public string Title() => Payments.PaymentsClient.Name();
    }

    // Uma Feature usando a implementação de um serviço do backend.
    public sealed class OrdersPageUsesService
    {
        public int Count() => global::Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Application.OrderService.Count();
    }

    // Uma Feature usando a composição na raiz, que poderia servir de ponte para outra Feature.
    public sealed class OrdersPageUsesRoot
    {
        public string Name() => RootComposition.Describe();
    }

    // Permitido: Shared e Infrastructure.
    public sealed class OrdersPageAllowed
    {
        public string Render() => Shared.StatusBadge.Text() + Infrastructure.GatewayPipeline.Describe();
    }
}

namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.WebClient
{
    // Composição na raiz: pode usar qualquer área, inclusive Features.
    public static class RootComposition
    {
        public static string Describe() => Features.Payments.PaymentsClient.Name();
    }
}

namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.WebClient.Features.Payments
{
    public static class PaymentsClient
    {
        public static string Name() => "payments";
    }
}

namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.WebClient.Shared
{
    public static class StatusBadge
    {
        public static string Text() => "ok";
    }

    // Componente genérico usando uma Feature.
    public sealed class SharedUsesFeature
    {
        public string Text() => Features.Payments.PaymentsClient.Name();
    }
}

namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.WebClient.Infrastructure
{
    public static class GatewayPipeline
    {
        public static string Describe() => "gateway";
    }

    // Infraestrutura usando um componente de Shared.
    public sealed class InfrastructureUsesShared
    {
        public string Text() => Shared.StatusBadge.Text();
    }

    // Acesso a dados (System.Data) no web client.
    public sealed class InfrastructureUsesData
    {
        public int Columns() => new DataTable().Columns.Count;
    }
}

namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.WebClient.Misc
{
    // Pasta fora das áreas conhecidas servindo de ponte para uma Feature: recusada pela área desconhecida.
    public static class FeatureBridge
    {
        public static string Name() => Features.Payments.PaymentsClient.Name();
    }
}

namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.WebClient.Shell
{
    // O layout usando uma Feature.
    public sealed class ShellUsesFeature
    {
        public string Text() => Features.Payments.PaymentsClient.Name();
    }

    // Permitido: Shared.
    public sealed class ShellAllowed
    {
        public string Text() => Shared.StatusBadge.Text();
    }
}
