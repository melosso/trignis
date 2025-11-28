using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Serilog;
using Trignis.MicrosoftSQL.Models;

namespace Trignis.MicrosoftSQL.Helpers;

/// <summary>
/// Validates configuration to catch issues early at startup
/// </summary>
public static class ConfigurationValidator
{
    public static void ValidateConfiguration(IConfiguration configuration)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        Log.Debug("Validating configuration...");

        // Validate global settings
        var globalSettings = configuration.GetSection("ChangeTracking:GlobalSettings").Get<GlobalSettings>() ?? new GlobalSettings();
        ValidateGlobalSettings(globalSettings, warnings);

        // Validate environments
        var environments = configuration.GetSection("ChangeTracking:Environments").Get<EnvironmentConfig[]>() ?? Array.Empty<EnvironmentConfig>();
        
        if (environments.Length == 0)
        {
            errors.Add("No environments configured in ChangeTracking:Environments");
        }
        else
        {
            foreach (var env in environments)
            {
                ValidateEnvironment(env, globalSettings, errors, warnings);
            }
        }

        // Validate health endpoint settings
        var healthEnabled = configuration.GetValue<bool>("Health:Enabled", false);
        if (healthEnabled)
        {
            var healthPort = configuration.GetValue<int>("Health:Port", 2455);
            if (healthPort < 1 || healthPort > 65535)
            {
                errors.Add($"Health:Port is set to {healthPort} which is invalid. Valid range: 1-65535");
            }
        }

        // Report results
        if (errors.Any())
        {
            Log.Error("Configuration validation failed with {ErrorCount} error(s):", errors.Count);
            foreach (var error in errors)
            {
                Log.Error("  – {Error}", error);
            }
            throw new InvalidOperationException($"Configuration validation failed with {errors.Count} error(s). See logs for details.");
        }

        if (warnings.Any())
        {
            Log.Warning("Configuration validation completed with {WarningCount} warning(s):", warnings.Count);
            foreach (var warning in warnings)
            {
                Log.Warning("  – {Warning}", warning);
            }
        }
        else
        {
            Log.Information("✓ Configuration validation passed");
        }
    }

    private static void ValidateGlobalSettings(GlobalSettings settings, List<string> warnings)
    {
        if (settings.PollingIntervalSeconds < 5)
        {
            warnings.Add($"Global PollingIntervalSeconds is set to {settings.PollingIntervalSeconds}s which may be too aggressive. Minimum recommended: 5s");
        }
        else if (settings.PollingIntervalSeconds > 3600)
        {
            warnings.Add($"Global PollingIntervalSeconds is set to {settings.PollingIntervalSeconds}s which may miss changes. Maximum recommended: 3600s");
        }

        if (settings.RetryCount < 0)
        {
            warnings.Add($"Global RetryCount is set to {settings.RetryCount} which is invalid. Using default 3");
        }
        else if (settings.RetryCount > 10)
        {
            warnings.Add($"Global RetryCount is set to {settings.RetryCount} which may be excessive. Recommended: 3-5");
        }

        if (settings.RetryDelaySeconds < 1)
        {
            warnings.Add($"Global RetryDelaySeconds is set to {settings.RetryDelaySeconds}s which is too low. Minimum: 1s");
        }

        if (settings.MaxRecordsPerBatch < 100)
        {
            warnings.Add($"MaxRecordsPerBatch is set to {settings.MaxRecordsPerBatch} which may create too many API calls. Recommended: 100-1000");
        }
        else if (settings.MaxRecordsPerBatch > 10000)
        {
            warnings.Add($"MaxRecordsPerBatch is set to {settings.MaxRecordsPerBatch} which may create very large payloads. Recommended: 100-1000");
        }

        if (settings.MaxPayloadSizeBytes < 1024 * 1024) // 1MB
        {
            warnings.Add($"MaxPayloadSizeBytes is set to {settings.MaxPayloadSizeBytes / 1024}KB which may be too small. Recommended: 1-10MB");
        }
    }

    private static void ValidateEnvironment(EnvironmentConfig env, GlobalSettings globalSettings, List<string> errors, List<string> warnings)
    {
        var envName = env.Name;

        // Validate tracking objects
        if (env.ChangeTracking.TrackingObjects.Length == 0)
        {
            warnings.Add($"Environment '{envName}' has no tracking objects configured");
        }
        else
        {
            foreach (var obj in env.ChangeTracking.TrackingObjects)
            {
                // Validate required fields
                if (string.IsNullOrWhiteSpace(obj.Name))
                {
                    errors.Add($"Environment '{envName}': Tracking object has no Name specified");
                }
                if (string.IsNullOrWhiteSpace(obj.Database))
                {
                    errors.Add($"Environment '{envName}': Tracking object '{obj.Name}' has no Database specified");
                }
                if (string.IsNullOrWhiteSpace(obj.TableName))
                {
                    errors.Add($"Environment '{envName}': Tracking object '{obj.Name}' has no TableName specified");
                }
                if (string.IsNullOrWhiteSpace(obj.StoredProcedureName))
                {
                    errors.Add($"Environment '{envName}': Tracking object '{obj.Name}' has no StoredProcedureName specified");
                }

                // Validate connection string exists in environment
                if (!env.ConnectionStrings.ContainsKey(obj.Database))
                {
                    errors.Add($"Environment '{envName}': Tracking object '{obj.Name}' references database '{obj.Database}' but no connection string found");
                }

                // Validate InitialSyncMode
                if (!string.IsNullOrEmpty(obj.InitialSyncMode) && 
                    !obj.InitialSyncMode.Equals("Full", StringComparison.OrdinalIgnoreCase) && 
                    !obj.InitialSyncMode.Equals("Incremental", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Environment '{envName}': Tracking object '{obj.Name}' has invalid InitialSyncMode '{obj.InitialSyncMode}'. Valid values: 'Full', 'Incremental'");
                }
            }
        }

        // Validate API endpoints if API export is enabled
        var exportToApi = env.ChangeTracking.ExportToApi ?? globalSettings.ExportToApi;
        if (exportToApi)
        {
            if (env.ChangeTracking.ApiEndpoints.Length == 0)
            {
                warnings.Add($"Environment '{envName}': ExportToApi is enabled but no API endpoints are configured");
            }
            else
            {
                foreach (var endpoint in env.ChangeTracking.ApiEndpoints)
                {
                    ValidateApiEndpoint(endpoint, envName, errors, warnings);
                }
            }
        }

        // Validate file export settings
        var exportToFile = env.ChangeTracking.ExportToFile ?? globalSettings.ExportToFile;
        if (exportToFile)
        {
            var filePath = env.ChangeTracking.FilePath ?? globalSettings.FilePath;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                warnings.Add($"Environment '{envName}': ExportToFile is enabled but FilePath is not configured");
            }
        }

        // Validate environment-specific overrides
        if (env.ChangeTracking.PollingIntervalSeconds.HasValue)
        {
            var interval = env.ChangeTracking.PollingIntervalSeconds.Value;
            if (interval < 5)
            {
                warnings.Add($"Environment '{envName}': PollingIntervalSeconds is set to {interval}s which may be too aggressive. Minimum recommended: 5s");
            }
            else if (interval > 3600)
            {
                warnings.Add($"Environment '{envName}': PollingIntervalSeconds is set to {interval}s which may miss changes. Maximum recommended: 3600s");
            }
        }

        if (env.ChangeTracking.RetryCount.HasValue)
        {
            var retryCount = env.ChangeTracking.RetryCount.Value;
            if (retryCount < 0)
            {
                warnings.Add($"Environment '{envName}': RetryCount is set to {retryCount} which is invalid");
            }
            else if (retryCount > 10)
            {
                warnings.Add($"Environment '{envName}': RetryCount is set to {retryCount} which may be excessive. Recommended: 3-5");
            }
        }

        if (env.ChangeTracking.RetryDelaySeconds.HasValue)
        {
            var retryDelay = env.ChangeTracking.RetryDelaySeconds.Value;
            if (retryDelay < 1)
            {
                warnings.Add($"Environment '{envName}': RetryDelaySeconds is set to {retryDelay}s which is too low. Minimum: 1s");
            }
        }
    }

    private static void ValidateApiEndpoint(ApiEndpoint endpoint, string envName, List<string> errors, List<string> warnings)
    {
        var endpointName = endpoint.Key ?? "unnamed endpoint";

        // Validate message queue configuration
        if (!string.IsNullOrEmpty(endpoint.MessageQueueType))
        {
            if (endpoint.MessageQueue == null)
            {
                errors.Add($"Environment '{envName}': API endpoint '{endpointName}' has MessageQueueType '{endpoint.MessageQueueType}' but MessageQueue configuration is missing");
                return;
            }

            switch (endpoint.MessageQueueType.ToLower())
            {
                case "rabbitmq":
                    ValidateRabbitMQConfig(endpoint.MessageQueue, envName, endpointName, errors, warnings);
                    break;
                case "azureservicebus":
                    ValidateAzureServiceBusConfig(endpoint.MessageQueue, envName, endpointName, errors, warnings);
                    break;
                case "awssqs":
                    ValidateAwsSqsConfig(endpoint.MessageQueue, envName, endpointName, errors, warnings);
                    break;
                default:
                    errors.Add($"Environment '{envName}': API endpoint '{endpointName}' has unsupported MessageQueueType '{endpoint.MessageQueueType}'. Valid types: RabbitMQ, AzureServiceBus, AWSSQS");
                    break;
            }
        }
        else
        {
            // HTTP endpoint validation
            if (string.IsNullOrWhiteSpace(endpoint.Url))
            {
                errors.Add($"Environment '{envName}': API endpoint '{endpointName}' has no Url specified");
            }
            else if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri))
            {
                errors.Add($"Environment '{envName}': API endpoint '{endpointName}' has invalid Url '{endpoint.Url}'");
            }
            else if (uri.Scheme != "http" && uri.Scheme != "https")
            {
                warnings.Add($"Environment '{envName}': API endpoint '{endpointName}' uses non-HTTP(S) scheme '{uri.Scheme}'");
            }

            // Validate authentication
            ValidateAuth(endpoint.Auth, envName, endpointName, warnings);
        }
    }

    private static void ValidateRabbitMQConfig(MessageQueueConfig config, string envName, string endpointName, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(config.HostName))
        {
            errors.Add($"Environment '{envName}': RabbitMQ endpoint '{endpointName}' has no HostName specified");
        }

        if (config.Port <= 0 || config.Port > 65535)
        {
            warnings.Add($"Environment '{envName}': RabbitMQ endpoint '{endpointName}' has invalid Port {config.Port}. Using default 5672");
        }

        if (string.IsNullOrWhiteSpace(config.QueueName) && string.IsNullOrWhiteSpace(config.Exchange))
        {
            errors.Add($"Environment '{envName}': RabbitMQ endpoint '{endpointName}' must specify either QueueName or Exchange");
        }

        if (!string.IsNullOrWhiteSpace(config.QueueName) && !string.IsNullOrWhiteSpace(config.Exchange))
        {
            warnings.Add($"Environment '{envName}': RabbitMQ endpoint '{endpointName}' has both QueueName and Exchange specified. Exchange will be used");
        }
    }

    private static void ValidateAzureServiceBusConfig(MessageQueueConfig config, string envName, string endpointName, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            errors.Add($"Environment '{envName}': Azure Service Bus endpoint '{endpointName}' has no ConnectionString specified");
        }

        if (string.IsNullOrWhiteSpace(config.QueueName) && string.IsNullOrWhiteSpace(config.TopicName))
        {
            errors.Add($"Environment '{envName}': Azure Service Bus endpoint '{endpointName}' must specify either QueueName or TopicName");
        }

        if (!string.IsNullOrWhiteSpace(config.QueueName) && !string.IsNullOrWhiteSpace(config.TopicName))
        {
            warnings.Add($"Environment '{envName}': Azure Service Bus endpoint '{endpointName}' has both QueueName and TopicName specified. QueueName will be used");
        }
    }

    private static void ValidateAwsSqsConfig(MessageQueueConfig config, string envName, string endpointName, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(config.QueueUrl))
        {
            errors.Add($"Environment '{envName}': AWS SQS endpoint '{endpointName}' has no QueueUrl specified");
        }
        else if (!Uri.TryCreate(config.QueueUrl, UriKind.Absolute, out var uri))
        {
            errors.Add($"Environment '{envName}': AWS SQS endpoint '{endpointName}' has invalid QueueUrl '{config.QueueUrl}'");
        }

        if (string.IsNullOrWhiteSpace(config.Region))
        {
            warnings.Add($"Environment '{envName}': AWS SQS endpoint '{endpointName}' has no Region specified. Will use default region");
        }

        var hasAccessKey = !string.IsNullOrWhiteSpace(config.AccessKeyId);
        var hasSecretKey = !string.IsNullOrWhiteSpace(config.SecretAccessKey);

        if (hasAccessKey != hasSecretKey)
        {
            errors.Add($"Environment '{envName}': AWS SQS endpoint '{endpointName}' has incomplete credentials. Both AccessKeyId and SecretAccessKey must be provided, or neither");
        }

        if (!hasAccessKey && !hasSecretKey)
        {
            warnings.Add($"Environment '{envName}': AWS SQS endpoint '{endpointName}' has no explicit credentials. Will use default AWS credential chain");
        }
    }

    private static void ValidateAuth(ApiAuth? auth, string envName, string endpointName, List<string> warnings)
    {
        if (auth == null)
        {
            warnings.Add($"Environment '{envName}': API endpoint '{endpointName}' has no authentication configured");
            return;
        }

        if (string.IsNullOrWhiteSpace(auth.Type))
        {
            warnings.Add($"Environment '{envName}': API endpoint '{endpointName}' has Auth configured but no Type specified");
            return;
        }

        switch (auth.Type.ToLower())
        {
            case "bearer":
                if (string.IsNullOrWhiteSpace(auth.Token))
                {
                    warnings.Add($"Environment '{envName}': API endpoint '{endpointName}' uses Bearer auth but Token is not specified");
                }
                break;

            case "basic":
                if (string.IsNullOrWhiteSpace(auth.Username) || string.IsNullOrWhiteSpace(auth.Password))
                {
                    warnings.Add($"Environment '{envName}': API endpoint '{endpointName}' uses Basic auth but Username or Password is not specified");
                }
                break;

            case "apikey":
                if (string.IsNullOrWhiteSpace(auth.ApiKey))
                {
                    warnings.Add($"Environment '{envName}': API endpoint '{endpointName}' uses ApiKey auth but ApiKey is not specified");
                }
                break;

            case "oauth2clientcredentials":
                if (string.IsNullOrWhiteSpace(auth.TokenEndpoint))
                {
                    warnings.Add($"Environment '{envName}': API endpoint '{endpointName}' uses OAuth2 auth but TokenEndpoint is not specified");
                }
                if (string.IsNullOrWhiteSpace(auth.ClientId) || string.IsNullOrWhiteSpace(auth.ClientSecret))
                {
                    warnings.Add($"Environment '{envName}': API endpoint '{endpointName}' uses OAuth2 auth but ClientId or ClientSecret is not specified");
                }
                break;

            default:
                warnings.Add($"Environment '{envName}': API endpoint '{endpointName}' has unsupported Auth Type '{auth.Type}'. Valid types: Bearer, Basic, ApiKey, OAuth2ClientCredentials");
                break;
        }
    }
}