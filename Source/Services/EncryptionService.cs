using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Serilog;
using System.Text.Json;
using System.Text.Json.Nodes;
using Trignis.Helpers;

namespace Trignis.Services
{
    public class EncryptionService
    {
        private const string EncryptedHeader = "PWENC:";
        private const string PrivateKeyFileName = "recovery.baklz4";
        private readonly string _certsPath;
        private string _currentPublicKeyPem = string.Empty;
        
        // Fallback key - only used if no environment variable is set
        private const string _fallbackKey = "$XTSI5gTEf1wq3G2uOdWTsFUrgZ6mkCBGrdr0fsRTegXwis68HxGEoCsIBpgbPl5swwY9BQ0qiXG6CaeEPJzp3SPyGebl0ZyHL3jLACKIuSw7G1ufAZ5XATtetKatH0sr#";
        
        private readonly string _encryptionKey;

        public EncryptionService(string rootPath)
        {
            // Find the project root by searching upwards for Trignis.csproj
            var projectRoot = FindProjectRoot(rootPath);
            _certsPath = Path.Combine(projectRoot, ".core");
            Directory.CreateDirectory(_certsPath);
            if (OperatingSystem.IsWindows())
            {
                var dirInfo = new DirectoryInfo(_certsPath);
                if ((dirInfo.Attributes & FileAttributes.Hidden) == 0)
                {
                    dirInfo.Attributes |= FileAttributes.Hidden;
                }
            }

            // Load encryption key from environment variable or .env file
            _encryptionKey = LoadEncryptionKey();
            
            InitializeKeyPair();
        }

        private string LoadEncryptionKey()
        {
            // Priority 1: Check Windows environment variable
            var envKey = Environment.GetEnvironmentVariable("TRIGNIS_ENCRYPTION_KEY", EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                Log.Debug("Using encryption key from Windows system environment variable");
                return envKey;
            }

            // Priority 2: Check process environment variable
            envKey = Environment.GetEnvironmentVariable("TRIGNIS_ENCRYPTION_KEY", EnvironmentVariableTarget.Process);
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                Log.Debug("Using encryption key from process environment variable");
                return envKey;
            }

            // .env is not read here. Program.cs loads it into the process environment first

            // Priority 3: Fallback to hardcoded key (with warning)
            Log.Warning("No TRIGNIS_ENCRYPTION_KEY found in environment or .env file. Using fallback key. " +
                        "For production, set TRIGNIS_ENCRYPTION_KEY environment variable or create .env file.");
            
            return _fallbackKey;
        }

        private static string FindProjectRoot(string startPath)
        {
            var current = new DirectoryInfo(startPath);
            while (current != null)
            {
                // Look for environments folder or .env file
                if (Directory.Exists(Path.Combine(current.FullName, "environments")) ||
                    File.Exists(Path.Combine(current.FullName, ".env")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
            // Fallback to startPath if not found
            return startPath;
        }

        private void InitializeKeyPair()
        {
            var privateKeyPath = Path.Combine(_certsPath, PrivateKeyFileName);

            // public key is always derived from the private key, never read from disk
            if (!File.Exists(privateKeyPath))
            {
                Log.Debug("Private key not found. Generating new keypair...");

                using var rsa = RSA.Create(2048);
                var privateKeyPem = ExportPrivateKeyPem(rsa);

                File.WriteAllText(privateKeyPath, EncryptPrivateKey(privateKeyPem));
                Log.Debug("Private key saved to: {PrivateKeyPath}", privateKeyPath);

                _currentPublicKeyPem = ExportPublicKeyPem(rsa);
                Log.Information("⚠ Generated new RSA keypair for encryption");
            }
            else
            {
                // Private key exists, derive public key from it
                try
                {
                    var encrypted = File.ReadAllText(privateKeyPath);
                    var privateKeyPem = DecryptPrivateKey(encrypted);
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(privateKeyPem);
                    _currentPublicKeyPem = ExportPublicKeyPem(rsa);

                    Log.Debug("Loaded existing private key and derived public key");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to load private key. The encryption key may have changed.");
                    throw new InvalidOperationException(
                        "Failed to decrypt private key. If you changed TRIGNIS_ENCRYPTION_KEY, " +
                        "you must delete the .core folder to regenerate keys.", ex);
                }
            }
        }

        public string Encrypt(string plainText)
        {
            return Encrypt(plainText, _currentPublicKeyPem);
        }

        public string Decrypt(string encryptedContent)
        {
            var privateKeyPath = Path.Combine(_certsPath, PrivateKeyFileName);
            if (!File.Exists(privateKeyPath))
            {
                throw new InvalidOperationException("Private key not found. Required for decryption.");
            }

            var encrypted = File.ReadAllText(privateKeyPath);
            var privateKey = DecryptPrivateKey(encrypted);
            return Decrypt(encryptedContent, privateKey);
        }

        public bool IsEncrypted(string content)
        {
            return content.StartsWith(EncryptedHeader);
        }

        // AES + RSA hybrid encryption
        private static string Encrypt(string plainText, string publicKeyPem)
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.GenerateKey();
            aes.GenerateIV();

            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] cipherBytes;
            using (var ms = new MemoryStream())
            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cs.Write(plainBytes, 0, plainBytes.Length);
                cs.FlushFinalBlock();
                cipherBytes = ms.ToArray();
            }

            var keyIv = new byte[aes.Key.Length + aes.IV.Length];
            Buffer.BlockCopy(aes.Key, 0, keyIv, 0, aes.Key.Length);
            Buffer.BlockCopy(aes.IV, 0, keyIv, aes.Key.Length, aes.IV.Length);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            var encryptedKeyIv = rsa.Encrypt(keyIv, RSAEncryptionPadding.OaepSHA256);

            return EncryptedHeader + Convert.ToBase64String(encryptedKeyIv) + "::" + Convert.ToBase64String(cipherBytes);
        }

        private static string Decrypt(string encryptedContent, string privateKeyPem)
        {
            if (!encryptedContent.StartsWith(EncryptedHeader))
                throw new InvalidOperationException("Content is not encrypted");
            var payload = encryptedContent.Substring(EncryptedHeader.Length);
            var parts = payload.Split(new[] { "::" }, StringSplitOptions.None);
            if (parts.Length != 2)
                throw new FormatException("Invalid encrypted format");

            var encryptedKeyIv = Convert.FromBase64String(parts[0]);
            var cipherBytes = Convert.FromBase64String(parts[1]);

            var sanitizedPem = string.Join("\n",
                privateKeyPem.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim()));

            using var rsa = RSA.Create();
            rsa.ImportFromPem(sanitizedPem);
            var keyIv = rsa.Decrypt(encryptedKeyIv, RSAEncryptionPadding.OaepSHA256);

            var key = new byte[32];
            var iv = new byte[16];
            Buffer.BlockCopy(keyIv, 0, key, 0, 32);
            Buffer.BlockCopy(keyIv, 32, iv, 0, 16);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            using var ms = new MemoryStream(cipherBytes);
            using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var sr = new StreamReader(cs, Encoding.UTF8);
            return sr.ReadToEnd();
        }

        private static string ExportPrivateKeyPem(RSA rsa)
        {
            var builder = new StringBuilder();
            builder.AppendLine("-----BEGIN PRIVATE KEY-----");
            builder.AppendLine(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey(), Base64FormattingOptions.InsertLineBreaks));
            builder.AppendLine("-----END PRIVATE KEY-----");
            return builder.ToString();
        }

        private static string ExportPublicKeyPem(RSA rsa)
        {
            var builder = new StringBuilder();
            builder.AppendLine("-----BEGIN PUBLIC KEY-----");
            builder.AppendLine(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo(), Base64FormattingOptions.InsertLineBreaks));
            builder.AppendLine("-----END PUBLIC KEY-----");
            return builder.ToString();
        }

        private string EncryptPrivateKey(string pem)
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(_encryptionKey.PadRight(32).Substring(0, 32));
            aes.GenerateIV();
            var iv = aes.IV;
            using var ms = new MemoryStream();
            ms.Write(iv, 0, iv.Length);
            using var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write);
            var bytes = Encoding.UTF8.GetBytes(pem);
            cs.Write(bytes, 0, bytes.Length);
            cs.FlushFinalBlock();
            return Convert.ToBase64String(ms.ToArray());
        }

        private string DecryptPrivateKey(string encrypted)
        {
            var bytes = Convert.FromBase64String(encrypted);
            using var ms = new MemoryStream(bytes);
            var iv = new byte[16];
            ms.Read(iv, 0, 16);
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(_encryptionKey.PadRight(32).Substring(0, 32));
            aes.IV = iv;
            using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var sr = new StreamReader(cs);
            return sr.ReadToEnd();
        }

        public void EncryptConfigFiles()
        {
            var envDir = Path.Combine(Path.GetDirectoryName(_certsPath)!, "environments");
            if (!Directory.Exists(envDir))
                return;

            var files = Directory.GetFiles(envDir, "*.json", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var jsonNode = JsonNode.Parse(content);
                    if (jsonNode is JsonObject jsonObject)
                    {
                        bool needsEncrypt = false;

                        // Encrypt ConnectionStrings
                        if (jsonObject.TryGetPropertyValue("ConnectionStrings", out var csNode) && csNode is JsonObject)
                        {
                            needsEncrypt = true;
                        }

                        JsonNode? ctNode = null;
                        if (jsonObject.TryGetPropertyValue("ChangeTracking", out ctNode) && ctNode is JsonObject ctObject)
                        {
                            // Check ApiAuth (legacy)
                            if (ctObject.TryGetPropertyValue("ApiAuth", out var aaNode) && aaNode is JsonObject)
                            {
                                needsEncrypt = true;
                            }

                            // Check if ApiEndpoints exist and have Auth or MessageQueue to encrypt
                            if (ctObject.TryGetPropertyValue("ApiEndpoints", out var aeNode) && aeNode is JsonArray aeArray)
                            {
                                foreach (var endpoint in aeArray)
                                {
                                    if (endpoint is JsonObject epObj)
                                    {
                                        if (epObj.TryGetPropertyValue("Auth", out var authNode) && authNode is JsonObject)
                                        {
                                            needsEncrypt = true;
                                        }
                                        if (epObj.TryGetPropertyValue("MessageQueue", out var mqNode) && mqNode is JsonObject)
                                        {
                                            needsEncrypt = true;
                                        }
                                    }
                                }
                            }
                        }

                        if (needsEncrypt)
                        {
                            // Encrypt ConnectionStrings
                            if (jsonObject.TryGetPropertyValue("ConnectionStrings", out csNode) && csNode is JsonObject csObj)
                            {
                                JsonSecrets.MapProps(csObj, null, EncryptIfPlain);
                            }

                            // Encrypt ChangeTracking sections
                            if (ctNode is JsonObject ctObj)
                            {
                                // Encrypt legacy ApiAuth
                                if (ctObj.TryGetPropertyValue("ApiAuth", out var aaNode) && aaNode is JsonObject aaObj)
                                {
                                    JsonSecrets.MapProps(aaObj, null, EncryptIfPlain);
                                }

                                // Encrypt ApiEndpoints
                                if (ctObj.TryGetPropertyValue("ApiEndpoints", out var innerAeNode) && innerAeNode is JsonArray aeArray)
                                {
                                    foreach (var endpoint in aeArray)
                                    {
                                        if (endpoint is JsonObject epObj)
                                        {
                                            if (epObj.TryGetPropertyValue("Auth", out var authNode) && authNode is JsonObject authObj)
                                            {
                                                JsonSecrets.MapProps(authObj, JsonSecrets.AuthProps, EncryptIfPlain);
                                            }

                                            if (epObj.TryGetPropertyValue("MessageQueue", out var mqNode) && mqNode is JsonObject mqObj)
                                            {
                                                JsonSecrets.MapProps(mqObj, JsonSecrets.MessageQueueProps, EncryptIfPlain);
                                            }
                                        }
                                    }
                                }
                            }

                            var options = new JsonSerializerOptions { WriteIndented = true };
                            var encryptedContent = JsonSerializer.Serialize(jsonObject, options);
                            File.WriteAllText(file, encryptedContent);
                            Log.Debug("Encrypted config file: {File}", file);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to encrypt config file: {File}", file);
                }
            }
        }

        /// <summary>Returns the encrypted value, or null when it is already encrypted.</summary>
        private string? EncryptIfPlain(string key, string value) => IsEncrypted(value) ? null : Encrypt(value);
    }
}