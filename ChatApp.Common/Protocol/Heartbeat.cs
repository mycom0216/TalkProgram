namespace ChatApp.Common.Protocol;

/// <summary>
/// 하트비트(연결 확인) 규칙. 서버와 클라이언트가 같은 값을 쓴다.
///
/// [왜] TCP는 상대가 "조용히" 사라지면 알아채지 못한다.
///   - 정상 종료(프로그램 닫기)는 FIN 패킷이 와서 바로 알 수 있다.
///   - 하지만 노트북 절전, 와이파이 끊김, 랜선 뽑힘, 프로그램 강제 종료(정전)에는 아무 신호도 오지 않는다.
///     이때 서버는 그 사람을 계속 "온라인"으로 보여 주고, 클라이언트는 끊긴 줄 모르고 메시지를 보낸다.
///   - 운영체제의 TCP Keep-Alive도 있지만 윈도우 기본값이 2시간이라 채팅에는 쓸 수 없다.
///   → 그래서 프로그램이 직접 "살아 있니?"(Ping) / "응"(Pong)을 주고받는다.
///
/// 동작
///   - 클라이언트는 IntervalSeconds마다 Ping을 보내고, 서버는 Pong으로 답한다.
///   - 양쪽 모두 "마지막으로 무언가를 받은 시각"을 기억하다가, TimeoutSeconds 동안 아무것도 못 받으면 연결을 끊는다.
///     (Ping/Pong뿐 아니라 채팅·파일 조각 등 어떤 패킷이든 받으면 살아 있는 것으로 본다)
///   - Timeout = Interval × 3: Ping 두 번이 잠깐 늦게 도착해도(네트워크 지연) 끊지 않도록 여유를 둔다.
/// </summary>
public static class Heartbeat
{
    /// <summary>클라이언트가 Ping을 보내는 간격(초).</summary>
    public const int IntervalSeconds = 15;

    /// <summary>이 시간(초) 동안 아무 패킷도 받지 못하면 상대가 사라진 것으로 보고 연결을 끊는다.</summary>
    public const int TimeoutSeconds = 45;
}
