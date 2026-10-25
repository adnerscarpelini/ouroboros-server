using RabbitMQ.Client;

namespace Ouroboros.Messaging;

public static class RabbitConnectionFactory
{
    public static ConnectionFactory Create(string host, int port, string user, string password, bool localDevelopment,
        string virtualHost = "ouroboros")
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(user)
            || user == "guest" || string.IsNullOrWhiteSpace(password) || password.Length < 32 || string.IsNullOrWhiteSpace(virtualHost))
        {
            throw new ArgumentException("Configuração do broker inválida.");
        }
        return new ConnectionFactory
        {
            HostName = host,
            Port = port,
            UserName = user,
            Password = password,
            VirtualHost = virtualHost,
            Ssl = new SslOption { Enabled = !localDevelopment, ServerName = host },
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = false,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            HandshakeContinuationTimeout = TimeSpan.FromSeconds(5),
            ContinuationTimeout = TimeSpan.FromSeconds(10),
            RequestedHeartbeat = TimeSpan.FromSeconds(15),
            RequestedChannelMax = 8,
            ConsumerDispatchConcurrency = 1,
        };
    }
}
