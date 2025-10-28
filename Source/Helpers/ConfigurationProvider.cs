using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Trignis.MicrosoftSQL.Services;
using Serilog;

namespace Trignis.MicrosoftSQL.Helpers
{
    public class ConfigurationProvider : JsonConfigurationProvider
    {
        private readonly EncryptionService _encryptionService;

        public ConfigurationProvider(JsonConfigurationSource source, EncryptionService encryptionService)
            : base(source)
        {
            _encryptionService = encryptionService;
        }

    public override void Load()
    {
        var fileProvider = Source.FileProvider ?? new PhysicalFileProvider(Directory.GetCurrentDirectory());
        var path = Source.Path ?? throw new InvalidOperationException("Path is required");
        var fileInfo = fileProvider.GetFileInfo(path);
        if (!fileInfo.Exists)
        {
            if (!Source.Optional)
            {
                throw new FileNotFoundException($"The configuration file '{path}' was not found and is not optional.");
            }
            return;
        }

        using var stream = fileInfo.CreateReadStream();
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        // Parse JSON
        var jsonNode = JsonNode.Parse(content);
        if (jsonNode is JsonObject jsonObject)
        {
            // Decrypt ConnectionStrings
            DecryptJsonSection(jsonObject, "ConnectionStrings");
            
            if (jsonObject.TryGetPropertyValue("ChangeTracking", out var changeTrackingNode) && changeTrackingNode is JsonObject ctObject)
            {
                // Decrypt legacy ApiAuth
                DecryptJsonSection(ctObject, "ApiAuth");
                
                // Decrypt ApiEndpoints
                if (ctObject.TryGetPropertyValue("ApiEndpoints", out var ApiEndpointsNode) && ApiEndpointsNode is JsonArray aeArray)
                {
                    foreach (var endpoint in aeArray)
                    {
                        if (endpoint is JsonObject epObj)
                        {
                            // Decrypt Auth section
                            if (epObj.TryGetPropertyValue("Auth", out var authNode) && authNode is JsonObject authObj)
                            {
                                DecryptAuthObject(authObj);
                            }
                            
                            // Decrypt MessageQueue section
                            if (epObj.TryGetPropertyValue("MessageQueue", out var mqNode) && mqNode is JsonObject mqObj)
                            {
                                DecryptMessageQueueObject(mqObj);
                            }
                        }
                    }
                }
            }

            // Serialize back
            content = jsonObject.ToJsonString();
        }

        // Load from modified content
        using var memoryStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        Load(memoryStream);
    }

    private void DecryptAuthObject(JsonObject authObj)
    {
        // Decrypt sensitive Auth properties
        var sensitiveProps = new[] { "Token", "Password", "ApiKey", "ClientSecret", "ClientId" };
        
        foreach (var propName in sensitiveProps)
        {
            if (authObj.TryGetPropertyValue(propName, out var propValue) && 
                propValue is JsonValue jsonValue && 
                jsonValue.TryGetValue(out string? strValue) && 
                strValue != null && 
                _encryptionService.IsEncrypted(strValue))
            {
                try
                {
                    authObj[propName] = _encryptionService.Decrypt(strValue);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to decrypt ApiEndpoints.Auth.{Key}, configuration may be corrupted", propName);
                    throw;
                }
            }
        }
    }

    private void DecryptMessageQueueObject(JsonObject mqObj)
    {
        // Decrypt sensitive MessageQueue properties
        var sensitiveProps = new[] { "Password", "ConnectionString", "SecretAccessKey", "AccessKeyId" };
        
        foreach (var propName in sensitiveProps)
        {
            if (mqObj.TryGetPropertyValue(propName, out var propValue) && 
                propValue is JsonValue jsonValue && 
                jsonValue.TryGetValue(out string? strValue) && 
                strValue != null && 
                _encryptionService.IsEncrypted(strValue))
            {
                try
                {
                    mqObj[propName] = _encryptionService.Decrypt(strValue);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to decrypt ApiEndpoints.MessageQueue.{Key}, configuration may be corrupted", propName);
                    throw;
                }
            }
        }
    }

        private void DecryptJsonSection(JsonObject jsonObject, string sectionName)
        {
            if (jsonObject.TryGetPropertyValue(sectionName, out var sectionNode))
            {
                if (sectionNode is JsonObject sectionObj)
                {
                    foreach (var prop in sectionObj)
                    {
                        if (prop.Value is JsonValue jsonValue && jsonValue.TryGetValue(out string? strValue) && strValue != null && _encryptionService.IsEncrypted(strValue))
                        {
                            try
                            {
                                sectionObj[prop.Key] = _encryptionService.Decrypt(strValue);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Failed to decrypt {Section}.{Key}, configuration may be corrupted", sectionName, prop.Key);
                                throw; // Re-throw to fail fast
                            }
                        }
                    }
                }
                else if (sectionNode is JsonValue jsonValue && jsonValue.TryGetValue(out string? strValue) && strValue != null && _encryptionService.IsEncrypted(strValue))
                {
                    try
                    {
                        jsonObject[sectionName] = JsonNode.Parse(_encryptionService.Decrypt(strValue));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to decrypt {Section}, configuration may be corrupted", sectionName);
                        throw;
                    }
                }
            }
        }
    }

    public class EncryptedJsonConfigurationSource : JsonConfigurationSource
    {
        private readonly EncryptionService _encryptionService;

        public EncryptedJsonConfigurationSource(EncryptionService encryptionService)
        {
            _encryptionService = encryptionService;
        }

        public override IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            EnsureDefaults(builder);
            return new ConfigurationProvider(this, _encryptionService);
        }
    }

    public static class EncryptedJsonConfigurationExtensions
    {
        public static IConfigurationBuilder AddEncryptedJsonFile(this IConfigurationBuilder builder, string path, EncryptionService encryptionService, bool optional = true, bool reloadOnChange = false)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Invalid file path", nameof(path));
            }

            var source = new EncryptedJsonConfigurationSource(encryptionService)
            {
                FileProvider = builder.GetFileProvider(),
                Path = path,
                Optional = optional,
                ReloadOnChange = reloadOnChange
            };

            builder.Add(source);
            return builder;
        }
    }
}