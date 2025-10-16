using System.Security.Cryptography;

namespace WrapperApi.Helpers
{
	public static class ClientSecretHelper
	{
		private const int SaltSize = 16; // bytes
		private const int KeySize = 32;  // bytes
		private const int Iterations = 100_000;

		public static (byte[] hash, byte[] salt) CreateSecretHash(string secret)
		{
			using var rng = RandomNumberGenerator.Create();
			var salt = new byte[SaltSize];
			rng.GetBytes(salt);

			using var pbkdf2 = new Rfc2898DeriveBytes(secret, salt, Iterations, HashAlgorithmName.SHA256);
			var hash = pbkdf2.GetBytes(KeySize);
			return (hash, salt);
		}

		public static bool VerifySecret(string secret, byte[] storedHash, byte[] storedSalt)
		{
			using var pbkdf2 = new Rfc2898DeriveBytes(secret, storedSalt, Iterations, HashAlgorithmName.SHA256);
			var computed = pbkdf2.GetBytes(KeySize);
			return CryptographicOperations.FixedTimeEquals(computed, storedHash);
		}

		public static string GeneratePlainSecret(int length = 32)
		{
			var bytes = new byte[length];
			using var rng = RandomNumberGenerator.Create();
			rng.GetBytes(bytes);
			return Convert.ToBase64String(bytes); // return to operator once
		}
	}
}

