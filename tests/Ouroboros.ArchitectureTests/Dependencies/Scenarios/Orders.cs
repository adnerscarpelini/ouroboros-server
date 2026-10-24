using System.Data;
using System.Net.Http;

// Serviços fictícios para os cenários controlados de DependencyRuleScenarioTests. O compilador gera o IL real destes tipos,
// então cada violação abaixo é de verdade: a dependência proibida aparece só no ponto indicado em cada comentário.
namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Domain
{
    public sealed class Order;

    // Domain usando Application, só dentro do corpo do método.
    public sealed class DomainUsesApplication
    {
        public int Count() => Application.OrderService.Count();
    }

    // Domain usando HTTP (BCL fora do que o domínio pode usar), só dentro do corpo do método.
    public sealed class DomainUsesHttp
    {
        public string Verb() => HttpMethod.Get.Method;
    }

    // Permitido: tipos do próprio Domain e BCL.
    public sealed class DomainAllowed
    {
        public Order Create() => new();

        public string Describe(DateTimeOffset moment) => moment.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }
}

namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Contracts
{
    public interface IOrderQuery
    {
        Guid Id { get; }
    }

    // Contracts expondo uma entidade de Domain na assinatura.
    public sealed class ContractsUsesDomain
    {
        public Domain.Order? Order { get; set; }
    }
}

namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Infrastructure
{
    public class OrdersStore
    {
        public int Count() => 0;
    }

    public sealed class StoreException : Exception;

    public static class Clock
    {
        public static DateTime Now() => DateTime.UnixEpoch;
    }

    // Permitido: Infrastructure implementa e usa Application e Domain.
    public sealed class InfrastructureAllowed
    {
        public int Run() => Application.OrderService.Count() + new Domain.Order().GetHashCode();
    }
}

namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.Orders.Application
{
    public static class OrderService
    {
        public static int Count() => 0;
    }

    // Application chamando um método estático de Infrastructure; a assinatura (DateTime) não revela a dependência.
    public sealed class BodyOnlyViolation
    {
        public DateTime Today() => Infrastructure.Clock.Now();
    }

    // Application instanciando uma classe de Infrastructure em uma variável local.
    public sealed class LocalVariableViolation
    {
        public int Run()
        {
            var store = new Infrastructure.OrdersStore();
            return store.Count();
        }
    }

    // Application capturando uma exceção de Infrastructure.
    public sealed class CatchViolation
    {
        public int Run()
        {
            try
            {
                return OrderService.Count();
            }
            catch (Infrastructure.StoreException)
            {
                return -1;
            }
        }
    }

    // Application usando Infrastructure apenas como argumento genérico.
    public sealed class GenericArgumentViolation
    {
        public int Run() => new List<Infrastructure.OrdersStore>().Count;
    }

    // Application herdando de Infrastructure.
    public sealed class BaseTypeViolation : Infrastructure.OrdersStore;

    // Application usando ADO.NET, que pertence ao adaptador de persistência.
    public sealed class DataAccessViolation
    {
        public int Run() => new DataTable().Rows.Count;
    }

    // Permitido: Domain, Contracts, o próprio Application e BCL.
    public sealed class ApplicationAllowed
    {
        public int Run(Contracts.IOrderQuery query) => OrderService.Count() + new Domain.Order().GetHashCode() + query.Id.GetHashCode();
    }

    // Application chamando um executável, no corpo do método.
    public sealed class CallsHost
    {
        public int Run() => Host.HostEntry.Start();
    }

    // Application de Orders chamando outro serviço, no corpo do método.
    public sealed class CallsPayments
    {
        public int Run() => Payments.Application.PaymentsService.Count();
    }
}

namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.Payments.Application
{
    public static class PaymentsService
    {
        public static int Count() => 0;
    }

    // Application de Payments chamando Orders, o que fecha um ciclo entre os serviços.
    public sealed class CallsOrders
    {
        public int Run() => Orders.Application.OrderService.Count();
    }
}

// Executável fictício: nenhuma biblioteca pode depender dele.
namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.Host
{
    public static class HostEntry
    {
        public static int Start() => 0;
    }
}

// Código técnico compartilhado fictício: não pode conhecer serviços.
namespace Ouroboros.ArchitectureTests.Dependencies.Scenarios.Shared
{
    public sealed class SharedUsesService
    {
        public int Run() => Orders.Application.OrderService.Count();
    }
}
