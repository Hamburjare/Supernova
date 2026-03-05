using System.Security.Cryptography;
using System.Text;
using Microsoft.Maui.Storage;

namespace Supernova.Services;

public static class EnvConfig
{
    private static Dictionary<string, string>? _values;

    public static async Task<Dictionary<string, string>> LoadAsync()
    {
        if (_values != null) return _values;
        _values = new Dictionary<string, string>();

        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync("env.enc");
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            byte[] encrypted = ms.ToArray();

            if (encrypted.Length > 16)
            {
                byte[] iv = encrypted[..16];
                byte[] ciphertext = encrypted[16..];

                byte[] salt = Encoding.UTF8.GetBytes("SupernovaSaltVal");
                byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                    "SupernovaSpacetimeEncryptionKey"u8, salt, 100_000, HashAlgorithmName.SHA256, 32);

                using var aes = Aes.Create();
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                byte[] plaintext;
                using (var decryptor = aes.CreateDecryptor())
                {
                    plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                }

                ParseEnv(Encoding.UTF8.GetString(plaintext));
            }
        }
        catch
        {
            // encrypted env not available — fall through to env vars / defaults
        }

        // Environment variables override encrypted values
        var uri = Environment.GetEnvironmentVariable("SPACETIMEDB_SERVER_URI");
        if (!string.IsNullOrEmpty(uri)) _values["SPACETIMEDB_SERVER_URI"] = uri;

        var db = Environment.GetEnvironmentVariable("SPACETIMEDB_DATABASE");
        if (!string.IsNullOrEmpty(db)) _values["SPACETIMEDB_DATABASE"] = db;

        // Sensible defaults
        _values.TryAdd("SPACETIMEDB_SERVER_URI", "http://localhost:3000");
        _values.TryAdd("SPACETIMEDB_DATABASE", "supernova");

        return _values;
    }

    private static void ParseEnv(string content)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;
            int eq = trimmed.IndexOf('=');
            if (eq > 0)
            {
                string k = trimmed[..eq].Trim();
                string v = trimmed[(eq + 1)..].Trim();
                _values![k] = v;
            }
        }
    }

    public static string GetServerUri() =>
        _values?.GetValueOrDefault("SPACETIMEDB_SERVER_URI") ?? "http://localhost:3000";

    public static string GetDatabase() =>
        _values?.GetValueOrDefault("SPACETIMEDB_DATABASE") ?? "supernova";
}
