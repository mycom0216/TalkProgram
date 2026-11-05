using System.Security.Cryptography;

namespace ChatApp.Server.Security;

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   비밀번호를 DB에 "그대로" 저장하지 않고, 사용자마다 다른 소금(Salt)을 섞어 PBKDF2-SHA256으로 일부러 느리게 계산한 값(해시)만 저장한다. 로그인 때는 같은 계산을 다시 해서 비교한다.
//
// ■ 흐름
//   가입/변경: Hash(비밀번호) → 랜덤 Salt 16바이트 생성 → PBKDF2(60만 번 반복) → 32바이트 해시 → "pbkdf2-sha256$60만$해시"와 Salt를 돌려줌 → AuthService가 DB에 저장
//   로그인:    Verify(입력, 저장된 해시, 저장된 Salt) → 해시 앞에 적힌 횟수(접두사가 없으면 옛 10만 번)로 다시 계산 → 저장값과 "고정 시간 비교"
//   업그레이드: 로그인에 성공했는데 NeedsRehash가 true(옛 해시)면 AuthService가 같은 비밀번호로 새 해시를 만들어 DB에 다시 저장한다
//   (호출하는 곳: AuthService의 가입·로그인·비밀번호 변경·임시 비밀번호 발급)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 해시 vs 암호화(복호화 불가 vs 가능) ............. "hash vs encryption password storage"
//   - Salt(소금): 같은 비밀번호도 다른 해시가 되게 ..... "password salt rainbow table"
//   - PBKDF2와 반복 횟수(느리게 만들기) ............... "PBKDF2 iterations Rfc2898DeriveBytes.Pbkdf2"
//   - 고정 시간 비교(타이밍 공격 방어) ................. "CryptographicOperations.FixedTimeEquals timing attack"
//   - 암호용 난수 vs 일반 난수 ........................ "RandomNumberGenerator vs Random"
//
// ■ 면접 예상 질문과 답
//   Q1. 왜 비밀번호를 암호화하지 않고 해시로 저장했나요?
//       A. 암호화는 키만 있으면 원래 비밀번호로 되돌릴 수 있어서, DB와 키가 함께 유출되면 전부 노출됩니다. 해시는 되돌릴 수 없고, 로그인 때 "같은 계산을 해서 같은지"만 보면 되므로 원문을 서버가 알 필요가 없습니다.
//   Q2. Salt는 왜 쓰고, 왜 사용자마다 다르게 하나요?
//       A. Salt가 없으면 같은 비밀번호는 같은 해시가 되어 미리 계산해 둔 표(레인보우 테이블)로 한 번에 풀리고, 두 사용자의 비밀번호가 같다는 사실도 드러납니다. 사용자마다 랜덤 Salt를 섞으면 공격자가 사용자별로 따로 계산해야 합니다. Salt는 비밀이 아니라서 해시 옆에 같이 저장해도 됩니다.
//   Q3. 그냥 SHA-256 한 번이 아니라 PBKDF2인 이유는요?
//       A. SHA-256은 빠르게 만든 함수라 공격자가 1초에 수십억 번 시도할 수 있습니다. PBKDF2는 같은 계산을 수만 번 반복해 한 번의 시도 비용을 일부러 키웁니다. 정상 로그인은 한 번만 계산하니 체감이 거의 없지만, 무차별 대입은 그만큼 느려집니다.
//   Q4. 반복 횟수는 왜 60만이고, 올릴 때 기존 사용자는 어떻게 하나요?
//       A. OWASP Password Storage Cheat Sheet가 PBKDF2-HMAC-SHA256에 권장하는 값이 60만이라 맞췄습니다(예전 10만은 낡은 값). 이미 저장된 해시는 옛 횟수로 만들어졌으므로 횟수를 해시 문자열 앞에 같이 저장합니다("pbkdf2-sha256$600000$..."). 접두사가 없는 해시는 옛 10만으로 검증하고, 로그인에 성공하면 그 자리에서 새 횟수로 다시 저장해서 사용자가 로그인할 때마다 자연스럽게 업그레이드됩니다. 대가는 로그인 한 번의 CPU 시간이 약 6배가 된다는 점입니다.
//   Q5. FixedTimeEquals는 왜 쓰나요? 그냥 ==이면 안 되나요?
//       A. 일반 비교는 처음 다른 바이트에서 바로 끝나서, 비교에 걸린 시간 차이로 "앞에서 몇 바이트가 맞았는지"가 새어 나갈 수 있습니다. FixedTimeEquals는 끝까지 같은 시간이 걸리게 비교합니다. 다만 해시를 비교하는 상황에서는 실제 위험이 작고, 습관으로 지키는 안전장치에 가깝습니다.
//   Q6. 없는 사원번호로 로그인하면 일부러 해시 계산을 한다고요?
//       A. 네. AuthService는 사용자가 없을 때도 DummyHash로 Verify를 한 번 호출합니다. 안 그러면 "없는 사용자는 응답이 빠르고, 있는 사용자는 느리다"는 시간 차이로 사원번호가 존재하는지 알아낼 수 있기 때문입니다(사용자 열거 방어).
//   Q7. Salt를 만들 때 Random 대신 RandomNumberGenerator를 쓴 이유는요?
//       A. Random은 예측 가능한 수열이라 보안용으로는 부적합합니다. RandomNumberGenerator는 운영체제의 암호용 난수를 써서 예측할 수 없습니다.
//
// ■ 디버깅할 때 먼저 볼 곳
//   - 맞는 비밀번호인데 로그인 실패 ...... 해시 크기(HashSize)를 바꾼 적이 없는지, DB의 password_hash가 잘리지 않았는지(반복 횟수는 해시에 같이 저장돼 바꿔도 기존 사용자가 실패하지 않음), DB의 Salt가 잘리지 않았는지
//   - 로그인이 느리다 ................. 반복 횟수와 서버 CPU. 동시 로그인이 몰리면 PBKDF2가 CPU를 많이 쓴다
//
// ■ 개선 아이디어 (코드는 바꾸지 않음)
//   - (적용 완료) 반복 횟수를 저장해 두고 로그인 성공 시 자동 업그레이드. 알고리즘 자체를 바꿀 때(예: Argon2)도 접두사로 구분하면 같은 방식으로 옮길 수 있다
//   - 입력한 비밀번호 길이 상한은 AuthService에서 64자로 막는다(아주 긴 입력으로 CPU를 쓰게 하는 공격 완화) — 이 파일에는 길이 검사가 없으므로 호출하는 쪽에서 막아야 한다는 점을 기억
// ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// 비밀번호 해시 (PBKDF2-SHA256 + 사용자별 Salt, NF-09).
/// DB에는 원래 비밀번호가 아니라 해시와 Salt만 저장한다.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000; // OWASP 권장(PBKDF2-HMAC-SHA256). 반복 횟수가 클수록 무차별 대입이 느려진다
    private const int LegacyIterations = 100_000; // 횟수를 저장하기 전에 만든 해시(접두사 없음)는 모두 이 값
    private const int MaxIterations = 10_000_000; // DB 값이 망가져도 CPU를 무한정 쓰지 않게 하는 상한
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string Prefix = "pbkdf2-sha256$"; // "pbkdf2-sha256$600000$Base64해시" (password_hash VARCHAR(100)에 들어가는 길이)

    /// <summary>비밀번호를 새 Salt와 최신 반복 횟수의 PBKDF2-SHA256으로 해시해 (해시 문자열, Base64 Salt)를 돌려준다.</summary>
    public static (string Hash, string Salt) Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return ($"{Prefix}{Iterations}${Convert.ToBase64String(hash)}", Convert.ToBase64String(salt));
    }

    /// <summary>입력한 비밀번호가 저장된 해시·Salt와 맞으면 true. 해시 형식이 깨졌으면 false (시간 차이로 정보가 새지 않게 비교).</summary>
    public static bool Verify(string password, string storedHash, string storedSalt)
    {
        if (!TryParse(storedHash, out int iterations, out byte[] expected)) return false;
        byte[] salt = Convert.FromBase64String(storedSalt);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        // 옛 해시는 계산이 더 빨라서, 없는 사원번호(최신 횟수로 계산)와 응답 시간이 달라진다 → 모자란 만큼 더 계산해 시간을 맞춘다.
        if (iterations < Iterations)
            Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations - iterations, HashAlgorithmName.SHA256, expected.Length);

        // 일반 비교(==)는 앞에서부터 다른 글자를 만나면 바로 끝나서 시간 차이로 정보가 샌다.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>저장된 해시가 현재 반복 횟수보다 약하면 true (로그인 성공 직후 새 횟수로 다시 저장하는 데 쓴다).</summary>
    public static bool NeedsRehash(string storedHash)
        => TryParse(storedHash, out int iterations, out _) && iterations < Iterations;

    private static bool TryParse(string stored, out int iterations, out byte[] hash)
    {
        iterations = LegacyIterations;
        hash = [];
        string base64 = stored;
        if (stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            string[] parts = stored.Split('$');
            if (parts.Length != 3 || !int.TryParse(parts[1], out iterations) || iterations is < 1 or > MaxIterations) return false;
            base64 = parts[2];
        }
        try { hash = Convert.FromBase64String(base64); }
        catch (FormatException) { return false; }
        return hash.Length > 0;
    }
}
