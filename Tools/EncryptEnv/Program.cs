using System.Security.Cryptography;
using System.Text;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: EncryptEnv <input.env> <output.enc>");
    return 1;
}

string inputPath = args[0];
string outputPath = args[1];

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input file not found: {inputPath}");
    return 1;
}

byte[] plaintext = File.ReadAllBytes(inputPath);

byte[] salt = Encoding.UTF8.GetBytes("SupernovaSaltVal"); // 16 bytes
byte[] key;
using (var pbkdf2 = new Rfc2898DeriveBytes("SupernovaSpacetimeEncryptionKey", salt, 100_000, HashAlgorithmName.SHA256))
{
    key = pbkdf2.GetBytes(32);
}

using var aes = Aes.Create();
aes.Key = key;
aes.Mode = CipherMode.CBC;
aes.Padding = PaddingMode.PKCS7;
aes.GenerateIV();

byte[] ciphertext;
using (var encryptor = aes.CreateEncryptor())
{
    ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
using (var fs = File.Create(outputPath))
{
    fs.Write(aes.IV);
    fs.Write(ciphertext);
}

Console.WriteLine($"Encrypted {inputPath} -> {outputPath} ({plaintext.Length} -> {aes.IV.Length + ciphertext.Length} bytes)");
return 0;
