namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] FriendModels.cs - 친구 추가·검색에 쓰는 요청/응답과 휴대폰 번호 규칙
// - 친구는 "내 목록에 추가"하는 한쪽 방향이다(카카오톡처럼 상대 승인 없음). 상대 목록에는 생기지 않는다.
// - 검색은 아이디(사원번호) 또는 휴대폰 번호로 "정확히 일치"할 때만 찾는다.
//   [왜] 일부만 맞아도 찾게 하면 번호를 하나씩 바꿔 가며 다른 사람의 번호를 알아낼 수 있다(개인정보).
// - 휴대폰 번호는 다른 사람에게 보여 주지 않는다. 검색 결과에도 가운데를 가린 번호(010-****-5678)만 간다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>친구 찾기 방법.</summary>
public enum FriendSearchBy
{
    /// <summary>아이디(사원번호)</summary>
    EmployeeNo,
    /// <summary>휴대폰 번호</summary>
    Phone,
}

/// <summary>친구 찾기 요청 (클라이언트 → 서버, Packet.Data).</summary>
public sealed class FriendSearchRequest
{
    /// <summary>무엇으로 찾을지(아이디 또는 휴대폰 번호).</summary>
    public FriendSearchBy By { get; set; }
    /// <summary>찾을 값(아이디 또는 휴대폰 번호). 정확히 일치할 때만 찾는다.</summary>
    public string Query { get; set; } = "";
}

/// <summary>친구 찾기 결과 (서버 → 클라이언트). 못 찾으면 User가 null.</summary>
public sealed class FriendSearchResult
{
    /// <summary>요청에서 쓴 찾기 방법.</summary>
    public FriendSearchBy By { get; set; }
    /// <summary>요청에서 보낸 검색어.</summary>
    public string Query { get; set; } = "";
    /// <summary>찾은 사용자. 못 찾으면 null.</summary>
    public UserInfo? User { get; set; }
    /// <summary>이미 내 친구 목록에 있음.</summary>
    public bool AlreadyFriend { get; set; }
    /// <summary>찾은 사람이 나 자신.</summary>
    public bool IsMe { get; set; }
    /// <summary>휴대폰으로 찾았을 때 가운데를 가린 번호 (예: 010-****-5678). 아이디로 찾으면 null.</summary>
    public string? MaskedPhone { get; set; }
}

/// <summary>내 휴대폰 번호 (PhoneInfo 패킷). Phone이 빈 문자열이면 등록 안 함.</summary>
public sealed class PhoneInfo
{
    /// <summary>등록된 휴대폰 번호(예: 010-1234-5678). 빈 문자열이면 등록 안 함.</summary>
    public string Phone { get; set; } = "";
}

/// <summary>친구 기능 규칙 (서버·클라이언트 공통).</summary>
public static class FriendRules
{
    /// <summary>한 사람이 가질 수 있는 최대 친구 수.</summary>
    public const int MaxFriends = 1000;
    /// <summary>친구 찾기 최소 간격(밀리초). 번호를 연달아 바꿔 가며 찾는 것을 막는다.</summary>
    public const int SearchIntervalMs = 1000;
}

/// <summary>휴대폰 번호 정리·검사·가리기.</summary>
public static class PhoneRules
{
    /// <summary>
    /// 숫자만 남겨 "01012345678" 형태로 바꾼다. 휴대폰 번호(010·011·016·017·018·019로 시작, 10~11자리)가 아니면 null.
    /// "+82 10-1234-5678"처럼 국가번호를 붙여도 받아 준다.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string digits = new(input.Where(char.IsDigit).ToArray());
        if (input.TrimStart().StartsWith('+') && digits.StartsWith("82")) digits = "0" + digits[2..];
        if (digits.Length is < 10 or > 11) return null;
        if (!digits.StartsWith("01") || digits[2] is not ('0' or '1' or '6' or '7' or '8' or '9')) return null;
        return digits;
    }

    /// <summary>보기 좋게: 01012345678 → 010-1234-5678, 0161234567 → 016-123-4567.</summary>
    public static string Format(string normalized)
        => normalized.Length == 11 ? $"{normalized[..3]}-{normalized[3..7]}-{normalized[7..]}"
         : normalized.Length == 10 ? $"{normalized[..3]}-{normalized[3..6]}-{normalized[6..]}"
         : normalized;

    /// <summary>가운데를 가린다: 01012345678 → 010-****-5678.</summary>
    public static string Mask(string normalized)
        => normalized.Length >= 10 ? $"{normalized[..3]}-{new string('*', normalized.Length - 7)}-{normalized[^4..]}" : "***";
}
