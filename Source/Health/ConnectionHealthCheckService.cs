using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trignis.MicrosoftSQL.Models;
using RabbitMQ.Client;
using Azure.Messaging.ServiceBus;
using Amazon.SQS;
using Amazon.SQS.Model;
using System.Linq;

namespace Trignis.MicrosoftSQL.Services;

/// <summary>
/// Proactively checks message queue and database connectivity
/// </summary>
public class ConnectionHealthCheckService : BackgroundService
{
    private readonly ILogger<ConnectionHealthCheckService> _logger;
    private readonly IConfiguration _config;
    private readonly int _checkIntervalMinutes;
    private readonly bool _enabled;
    private readonly ConcurrentDictionary<string, ConnectionHealth> _healthStatus = new();

    public ConnectionHealthCheckService(
        ILogger<ConnectionHealthCheckService> logger,
        IConfiguration config)
    {
        _logger = logger;
        _config = config;
        _checkIntervalMinutes = _config.GetValue<int>("ChangeTracking:HealthCheckIntervalMinutes", 15);
        _enabled = _config.GetValue<bool>("ChangeTracking:HealthCheckEnabled", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogDebug("Connection health checks are disabled");
            return;
        }

        _logger.LogDebug("Connection health check service started (Interval: {Interval}min)", _checkIntervalMinutes);

        // Initial check after 30 seconds
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformHealthChecksAsync();
                await Task.Delay(TimeSpan.FromMinutes(_checkIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Connection health check cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error performing connection health checks");
            }
        }
    }

    private async Task PerformHealthChecksAsync()
    {
        _logger.LogDebug("Starting connection health checks...");

        var environments = _config.GetSection("ChangeTracking:Environments").Get<EnvironmentConfig[]>() ?? Array.Empty<EnvironmentConfig>();
        var apiEndpoints = environments.SelectMany(e => e.ChangeTracking.ApiEndpoints ?? Array.Empty<ApiEndpoint>()).ToArray();
        
        foreach (var endpoint in apiEndpoints)
        {
            if (string.IsNullOrEmpty(endpoint.MessageQueueType))
            {
                continue; // Skip HTTP endpoints
            }

            var key = $"{endpoint.MessageQueueType}:{endpoint.Key ?? "unnamed"}";

            try
            {
                var isHealthy = await CheckMessageQueueHealthAsync(endpoint);
                var previousHealth = _healthStatus.GetOrAdd(key, new ConnectionHealth { IsHealthy = true });

                if (isHealthy && !previousHealth.IsHealthy)
                {
                    // Recovered
                    _logger.LogInformation("Connection recovered: {Key} (was down for {Duration})", 
                        key, DateTime.UtcNow - previousHealth.LastFailureTime);
                    previousHealth.IsHealthy = true;
                    previousHealth.ConsecutiveFailures = 0;
                }
                else if (!isHealthy && previousHealth.IsHealthy)
                {
                    // First failure
                    _logger.LogWarning("Connection failed: {Key}", key);
                    previousHealth.IsHealthy = false;
                    previousHealth.LastFailureTime = DateTime.UtcNow;
                    previousHealth.ConsecutiveFailures = 1;
                }
                else if (!isHealthy)
                {
                    // Continued failure
                    previousHealth.ConsecutiveFailures++;
                    var duration = DateTime.UtcNow - previousHealth.LastFailureTime;
                    
                    if (previousHealth.ConsecutiveFailures % 4 == 0) // Alert every hour (15min * 4)
                    {
                        _logger.LogError("Connection still down: {Key} ({Failures} consecutive failures, down for {Duration})", 
                            key, previousHealth.ConsecutiveFailures, duration);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking health for {Key}", key);
            }
        }

        _logger.LogDebug("Connection health checks completed");
    }

    private async Task<bool> CheckMessageQueueHealthAsync(ApiEndpoint endpoint)
    {
        if (endpoint.MessageQueue == null)
        {
            return false;
        }

        switch (endpoint.MessageQueueType?.ToLower())
        {
            case "rabbitmq":
                return await CheckRabbitMQHealthAsync(endpoint.MessageQueue);
            case "azureservicebus":
                return await CheckAzureServiceBusHealthAsync(endpoint.MessageQueue);
            case "awssqs":
                return await CheckAwsSqsHealthAsync(endpoint.MessageQueue);
            default:
                return false;
        }
    }

    private async Task<bool> CheckRabbitMQHealthAsync(MessageQueueConfig config)
    {
        IConnection? connection = null;
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = config.HostName ?? "localhost",
                Port = config.Port,
                VirtualHost = config.VirtualHost ?? "/",
                UserName = config.Username ?? "guest",
                Password = config.Password ?? "guest",
                RequestedConnectionTimeout = TimeSpan.FromSeconds(10)
            };

            connection = await factory.CreateConnectionAsync();
            var isOpen = connection.IsOpen;
            
            if (isOpen)
            {
                await connection.CloseAsync();
            }
            
            return isOpen;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RabbitMQ health check failed for {Host}:{Port}", config.HostName, config.Port);
            return false;
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private async Task<bool> CheckAzureServiceBusHealthAsync(MessageQueueConfig config)
    {
        ServiceBusClient? client = null;
        try
        {
            client = new ServiceBusClient(config.ConnectionString);
            
            // Try to create a sender to verify connection
            ServiceBusSender? sender = null;
            try
            {
                if (!string.IsNullOrEmpty(config.QueueName))
                {
                    sender = client.CreateSender(config.QueueName);
                }
                else if (!string.IsNullOrEmpty(config.TopicName))
                {
                    sender = client.CreateSender(config.TopicName);
                }
                else
                {
                    return false;
                }

                // Just creating the sender verifies the connection is valid
                return true;
            }
            finally
            {
                if (sender != null)
                {
                    await sender.DisposeAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Azure Service Bus health check failed");
            return false;
        }
        finally
        {
            if (client != null)
            {
                await client.DisposeAsync();
            }
        }
    }

    private async Task<bool> CheckAwsSqsHealthAsync(MessageQueueConfig config)
    {
        AmazonSQSClient? client = null;
        try
        {
            var sqsConfig = new AmazonSQSConfig
            {
                MaxErrorRetry = 1,
                Timeout = TimeSpan.FromSeconds(10)
            };

            if (!string.IsNullOrEmpty(config.Region))
            {
                sqsConfig.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(config.Region);
            }

            if (!string.IsNullOrEmpty(config.AccessKeyId) && !string.IsNullOrEmpty(config.SecretAccessKey))
            {
                client = new AmazonSQSClient(config.AccessKeyId, config.SecretAccessKey, sqsConfig);
            }
            else
            {
                client = new AmazonSQSClient(sqsConfig);
            }

            // Try to get queue attributes to verify connection
            var request = new GetQueueAttributesRequest
            {
                QueueUrl = config.QueueUrl,
                AttributeNames = new System.Collections.Generic.List<string> { "QueueArn" }
            };

            var response = await client.GetQueueAttributesAsync(request);
            return response.HttpStatusCode == System.Net.HttpStatusCode.OK;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AWS SQS health check failed for {QueueUrl}", config.QueueUrl);
            return false;
        }
        finally
        {
            client?.Dispose();
        }
    }

    public ConnectionHealthStatus GetHealthStatus()
    {
        var status = new ConnectionHealthStatus
        {
            CheckTime = DateTime.UtcNow,
            TotalEndpoints = _healthStatus.Count,
            HealthyEndpoints = _healthStatus.Values.Count(h => h.IsHealthy),
            UnhealthyEndpoints = _healthStatus.Values.Count(h => !h.IsHealthy)
        };

        foreach (var kvp in _healthStatus)
        {
            status.Details[kvp.Key] = new EndpointHealthDetail
            {
                IsHealthy = kvp.Value.IsHealthy,
                ConsecutiveFailures = kvp.Value.ConsecutiveFailures,
                LastFailureTime = kvp.Value.LastFailureTime,
                DowntimeDuration = kvp.Value.IsHealthy ? TimeSpan.Zero : DateTime.UtcNow - kvp.Value.LastFailureTime
            };
        }

        return status;
    }
}

public class ConnectionHealth
{
    public bool IsHealthy { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime LastFailureTime { get; set; }
}

public class ConnectionHealthStatus
{
    public DateTime CheckTime { get; set; }
    public int TotalEndpoints { get; set; }
    public int HealthyEndpoints { get; set; }
    public int UnhealthyEndpoints { get; set; }
    public System.Collections.Generic.Dictionary<string, EndpointHealthDetail> Details { get; set; } = new();
}

public class EndpointHealthDetail
{
    public bool IsHealthy { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastFailureTime { get; set; }
    public TimeSpan DowntimeDuration { get; set; }
}