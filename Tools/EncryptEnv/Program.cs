using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Command-line tool to encrypt .env files for secure distribution.
/// Usage: dotnet run --project Tools/EncryptEnv -- <input.env> <output.env.enc> <passphrase>
/// </summary>
class Program
{
	private const int KeySizeBytes = 32;
	private const int NonceSizeBytes = 12;
	private const int TagSizeBytes = 16;
	private const int SaltSizeBytes = 16;
	private const int Pbkdf2Iterations = 100_000;

	static int Main(string[] args)
	{
		if (args.Length < 2)
		{
			Console.WriteLine("Usage: EncryptEnv <input.env> <output.env.enc> [passphrase]");
			Console.WriteLine("  If passphrase is not provided, reads from SUPERNOVA_ENV_KEY environment variable.");
			return 1;
		}

		var inputPath = args[0];
		var outputPath = args[1];
		var passphrase = args.Length >= 3 ? args[2] : Environment.GetEnvironmentVariable("SUPERNOVA_ENV_KEY");

		if (string.IsNullOrEmpty(passphrase))
		{
			Console.Error.WriteLine("Error: No passphrase provided and SUPERNOVA_ENV_KEY environment variable is not set.");
			return 1;
		}

		if (!File.Exists(inputPath))
		{
			Console.Error.WriteLine($"Error: Input file not found: {inputPath}");
			return 1;
		}

		try
		{
			var plaintext = File.ReadAllText(inputPath, Encoding.UTF8);
			var encrypted = Encrypt(plaintext, passphrase);
			File.WriteAllBytes(outputPath, encrypted);

			Console.WriteLine($"Successfully encrypted {inputPath} -> {outputPath}");
			Console.WriteLine($"Encrypted size: {encrypted.Length} bytes");
			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Error: {ex.Message}");
			return 1;
		}
	}

	private static byte[] Encrypt(string plaintext, string passphrase)
	{
		var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
		var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
		var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
		var key = DeriveKey(passphrase, salt);

		var ciphertext = new byte[plaintextBytes.Length];
		var tag = new byte[TagSizeBytes];

		using var aesGcm = new AesGcm(key, TagSizeBytes);
		aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

		var result = new byte[SaltSizeBytes + NonceSizeBytes + TagSizeBytes + ciphertext.Length];
		Buffer.BlockCopy(salt, 0, result, 0, SaltSizeBytes);
		Buffer.BlockCopy(nonce, 0, result, SaltSizeBytes, NonceSizeBytes);
		Buffer.BlockCopy(tag, 0, result, SaltSizeBytes + NonceSizeBytes, TagSizeBytes);
		Buffer.BlockCopy(ciphertext, 0, result, SaltSizeBytes + NonceSizeBytes + TagSizeBytes, ciphertext.Length);

		return result;
	}

	private static byte[] DeriveKey(string passphrase, byte[] salt)
	{
		using var pbkdf2 = new Rfc2898DeriveBytes(
			passphrase,
			salt,
			Pbkdf2Iterations,
			HashAlgorithmName.SHA256);

		return pbkdf2.GetBytes(KeySizeBytes);
	}
}
