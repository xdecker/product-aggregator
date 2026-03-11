namespace ProductAggregator.Core.Models;

public class RedisSettings
{
    public bool Enabled { get; set; } = false;
    public string ConnectionString { get; set; } = string.Empty;
    public string InstanceName { get; set; } = string.Empty;
}