using System.Collections;
using System.Collections.ObjectModel;
using System.Text;

namespace Supernova;

public sealed class EnvConfiguration
{
	private readonly IReadOnlyDictionary<string, string> _values;

	private EnvConfiguration(IReadOnlyDictionary<string, string> values)
	{
		_values = values;
	}

	public static async Task<EnvConfiguration> LoadAsync(string appPackageFileName = ".env")
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		await TryLoadFromAppPackageAsync(values, appPackageFileName);
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

	private static async Task TryLoadFromAppPackageAsync(IDictionary<string, string> values, string fileName)
	{
		try
		{
			await using var stream = await FileSystem.OpenAppPackageFileAsync(fileName);
			using var reader = new StreamReader(stream, Encoding.UTF8);
			while (true)
			{
				var line = await reader.ReadLineAsync();
				if (line is null)
				{
					break;
				}

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
		catch (FileNotFoundException)
		{
			// No .env file in app package; fallback to environment variables.
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