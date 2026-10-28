namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] 서버 설정 중 클라이언트가 알아야 하는 값(공개해도 되는 값)만 담아 보내는 DTO.
// [연결 관계] 클라이언트가 ServerInfoReq를 보내면 서버 AuthService.GetServerInfo()가 이 객체를 만들어
//   ServerInfo 패킷으로 돌려준다. RegisterForm(도메인 목록), MainForm(파일 최대 크기)이 사용한다.
// [왜] 서버의 appsettings.json(ServerConfig)에는 DB 비밀번호가 들어 있으므로 통째로 보내면 안 된다.
//   필요한 값만 골라 담은 별도 클래스를 둔다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 로그인 전에도 받을 수 있는 서버 안내 정보.
/// 회원가입 화면이 "가입 가능한 이메일 도메인"을 서버 설정에서 받아 와 보여 준다.
/// </summary>
public sealed class ServerInfo
{
    /// <summary>가입 가능한 이메일 도메인(@ 뒤). 예: ["company.kr", "basiclike.kr"]</summary>
    // [개념] = []; : C# 12 "컬렉션 식". 빈 리스트를 new List<string>() 대신 짧게 쓴다.
    public List<string> AllowedEmailDomains { get; set; } = [];

    /// <summary>true면 도메인 제한 없음(테스트용). AllowedEmailDomains는 비어 있다.</summary>
    public bool AnyEmailDomain { get; set; }

    /// <summary>false면 가입 후 관리자 승인이 필요하다.</summary>
    public bool AutoApprove { get; set; } = true;

    /// <summary>파일 1개 최대 크기(바이트). 클라이언트가 보내기 전에 미리 확인한다(최종 판단은 서버).</summary>
    // [왜] 3GB를 넘는 파일을 끝까지 올린 뒤에 거절당하면 시간 낭비다. 미리 알려 주면 보내기 전에 걸러낸다.
    //   그래도 클라이언트는 조작될 수 있으니 서버(FileService)도 반드시 다시 검사한다.
    // [개념] long: 3GB(약 32억 바이트)는 int 최댓값(약 21억)을 넘으므로 64비트 정수를 쓴다.
    public long MaxFileSize { get; set; } = FileRules.DefaultMaxFileSize;

    /// <summary>서버의 프로토콜 버전과, 서버가 받아 주는 가장 낮은 클라이언트 버전 (ProtocolInfo 참고).</summary>
    public int ProtocolVersion { get; set; }
    public int MinProtocolVersion { get; set; }

    /// <summary>관리자가 배포한 최신 클라이언트 버전(예: "1.3.0"). 비어 있으면 업데이트 안내를 하지 않는다.</summary>
    public string? LatestClientVersion { get; set; }

    /// <summary>최신 설치 파일 위치(사내 공유 폴더 경로나 웹 주소). 업데이트 안내를 누르면 연다.</summary>
    public string? ClientUpdateUrl { get; set; }

    /// <summary>"@company.kr, @basiclike.kr" 또는 "제한 없음"</summary>
    // [개념] Select(d => "@" + d): LINQ. 목록의 각 요소를 변환한 새 목록을 만든다. string.Join은 구분자로 이어 붙인다.
    public string DomainsText => AnyEmailDomain ? "제한 없음" : string.Join(", ", AllowedEmailDomains.Select(d => "@" + d));
}
