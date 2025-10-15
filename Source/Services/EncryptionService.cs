using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Serilog;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trignis.MicrosoftSQL.Services
{
    public class EncryptionService
    {
        private const string EncryptedHeader = "PWENC:";
        private const string PrivateKeyFileName = "key_b.pem";
        private const string PublicKeyFileName = "key_a.pem";

        private readonly string _certsPath;
        private string _currentPublicKeyPem = string.Empty;

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
            InitializeKeyPair();
        }

        private static string FindProjectRoot(string startPath)
        {
            var current = new DirectoryInfo(startPath);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "environments")))
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
            var publicKeyPath = Path.Combine(_certsPath, PublicKeyFileName);

            if (!File.Exists(privateKeyPath))
            {
                Log.Debug("Private key not found. Generating new keypair...");

                // Generate new keypair
                using var rsa = RSA.Create(2048);
                var privateKeyPem = ExportPrivateKeyPem(rsa);
                var publicKeyPem = ExportPublicKeyPem(rsa);

                // Save private key
                File.WriteAllText(privateKeyPath, privateKeyPem);
                Log.Debug("Private key saved to: {PrivateKeyPath}", privateKeyPath);

                // Save public key
                File.WriteAllText(publicKeyPath, publicKeyPem);
                Log.Debug("Public key saved to: {PublicKeyPath}", publicKeyPath);

                // Update current public key
                _currentPublicKeyPem = publicKeyPem;
                Log.Debug("Generated new keypair.");
            }
            else
            {
                // Private key exists, derive public key from it
                try
                {
                    var privateKeyPem = File.ReadAllText(privateKeyPath);
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(privateKeyPem);
                    var derivedPublicKeyPem = ExportPublicKeyPem(rsa);
                    _currentPublicKeyPem = derivedPublicKeyPem;

                    // Also save/update public key file
                    File.WriteAllText(publicKeyPath, derivedPublicKeyPem);

                    Log.Debug("Loaded existing private key and derived public key.");
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to load private key: {Error}", ex.Message);
                    throw;
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

            var privateKey = File.ReadAllText(privateKeyPath);
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
                        if (jsonObject.TryGetPropertyValue("ConnectionStrings", out var csNode) && csNode is JsonObject)
                        {
                            needsEncrypt = true;
                        }
                        JsonNode? ctNode = null;
                        JsonNode? aaNode = null;
                        if (jsonObject.TryGetPropertyValue("ChangeTracking", out ctNode) && ctNode is JsonObject ctObject)
                        {
                            if (ctObject.TryGetPropertyValue("ApiAuth", out aaNode) && aaNode is JsonObject)
                            {
                                needsEncrypt = true;
                            }
                        }

                        if (needsEncrypt)
                        {
                            // Encrypt the sections
                            if (jsonObject.TryGetPropertyValue("ConnectionStrings", out csNode) && csNode is JsonObject csObj)
                            {
                                foreach (var prop in csObj)
                                {
                                    if (prop.Value is JsonValue jv && jv.TryGetValue(out string? val) && val != null && !IsEncrypted(val))
                                    {
                                        csObj[prop.Key] = Encrypt(val);
                                    }
                                }
                            }
                            if (ctNode is JsonObject ctObj)
                            {
                                if (aaNode is JsonObject aaObj)
                                {
                                    foreach (var prop in aaObj)
                                    {
                                        if (prop.Value is JsonValue jv && jv.TryGetValue(out string? val) && val != null && !IsEncrypted(val))
                                        {
                                            aaObj[prop.Key] = Encrypt(val);
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
    }
}