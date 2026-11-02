using System.Collections.Concurrent;

namespace ChatApp.Server.Core;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] "같은 대상이 짧은 시간에 너무 많이 시도하는 것"을 막는 속도 제한기 (토큰 버킷 방식).
// [개념] 토큰 버킷: 대상(IP, 사원번호)마다 양동이에 토큰이 최대 capacity개 들어 있고, 시도할 때마다 1개씩 쓴다.
//        토큰은 시간이 지나면 조금씩 다시 찬다(refillPerMinute). 토큰이 없으면 거절.
//        → 평소에는 몇 번 연달아 해도 괜찮고(버스트 허용), 계속 두드리면 일정 속도 이상은 못 한다.
// [왜]   로그인: 비밀번호를 빠르게 계속 대입하는 공격을 늦춘다(계정 잠금과 별개로 IP 단위).
//        회원가입: 가입 1번마다 비밀번호 해시(PBKDF2 60만 번)로 CPU를 많이 쓰고 승인 대기 계정·알림이 쌓인다.
// [스레드] 여러 수신 스레드가 동시에 부른다. 버킷마다 lock을 걸어 같은 대상의 동시 시도도 정확히 센다.
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   토큰 버킷 방식의 "너무 잦은 시도" 차단기. 대상(IP)마다 양동이를 하나 두고, 시도할 때마다 토큰 1개를 쓰며, 토큰은 시간이 지나면 일정 속도로 다시 찬다.
//
// ■ 계산 예시 (PacketHandler에서 쓰는 값)
//   로그인: capacity 10, 분당 10개 충전 → 처음에 10번까지 연달아 가능, 이후 평균 6초에 1번.
//   회원가입: capacity 3, 분당 0.3개 충전 → 처음 3번 가능, 이후 약 3분 20초에 1번(가입 한 번이 비밀번호 해시로 CPU를 많이 쓰기 때문).
//   토큰 수 = min(capacity, 기존 토큰 + 지난 분 × 분당 충전량). 1 미만이면 거절, 이상이면 1 빼고 허용.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 토큰 버킷(token bucket), 리키 버킷, 고정/슬라이딩 윈도우 ... "token bucket rate limiting algorithm"
//   - ConcurrentDictionary.GetOrAdd + 객체별 lock ............... "GetOrAdd per-key lock"
//   - 메모리 누수 방지용 주기 정리(Cleanup) ..................... "evict idle entries memory leak dictionary"
//   - TickCount64(시계 변경에 안전한 경과 시간) ................. "Environment.TickCount64 monotonic"
//
// ■ 면접 예상 질문과 답
//   Q1. 토큰 버킷이 "N초에 한 번만" 같은 단순 간격 제한보다 나은 점은요?
//       A. 정상 사용자는 순간적으로 여러 번 시도할 수 있어야 합니다(비밀번호 오타, 재시도). 토큰 버킷은 그 순간 집중(버스트)을 capacity만큼 허용하면서도, 계속 두드리는 공격에는 평균 속도(충전량)로만 허용해 느리게 만듭니다.
//   Q2. 이 제한과 계정 잠금(5회 실패 10분)은 어떻게 다른가요?
//       A. 계정 잠금은 "한 사원번호"를 보호하고, 속도 제한은 "한 IP"를 제한합니다. 공격자가 사원번호를 바꿔 가며 시도하면 계정 잠금은 피하지만 IP 속도 제한에는 걸리고, 반대로 한 계정을 여러 IP로 공격하면 계정 잠금에 걸립니다. 둘을 함께 써서 서로의 약점을 막습니다.
//   Q3. 동시에 여러 스레드가 같은 IP로 시도하면 정확히 세어지나요?
//       A. 버킷마다 lock을 걸어 "충전 계산 → 확인 → 차감"을 한 덩어리로 처리합니다. 서로 다른 IP끼리는 다른 lock이라 서로 기다리지 않습니다. 버킷을 찾거나 만드는 GetOrAdd는 ConcurrentDictionary가 원자적으로 처리합니다.
//   Q4. Cleanup이 필요한 이유는요?
//       A. IP마다 버킷이 하나씩 생기는데 지우지 않으면 접속한 모든 IP가 메모리에 영원히 남습니다. 30분 넘게 쓰이지 않은 버킷은 이미 토큰이 가득 찼을 시간이라 지워도 동작이 달라지지 않으므로, 하트비트 스레드가 주기적으로 지웁니다.
//   Q5. 시간에 DateTime.Now가 아니라 TickCount64를 쓰는 이유는요?
//       A. DateTime은 서머타임·시간 동기화로 거꾸로 가거나 점프할 수 있어 "경과 시간" 계산이 틀어질 수 있습니다. TickCount64는 부팅 후 흐른 시간이라 단조롭게 증가하므로 간격 계산에 안전합니다.
//   Q6. 한계는요? (솔직하게 답하기)
//       A. (1) 서버 한 대 메모리 안에서만 센다(서버를 여러 대로 늘리면 Redis 같은 공유 저장소가 필요). (2) 프록시·NAT 뒤의 회사 사용자는 같은 IP로 보여 서로 영향을 줄 수 있다. (3) Cleanup이 버킷을 지우는 순간 다른 스레드가 같은 버킷을 쓰고 있으면 그 시도의 카운트가 사라질 수 있지만, 30분 이상 쉰 버킷만 지우므로 실질 영향은 거의 없다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 정상 사용자가 "잠시 후 다시 시도"를 본다 .. PacketHandler의 _loginLimiter/_registerLimiter 값, 회사 NAT로 같은 IP 공유 여부
//   - 개발 중에는 제한이 안 걸린다 .............. 같은 PC(127.0.0.1)는 RateLimitLoopback=false일 때 제외되는 것이 정상
//   - 서버 메모리가 천천히 는다 ................. Cleanup이 주기적으로 불리는지(PacketHandler.Maintenance → 하트비트 스레드 1분 주기)
// ───────────────────────────────────────────────────────────────────────────
/// <summary>키(보통 IP)별 시도 횟수를 토큰 버킷 방식으로 제한하는 클래스 (스레드 안전).</summary>
// [읽는 법] 기본 생성자의 두 값이 곧 정책이다: capacity = 양동이 크기(한 번에 허용하는 연속 시도 수), refillPerMinute = 분당 다시 차는 토큰 수(평균 허용 속도). 인스턴스를 로그인용·가입용으로 따로 만들어 서로 다른 정책을 쓴다.
public sealed class RateLimiter(int capacity, double refillPerMinute)
{
    /// <summary>키 하나의 토큰 양동이 상태.</summary>
    private sealed class Bucket
    {
        /// <summary>지금 남은 토큰 수 (소수 가능, 1 이상이면 시도 허용).</summary>
        public double Tokens;

        /// <summary>토큰을 마지막으로 계산한 시각 (Environment.TickCount64, 밀리초).</summary>
        public long LastTicks;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    /// <summary>한 번 시도해도 되면 true(토큰 1개 사용), 너무 잦으면 false.</summary>
    public bool TryAcquire(string key)
    {
        long now = Environment.TickCount64;
        // [개념] GetOrAdd: 없으면 새로 만들어 넣고, 있으면 있는 것을 준다(원자적).
        // [읽는 법] 처음 보는 대상이면 토큰이 가득 찬 새 양동이를 만든다(처음부터 capacity번은 허용). 이미 있으면 기존 것을 쓴다. 키는 보통 IP 문자열이다.
        Bucket bucket = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = capacity, LastTicks = now });
        lock (bucket)
        {
            // [읽는 법] 마지막으로 확인한 뒤 흐른 시간을 "분" 단위 실수로 바꾼다(60_000ms = 1분, 숫자 사이의 _는 읽기 쉽게 하는 구분자). 60_000.0처럼 .0을 붙여야 나눗셈이 소수까지 계산된다(정수끼리 나누면 소수점이 버려진다).
            double minutes = (now - bucket.LastTicks) / 60_000.0;
            // [읽는 법] "지난 시간만큼 채워 주되 양동이 크기(capacity)를 넘지 않게". Math.Min이 상한이다. 오래 쉬었다 와도 토큰이 무한히 쌓이지 않아 갑자기 수백 번을 허용하는 일이 없다.
            bucket.Tokens = Math.Min(capacity, bucket.Tokens + minutes * refillPerMinute);
            bucket.LastTicks = now;
            // [읽는 법] 토큰이 1개 미만이면 거절. 허용되면 아래에서 1을 빼고 true를 돌려준다. 거절된 시도는 토큰을 쓰지 않지만 시간 기록(LastTicks)은 갱신되어, 계속 두드려도 충전이 "되돌아가지" 않고 정상 속도로 찬다.
            if (bucket.Tokens < 1) return false;
            bucket.Tokens -= 1;
            return true;
        }
    }

    /// <summary>오래 쓰지 않은(이미 가득 찼을) 버킷을 지워 메모리가 계속 늘지 않게 한다. 하트비트 스레드가 가끔 부른다.</summary>
    public void Cleanup()
    {
        long now = Environment.TickCount64;
        // [읽는 법] ConcurrentDictionary는 순회 중에 다른 스레드가 넣고 빼도 안전하다(일관된 스냅샷은 아니지만 예외가 나지 않는다). (key, bucket)은 항목을 키와 값으로 바로 풀어 쓰는 튜플 분해다. 30분 넘게 쓰이지 않은 항목만 지운다.
        foreach (var (key, bucket) in _buckets)
        {
            bool idle;
            lock (bucket) idle = now - bucket.LastTicks > 30 * 60_000;
            if (idle) _buckets.TryRemove(key, out _);
        }
    }
}
