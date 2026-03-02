using System.Security.Cryptography;
using System.Text;

namespace Supernova;

/// <summary>
/// Handles encryption and decryption of .env files using AES-256-GCM.
/// The encryption key is derived from a passphrase using PBKDF2.
/// </summary>
public static class EnvEncryption
{
	private const int KeySizeBytes = 32; // 256 bits
	private const int NonceSizeBytes = 12; // 96 bits for GCM
	private const int TagSizeBytes = 16; // 128 bits
	private const int SaltSizeBytes = 16;
	private const int Pbkdf2Iterations = 100_000;

	/// <summary>
	/// Encrypts plaintext using AES-256-GCM with a key derived from the passphrase.
	/// Output format: [salt (16 bytes)][nonce (12 bytes)][tag (16 bytes)][ciphertext]
	/// </summary>
	public static byte[] Encrypt(string plaintext, string passphrase)
	{
		if (string.IsNullOrEmpty(passphrase))
		{
			throw new ArgumentException("Passphrase cannot be empty", nameof(passphrase));
		}

		var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

		// Generate random salt and nonce
		var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
		var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);

		// Derive key from passphrase using PBKDF2
		var key = DeriveKey(passphrase, salt);

		// Encrypt using AES-GCM
		var ciphertext = new byte[plaintextBytes.Length];
		var tag = new byte[TagSizeBytes];

		using var aesGcm = new AesGcm(key, TagSizeBytes);
		aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

		// Combine: salt + nonce + tag + ciphertext
		var result = new byte[SaltSizeBytes + NonceSizeBytes + TagSizeBytes + ciphertext.Length];
		Buffer.BlockCopy(salt, 0, result, 0, SaltSizeBytes);
		Buffer.BlockCopy(nonce, 0, result, SaltSizeBytes, NonceSizeBytes);
		Buffer.BlockCopy(tag, 0, result, SaltSizeBytes + NonceSizeBytes, TagSizeBytes);
		Buffer.BlockCopy(ciphertext, 0, result, SaltSizeBytes + NonceSizeBytes + TagSizeBytes, ciphertext.Length);

		return result;
	}

	/// <summary>
	/// Decrypts ciphertext that was encrypted with Encrypt().
	/// </summary>
	public static string Decrypt(byte[] encryptedData, string passphrase)
	{
		if (string.IsNullOrEmpty(passphrase))
		{
			throw new ArgumentException("Passphrase cannot be empty", nameof(passphrase));
		}

		if (encryptedData.Length < SaltSizeBytes + NonceSizeBytes + TagSizeBytes)
		{
			throw new ArgumentException("Invalid encrypted data format", nameof(encryptedData));
		}

		// Extract components
		var salt = new byte[SaltSizeBytes];
		var nonce = new byte[NonceSizeBytes];
		var tag = new byte[TagSizeBytes];
		var ciphertextLength = encryptedData.Length - SaltSizeBytes - NonceSizeBytes - TagSizeBytes;
		var ciphertext = new byte[ciphertextLength];

		Buffer.BlockCopy(encryptedData, 0, salt, 0, SaltSizeBytes);
		Buffer.BlockCopy(encryptedData, SaltSizeBytes, nonce, 0, NonceSizeBytes);
		Buffer.BlockCopy(encryptedData, SaltSizeBytes + NonceSizeBytes, tag, 0, TagSizeBytes);
		Buffer.BlockCopy(encryptedData, SaltSizeBytes + NonceSizeBytes + TagSizeBytes, ciphertext, 0, ciphertextLength);

		// Derive key from passphrase
		var key = DeriveKey(passphrase, salt);

		// Decrypt using AES-GCM
		var plaintext = new byte[ciphertextLength];

		using var aesGcm = new AesGcm(key, TagSizeBytes);
		aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

		return Encoding.UTF8.GetString(plaintext);
	}

	/// <summary>
	/// Tries to decrypt the data, returning false if decryption fails (wrong key or corrupted data).
	/// </summary>
	public static bool TryDecrypt(byte[] encryptedData, string passphrase, out string? plaintext)
	{
		plaintext = null;

		try
		{
			plaintext = Decrypt(encryptedData, passphrase);
			return true;
		}
		catch (CryptographicException)
		{
			return false;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	private static byte[] DeriveKey(string passphrase, byte[] salt)
	{
		return Rfc2898DeriveBytes.Pbkdf2(
			passphrase,
			salt,
			Pbkdf2Iterations,
			HashAlgorithmName.SHA256,
			KeySizeBytes);
	}
}
