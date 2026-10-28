using System.Security.Cryptography;

namespace ChatApp.Server.Security;

/// <summary>
/// 비밀번호 해시 (PBKDF2-SHA256 + 사용자별 Salt, NF-09).
/// DB에는 원래 비밀번호가 아니라 해시와 Salt만 저장한다.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000; // 반복 횟수가 클수록 무차별 대입이 느려진다
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static (string Hash, string Salt) Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string storedHash, string storedSalt)
    {
        byte[] salt = Convert.FromBase64String(storedSalt);
        byte[] expected = Convert.FromBase64String(storedHash);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);

        // 일반 비교(==)는 앞에서부터 다른 글자를 만나면 바로 끝나서 시간 차이로 정보가 샌다.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
