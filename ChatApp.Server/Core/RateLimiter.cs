using System.Collections.Concurrent;

namespace ChatApp.Server.Core;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] "같은 대상이 짧은 시간에 너무 많이 시도하는 것"을 막는 속도 제한기 (토큰 버킷 방식).
// [개념] 토큰 버킷: 대상(IP, 사원번호)마다 양동이에 토큰이 최대 capacity개 들어 있고, 시도할 때마다 1개씩 쓴다.
//        토큰은 시간이 지나면 조금씩 다시 찬다(refillPerMinute). 토큰이 없으면 거절.
//        → 평소에는 몇 번 연달아 해도 괜찮고(버스트 허용), 계속 두드리면 일정 속도 이상은 못 한다.
// [왜]   로그인: 비밀번호를 빠르게 계속 대입하는 공격을 늦춘다(계정 잠금과 별개로 IP 단위).
//        회원가입: 가입 1번마다 비밀번호 해시(PBKDF2 10만 번)로 CPU를 많이 쓰고 승인 대기 계정·알림이 쌓인다.
// [스레드] 여러 수신 스레드가 동시에 부른다. 버킷마다 lock을 걸어 같은 대상의 동시 시도도 정확히 센다.
// ═══════════════════════════════════════════════════════════════════════════
public sealed class RateLimiter(int capacity, double refillPerMinute)
{
    private sealed class Bucket
    {
        public double Tokens;
        public long LastTicks;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    /// <summary>한 번 시도해도 되면 true(토큰 1개 사용), 너무 잦으면 false.</summary>
    public bool TryAcquire(string key)
    {
        long now = Environment.TickCount64;
        // [개념] GetOrAdd: 없으면 새로 만들어 넣고, 있으면 있는 것을 준다(원자적).
        Bucket bucket = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = capacity, LastTicks = now });
        lock (bucket)
        {
            double minutes = (now - bucket.LastTicks) / 60_000.0;
            bucket.Tokens = Math.Min(capacity, bucket.Tokens + minutes * refillPerMinute);
            bucket.LastTicks = now;
            if (bucket.Tokens < 1) return false;
            bucket.Tokens -= 1;
            return true;
        }
    }

    /// <summary>오래 쓰지 않은(이미 가득 찼을) 버킷을 지워 메모리가 계속 늘지 않게 한다. 하트비트 스레드가 가끔 부른다.</summary>
    public void Cleanup()
    {
        long now = Environment.TickCount64;
        foreach (var (key, bucket) in _buckets)
        {
            bool idle;
            lock (bucket) idle = now - bucket.LastTicks > 30 * 60_000;
            if (idle) _buckets.TryRemove(key, out _);
        }
    }
}
