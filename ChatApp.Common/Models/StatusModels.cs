namespace ChatApp.Common.Models;

/// <summary>
/// 접속 상태. 친구 목록의 점 색과 알림 여부가 달라진다.
/// [왜] 회사 메신저에서는 "지금 말 걸어도 되는지"가 중요하다(회의 중, 자리 비움).
/// </summary>
public enum Presence
{
    Online,       // 온라인 (초록)
    Away,         // 자리 비움 (노랑) - 10분 동안 입력이 없으면 자동으로 바뀜
    Busy,         // 회의 중 (빨강)
    DoNotDisturb, // 방해 금지 (빨강) - 알림 창·소리를 띄우지 않음
}

/// <summary>내 상태 바꾸기 (클라이언트 → 서버, StatusUpdate 패킷의 Data).</summary>
public sealed class StatusUpdate
{
    public const int MaxMessageLength = 60;

    public Presence Presence { get; set; } = Presence.Online;

    /// <summary>상태 메시지 (예: "오후 반차", "내선 1234"). null이면 바꾸지 않음, ""이면 지움.</summary>
    public string? StatusMessage { get; set; }
}
