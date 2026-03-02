using System.Collections;
using System.Collections.ObjectModel;
using System.Text;

namespace Supernova;

public sealed class EnvConfiguration
{
	private const string EncryptedEnvFileName = ".env.enc";
	private const string PlaintextEnvFileName = ".env";

	private readonly IReadOnlyDictionary<string, string> _values;

	private EnvConfiguration(IReadOnlyDictionary<string, string> values)
	{
		_values = values;
	}

	/// <summary>
	/// Loads environment configuration with the following priority:
	/// 1. Encrypted .env.enc file (if encryption key is available)
	/// 2. Plaintext .env file (for development)
	/// 3. System environment variables
	/// </summary>
	public static async Task<EnvConfiguration> LoadAsync(string? encryptionKey = null)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		// Try to get encryption key from build constant or environment
		encryptionKey ??= GetEncryptionKey();

		// Priority 1: Try encrypted .env.enc
		if (!string.IsNullOrEmpty(encryptionKey))
		{
			var loadedEncrypted = await TryLoadEncryptedAsync(values, encryptionKey);
			if (loadedEncrypted)
			{
				// Successfully loaded encrypted config - don't fall back to plaintext
				LoadFromEnvironment(values);
				return new EnvConfiguration(new ReadOnlyDictionary<string, string>(values));
			}
		}

		// Priority 2: Fall back to plaintext .env (for development)
		await TryLoadFromAppPackageAsync(values, PlaintextEnvFileName);

		// Priority 3: Environment variables (always loaded, can override)
		LoadFromEnvironment(values);

		return new EnvConfiguration(new ReadOnlyDictionary<string, string>(values));
	}

	public string? Get(string key)
	{
		return _values.TryGetValue(key, out var value) ? value : null;
	}

	public string GetOrDefault(string key, string defaultValue)
	{
		var value = Get(key);
		return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
	}

	/// <summary>
	/// Gets the encryption key from the build-time constant or environment variable.
	/// </summary>
	private static string? GetEncryptionKey()
	{
		// Try to get the compile-time embedded key via reflection
		// This avoids compilation errors when SupernovaEnvKey class doesn't exist
		var keyType = Type.GetType("Supernova.SupernovaEnvKey, Supernova");
		if (keyType is not null)
		{
			var valueField = keyType.GetField("Value", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
			if (valueField?.GetValue(null) is string embeddedKey && !string.IsNullOrEmpty(embeddedKey))
			{
				return embeddedKey;
			}
		}

		// Fall back to environment variable for development
		return Environment.GetEnvironmentVariable("SUPERNOVA_ENV_KEY");
	}

	private static async Task<bool> TryLoadEncryptedAsync(IDictionary<string, string> values, string encryptionKey)
	{
		try
		{
			await using var stream = await FileSystem.OpenAppPackageFileAsync(EncryptedEnvFileName);
			using var memoryStream = new MemoryStream();
			await stream.CopyToAsync(memoryStream);
			var encryptedData = memoryStream.ToArray();

			if (EnvEncryption.TryDecrypt(encryptedData, encryptionKey, out var plaintext) && plaintext is not null)
			{
				ParseEnvContent(values, plaintext);
				return true;
			}
		}
		catch (FileNotFoundException)
		{
			// No encrypted file available
		}

		return false;
	}

	private static async Task TryLoadFromAppPackageAsync(IDictionary<string, string> values, string fileName)
	{
		try
		{
			await using var stream = await FileSystem.OpenAppPackageFileAsync(fileName);
			using var reader = new StreamReader(stream, Encoding.UTF8);
			var content = await reader.ReadToEndAsync();
			ParseEnvContent(values, content);
		}
		catch (FileNotFoundException)
		{
			// No .env file in app package; fallback to environment variables.
		}
	}

	private static void ParseEnvContent(IDictionary<string, string> values, string content)
	{
		using var reader = new StringReader(content);
		while (reader.ReadLine() is { } line)
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			line = line.Trim();
			if (line.StartsWith('#'))
			{
				continue;
			}

			var eqIndex = line.IndexOf('=');
			if (eqIndex <= 0)
			{
				continue;
			}

			var key = line[..eqIndex].Trim();
			var value = line[(eqIndex + 1)..].Trim();
			if (value.Length >= 2)
			{
				if ((value.StartsWith('"') && value.EndsWith('"'))
					|| (value.StartsWith('\'') && value.EndsWith('\'')))
				{
					value = value[1..^1];
				}
			}

			if (!string.IsNullOrWhiteSpace(key))
			{
				values[key] = value;
			}
		}
	}

	private static void LoadFromEnvironment(IDictionary<string, string> values)
	{
		foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
		{
			if (entry.Key is not string key || string.IsNullOrWhiteSpace(key))
			{
				continue;
			}

			if (entry.Value is string value && !string.IsNullOrWhiteSpace(value))
			{
				values[key] = value;
			}
		}
	}
}