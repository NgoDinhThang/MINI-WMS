using StackExchange.Redis;

namespace MiniWms.Infrastructure.Authentication;

/// <summary>Creates the shared Redis connection; injectable to verify connection-failure handling.</summary>
public interface IRedisConnectionFactory
{
    IConnectionMultiplexer Connect(ConfigurationOptions options);
}

public sealed class RedisConnectionFactory : IRedisConnectionFactory
{
    public IConnectionMultiplexer Connect(ConfigurationOptions options) => ConnectionMultiplexer.Connect(options);
}
