using System.Security.Cryptography;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ChatApp.Common.Models;
using ChatApp.Server.Data;
using ChatApp.Server.Security;

namespace ChatApp.Server.Services;

// ═══════════════════════════════════════════════════════════════════════════
// AuthService.cs — 회원가입 / 로그인 "규칙"을 담당하는 서비스
//
// [역할]
//   - 회원가입: 입력값 검사(사원번호·이메일·이름·부서·직급·비밀번호) → 비밀번호 해시 → DB 저장
//   - 로그인  : 사원번호로 DB 조회 → 잠금 여부 확인 → 비밀번호 검증 → 계정 상태 확인 → UserInfo 반환
//   - 서버 안내: 회원가입 화면이 쓸 "가입 가능한 이메일 도메인, 파일 최대 크기" 등을 ServerInfo로 만들어 준다
//
// [연결 관계]
//   PacketHandler(패킷 종류별 처리) ──호출──▶ AuthService ──호출──▶ UserRepository(DB users 테이블)
//                                                         └──호출──▶ PasswordHasher(PBKDF2 해시)
//   AuthService는 "네트워크"를 전혀 모른다. 패킷을 만들고 보내는 일은 PacketHandler가 한다.
//   (역할을 나누면 로그인 규칙만 따로 테스트하거나, 나중에 LDAP 로그인으로 바꾸기 쉽다)
//
// [관련 패킷 흐름]
//   ServerInfoReq → ServerInfo(Data = ServerInfo)          : GetServerInfo()
//   RegisterReq(Data = RegisterRequest) → RegisterRes      : Register()
//   LoginReq(Data = LoginRequest) → LoginRes(Data = UserInfo): Login()
//
// [스레드]
//   클라이언트마다 있는 "수신 스레드(Session-N)"들이 이 객체의 메서드를 동시에 호출한다.
//   그래도 lock이 필요 없는 이유:
//     1) 필드(_users, _config, _emailRegex)는 생성자에서 한 번 정해진 뒤 바뀌지 않는다(읽기 전용 → 여러 스레드가 읽어도 안전).
//     2) Regex 객체는 .NET 문서상 여러 스레드에서 동시에 IsMatch를 불러도 안전하다.
//     3) 실제로 바뀌는 데이터(사용자 행)는 DB가 동시성을 처리한다(PRIMARY KEY/UNIQUE 제약, 한 문장짜리 UPDATE).
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]  ※ 이 파일은 기존 설명이 많으므로, 흐름 · 면접 질문 · 아직 설명이 없던 부분(LDAP, 잠금, 임시 비밀번호)을 보강한다.
//
// ■ 한 줄 요약
//   "이 사람이 누구인지 확인하는 규칙" 모음: 가입 검사, 로그인 검사(잠금·계정 상태·LDAP), 비밀번호 변경·초기화. 네트워크는 모르고 규칙만 안다.
//
// ■ 로그인 한눈에 보기 (Login → LoginLocked, 위에서 아래로 "검사 순서"가 곧 설계다)
//   ① 빈 값이면 거절 → ② [같은 사원번호끼리 한 줄로] lock → ③ DB에서 사용자 조회
//   ④ 없는 사원번호 → 가짜 해시로 같은 시간만큼 계산한 뒤 "틀렸다"(시간으로 존재 여부를 못 알아내게)
//   ⑤ 잠금 중이면 비밀번호를 보지도 않고 거절 → ⑥ 인증: LDAP 계정이면 사내 서버에 확인, 아니면 PBKDF2 해시 비교
//   ⑦ 실패면 횟수 +1(한도에 닿으면 잠금) → ⑧ 성공이어도 계정 상태(승인 대기/정지/퇴사) 확인 → ⑨ 화면용 UserInfo로 변환해서 반환
//   ※ "성공 기록"은 여기서 하지 않는다. 중복 접속 확인까지 통과한 뒤 PacketHandler가 RecordLoginSuccess를 부른다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 해시 + salt, PBKDF2(반복 600,000회, SHA-256) ........ "password hashing PBKDF2 salt iterations"
//   - 상수 시간 비교(FixedTimeEquals, PasswordHasher) ..... "constant time comparison timing attack"
//   - 계정 열거(enumeration) 방지: 같은 문구 + 같은 시간 ... "user enumeration timing attack login"
//   - 무차별 대입 방어: 실패 횟수 잠금 ...................... "brute force account lockout"
//   - 줄무늬 잠금(lock striping) ............................ "lock striping"
//   - LDAP 바인드 인증, LDAPS(포트 636), 필터 이스케이프 ..... "LDAP bind authentication LDAP injection escape"
//   - 암호학적 난수 RandomNumberGenerator vs Random ......... "cryptographically secure random C#"
//
// ■ 면접 예상 질문과 답
//   Q1. 비밀번호를 어떻게 저장하나요?
//       A. 원문은 저장하지 않고, 사람마다 다른 무작위 salt(16바이트)를 붙여 PBKDF2-SHA256으로 600,000번 반복한 해시(32바이트)만 저장합니다. 해시는 되돌릴 수 없고, salt 덕분에 같은 비밀번호도 해시가 달라 미리 계산한 표 공격이 어렵습니다. 반복 횟수는 무차별 대입을 느리게 만듭니다.
//   Q2. 사원번호가 틀렸을 때와 비밀번호가 틀렸을 때 같은 문구를 쓰는 이유는요?
//       A. 다르게 알려 주면 공격자가 "이 사원번호는 존재한다"는 정보를 얻어 그 계정만 집중 공격할 수 있습니다(계정 열거). 응답 "시간"도 같아야 해서, 없는 사원번호에도 가짜 해시로 같은 계산을 한 번 합니다.
//   Q3. 로그인 실패 5회 잠금은 어떻게 구현했고, 동시에 여러 연결로 시도하면요?
//       A. 실패 횟수와 잠금 시각은 DB에 두고, 실패 기록은 UPDATE 한 문장으로 원자적으로 처리합니다. 그래도 "잠금 확인 → 검증 → 실패 기록"이 한 덩어리가 아니면 연결 수십 개로 동시에 시도해 5번보다 훨씬 많이 추측할 수 있어서, 같은 사원번호는 lock으로 한 번에 하나씩 처리합니다.
//   Q4. 사원번호마다 lock 객체를 만들지 않고 64개 배열을 쓰는 이유는요?
//       A. 사원번호별로 lock 객체를 만들면 사용자가 늘수록 메모리가 계속 늘고 정리도 어렵습니다. 해시값을 64로 나눈 나머지로 고정된 64개 중 하나를 고르면(줄무늬 잠금) 메모리가 일정하고, 다른 사람과 같은 lock을 쓰더라도 잠깐 기다릴 뿐 결과는 틀리지 않습니다.
//   Q5. 잠금 확인을 비밀번호 검증보다 먼저 하는 이유는요?
//       A. 잠긴 동안에는 맞는 비밀번호를 넣어도 시도 자체가 막혀야 하기 때문입니다. 그렇지 않으면 잠금이 걸려도 공격자가 맞을 때까지 계속 추측할 수 있습니다.
//   Q6. 계정 상태(정지·승인 대기)는 왜 비밀번호가 맞은 뒤에 알려 주나요?
//       A. 비밀번호를 모르는 사람에게 "이 계정은 정지됐다" 같은 정보를 주지 않기 위해서입니다.
//   Q7. 로그인 성공 기록을 Login 안에서 하지 않는 이유는요?
//       A. 비밀번호가 맞아도 "이미 다른 곳에서 접속 중"이면 로그인이 거절될 수 있습니다. 세션 등록까지 성공한 뒤에 호출하는 쪽에서 성공을 기록해야 실패 횟수 초기화·마지막 로그인 시각이 정확합니다.
//   Q8. LDAP 로그인은 어떻게 동작하나요? 사내 서버에 비밀번호를 보내도 안전한가요?
//       A. 사용자 계정에 AuthSource가 "LDAP"이면 "사원번호@도메인" 형태로 LDAP 서버에 Bind(로그인)를 시도해서 성공 여부로 인증합니다. 암호화 없이 비밀번호를 보내면 위험하므로 SSL(LDAPS, 기본 포트 636)을 켠 연결만 사용합니다. 이 서버에는 비밀번호를 저장하지 않습니다.
//   Q9. LDAP 조회에 사용자 입력을 넣을 때 주의할 점은요?
//       A. LDAP 필터에 사원번호를 그대로 넣으면 *, (, ) 같은 문자로 검색 조건을 바꾸는 "LDAP 인젝션"이 가능합니다. 그래서 EscapeLdapFilter로 특수문자를 이스케이프하고, 속성 이름은 영문·숫자·하이픈만 허용(IsLdapAttributeName)합니다.
//   Q10. 임시 비밀번호는 어떻게 만드나요? new Random()을 쓰면 안 되나요?
//       A. 안 됩니다. Random은 시드를 알면 다음 값을 예측할 수 있습니다. 암호학적으로 안전한 RandomNumberGenerator로 헷갈리는 글자(0/O, 1/l/I)를 뺀 10자를 뽑고, 앞 두 글자를 영문·숫자로 고정해 비밀번호 규칙(영문+숫자)을 항상 만족시킵니다. 이 임시 비밀번호는 MustChangePassword로 다음 로그인 때 변경을 강제합니다.
//   Q11. 이 방식의 한계는요? (솔직하게 답하기)
//       A. (1) 같은 사원번호만 한 줄로 세우므로 공격자가 사원번호를 바꿔 가며 시도하는 "분산·사전 대입"은 IP 기준 제한 같은 다른 장치가 필요합니다(서버에는 IP별 연결 수 제한이 있음). (2) 잠금 시간 동안 정상 사용자가 로그인하지 못하는 서비스 거부(DoS) 가능성이 있습니다. (3) 비밀번호 규칙은 길이·영문·숫자 정도라 사전 단어를 막지는 못합니다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 맞는 비밀번호인데 로그인이 안 된다 ........ 잠금 중인지(LockMinutesLeft), AuthSource가 LDAP/LOCAL 중 무엇인지, Status가 ACTIVE인지
//   - LDAP 로그인만 실패한다 .................... ServerConfig의 Ldap(Enabled, Server, Port 636, UserPrincipalNameSuffix), 서버 인증서·방화벽, 타임아웃(1~20초로 제한됨)
//   - 가입이 "이미 가입된" 이라고 나온다 ......... UserRepository.TryInsert의 UNIQUE(사원번호·이메일·휴대폰) 충돌
//   - 이메일 도메인 때문에 가입이 막힌다 ......... ServerConfig의 AnyEmailDomain/EmailDomains → 생성자에서 만든 정규식
//   - 로그인이 가끔 오래 걸린다 ................. 같은 사원번호 동시 시도의 lock 대기, PBKDF2 계산 시간(약 0.1초), LDAP 서버 응답
// ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// 회원가입 / 로그인 규칙 (요구사항 AU-01 회원가입, AU-02 로그인, SC-02 로그인 실패 잠금).
/// 각 세션의 수신 스레드에서 동시에 호출된다. 공유 상태가 없고 DB가 동시성을 처리하므로 lock이 필요 없다.
/// </summary>
public sealed class AuthService
{
    // [개념] 정규식(Regex): 문자열이 정해진 "모양"인지 검사하는 패턴 언어.
    //   ^ = 문자열 시작, $ = 문자열 끝, [A-Za-z0-9] = 영문 대소문자·숫자 중 한 글자, {4,20} = 4~20번 반복.
    //   즉 "처음부터 끝까지 영문/숫자만 4~20자"일 때만 통과한다. (^, $가 없으면 "abc!@#1234"처럼 일부만 맞아도 통과해 버린다)
    // [개념] static readonly: 프로그램 전체에서 딱 하나만 만들고(static) 다시 바꾸지 않는다(readonly).
    //   정규식은 만들 때 비용이 드므로 매번 new 하지 않고 한 번 만들어 재사용한다.
    // [개념] RegexOptions.Compiled: 패턴을 미리 IL 코드로 컴파일해 두어 검사 속도가 빨라진다(처음 만들 때만 조금 느림).
    private static readonly Regex EmployeeNoRegex = new(@"^[A-Za-z0-9]{4,20}$", RegexOptions.Compiled);

    // [왜] 의존성 주입(Dependency Injection): 필요한 객체(DB 접근, 설정)를 직접 new 하지 않고 생성자로 받는다.
    //   → 어떤 DB/설정을 쓸지는 Program.cs가 정하고, 이 클래스는 "규칙"에만 집중할 수 있다. 테스트할 때 가짜 객체로 바꾸기도 쉽다.
    private readonly UserRepository _users;
    private readonly ServerConfig _config;
    // 이메일 검사 정규식. 허용 도메인 설정에 따라 패턴이 달라지므로 static이 아니라 생성자에서 만든다.
    private readonly Regex _emailRegex;

    public AuthService(UserRepository users, ServerConfig config)
    {
        _users = users;
        _config = config;

        // 아이디 부분은 공통, 도메인 부분만 설정에 따라 다르다.
        //  - 제한 없음("*"): 일반적인 이메일 형식이면 허용
        //  - 목록: @company.kr 또는 @basiclike.kr 처럼 목록에 있는 도메인과 정확히 일치해야 허용
        // [개념] 삼항 연산자 (조건 ? A : B): 조건이 참이면 A, 거짓이면 B.
        // - 제한 없음: "글자들(.글자들)*.영문2자 이상" → naver.com, mail.company.co.kr 등 일반 도메인 모양
        // - 목록     : (company\.kr|basiclike\.kr) 처럼 "이 중 하나"를 뜻하는 | (또는) 패턴을 만든다
        // [개념] Regex.Escape: 도메인의 '.'은 정규식에서 "아무 글자 한 개"라는 특수 문자다.
        //   그대로 쓰면 "companyXkr"도 통과하므로, Escape로 "\."(진짜 점)으로 바꿔 정확히 일치하게 만든다.
        // [개념] .Select(Regex.Escape): LINQ. 목록의 각 도메인에 Regex.Escape를 적용한 새 목록을 만든다(메서드 그룹을 그대로 넘긴 형태).
        string domainPattern = config.AnyEmailDomain
            ? @"[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}"
            : "(" + string.Join("|", config.EmailDomains.Select(Regex.Escape)) + ")";
        // [개념] $@"..." = 보간 문자열($: {변수} 끼워 넣기) + 축자 문자열(@: 역슬래시를 그대로 사용).
        //   아이디 부분 [A-Za-z0-9._%+-]+ 는 영문·숫자·._%+- 가 1개 이상(+). 그 뒤 @ 와 도메인 패턴.
        // [개념] RegexOptions.IgnoreCase: 대소문자 무시 (HONG@Company.KR 도 허용).
        _emailRegex = new Regex($@"^[A-Za-z0-9._%+-]+@{domainPattern}$",
                                RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>회원가입 화면에 보여 줄 서버 안내(가입 가능한 도메인 등).</summary>
    // [개념] => (식 본문 멤버): 한 줄짜리 메서드를 { return ...; } 대신 짧게 쓰는 문법.
    // [개념] new() { ... } (대상 형식 new + 객체 초기화자): 반환 형식(ServerInfo)이 정해져 있어 타입 이름을 생략했다.
    // [왜] 제한 없음("*")일 때는 도메인 목록을 비워 보낸다(클라이언트는 AnyEmailDomain을 보고 자주 쓰는 메일 목록을 보여 줌).
    // [왜] MaxFileSize도 함께 보내서, 클라이언트가 너무 큰 파일을 "보내기 전에" 미리 막을 수 있게 한다(최종 판단은 서버).
    public ServerInfo GetServerInfo() => new()
    {
        AllowedEmailDomains = _config.AnyEmailDomain ? [] : _config.EmailDomains,
        AnyEmailDomain = _config.AnyEmailDomain,
        AutoApprove = _config.AutoApprove,
        MaxFileSize = _config.MaxFileSizeBytes,
        ProtocolVersion = ChatApp.Common.Protocol.ProtocolInfo.Version,
        MinProtocolVersion = ChatApp.Common.Protocol.ProtocolInfo.MinSupported,
        LatestClientVersion = string.IsNullOrWhiteSpace(_config.ClientLatestVersion) ? null : _config.ClientLatestVersion.Trim(),
        ClientUpdateUrl = string.IsNullOrWhiteSpace(_config.ClientUpdateUrl) ? null : _config.ClientUpdateUrl.Trim(),
    };

    /// <summary>
    /// 회원가입 처리. 반환값은 (성공 여부, 사용자에게 보여 줄 안내 문구) 두 개.
    /// </summary>
    // [개념] 튜플 반환 (bool Success, string Message): 값 두 개를 클래스를 따로 만들지 않고 한 번에 돌려준다.
    //   호출하는 쪽에서 var (success, message) = auth.Register(req); 처럼 "분해"해서 쓴다.
    // [왜] 클라이언트도 화면에서 검사하지만, 서버가 반드시 다시 검사한다.
    //   클라이언트 프로그램은 사용자가 고치거나 직접 패킷을 만들어 보낼 수 있으므로 "클라이언트 검사는 편의, 서버 검사는 보안"이다.
    public (bool Success, string Message) Register(RegisterRequest req)
    {
        // [왜] Trim(): 앞뒤 공백 제거. 복사·붙여넣기로 생긴 공백 때문에 "A0001 "과 "A0001"이 다른 사람이 되는 일을 막는다.
        // [왜] 이메일은 ToLowerInvariant()로 소문자 통일 → Hong@a.kr 과 hong@a.kr 을 같은 이메일로 취급(UNIQUE 중복 검사 정확).
        //   Invariant = 언어/지역 설정과 무관하게 변환(터키어 i 문제 같은 지역별 차이 방지).
        string no = req.EmployeeNo.Trim();
        string email = req.Email.Trim().ToLowerInvariant();
        string name = req.Name.Trim();
        string dept = req.Department.Trim();
        string pos = req.Position.Trim();

        if (!EmployeeNoRegex.IsMatch(no))
            return (false, "사원번호는 영문·숫자 4~20자로 입력하세요.");
        // [왜] 100자 제한: DB 컬럼 email VARCHAR(100)과 맞춘다. 길이를 먼저 막아야 DB 오류가 나지 않는다.
        if (email.Length > 100 || !_emailRegex.IsMatch(email))
            return (false, _config.AnyEmailDomain
                ? "이메일 형식이 올바르지 않습니다. (예: hong@company.kr)"
                : $"사내 이메일({GetServerInfo().DomainsText})만 가입할 수 있습니다.");
        // [개념] 패턴 매칭 is < 1 or > 30: "1보다 작거나 30보다 크면". (name.Length < 1 || name.Length > 30)과 같은 뜻.
        // [왜] 각 길이 제한은 DB 컬럼 크기(VARCHAR(30) 등)와 맞춘 것이다.
        if (name.Length is < 1 or > 30)
            return (false, "이름은 1~30자로 입력하세요.");
        if (dept.Length is < 1 or > 30)
            return (false, "부서는 1~30자로 입력하세요.");
        if (pos.Length is < 1 or > 20)
            return (false, "직급은 1~20자로 입력하세요.");
        // [개념] is string pwError: 결과가 null이 아니고 string이면 pwError 변수에 담아서 if 안으로 들어간다(형식 패턴).
        if (ValidatePassword(req.Password) is string pwError)
            return (false, pwError);
        // 휴대폰 번호는 선택. 적었으면 형식을 확인한다 (친구가 번호로 나를 찾을 때만 쓰고 공개하지 않는다)
        string? phone = null;
        if (!string.IsNullOrWhiteSpace(req.Phone))
        {
            phone = PhoneRules.Normalize(req.Phone);
            if (phone is null) return (false, "휴대폰 번호 형식이 올바르지 않습니다. (예: 010-1234-5678)");
        }

        // [개념] 비밀번호는 절대 원문으로 저장하지 않는다. PBKDF2(여러 번 반복하는 해시) + salt(사람마다 다른 무작위 값)로 저장.
        //   - 해시: 되돌릴 수 없는 변환. DB가 유출돼도 원래 비밀번호를 바로 알 수 없다.
        //   - salt: 같은 비밀번호라도 사람마다 해시가 달라져, 미리 계산해 둔 표(레인보우 테이블) 공격을 막는다.
        // [개념] var (hash, salt) = ...: 튜플 분해. 반환된 두 값을 각각 변수에 담는다.
        var (hash, salt) = PasswordHasher.Hash(req.Password);
        // [왜] AutoApprove = true면 가입 즉시 사용(ACTIVE), false면 관리자 승인 대기(PENDING). 사내 메신저 운영 방식에 맞게 설정으로 선택.
        string status = _config.AutoApprove ? "ACTIVE" : "PENDING";

        // [주의] "먼저 SELECT로 있는지 확인 → 없으면 INSERT" 방식은 두 사람이 동시에 같은 사원번호로 가입하면 둘 다 통과할 수 있다(경쟁 상태).
        //   그래서 확인 없이 바로 INSERT하고, DB의 PRIMARY KEY/UNIQUE 제약이 중복을 거절하게 한다 → 동시에 와도 딱 한 명만 성공.
        if (!_users.TryInsert(no, email, name, dept, pos, hash, salt, status, phone))
            return (false, phone is null ? "이미 가입된 사원번호 또는 이메일입니다." : "이미 가입된 사원번호·이메일 또는 휴대폰 번호입니다.");

        return (true, _config.AutoApprove
            ? "회원가입이 완료되었습니다. 로그인하세요."
            : "회원가입 신청이 완료되었습니다. 관리자 승인 후 로그인할 수 있습니다.");
    }

    /// <summary>비밀번호 규칙 검사. 문제가 있으면 안내 문구, 없으면 null.</summary>
    // [개념] string? : null이 될 수 있는 문자열(Nullable 참조 형식). "오류가 없음"을 null로 표현한다.
    // [개념] password.Any(char.IsLetter): LINQ Any = 하나라도 조건을 만족하면 true. "영문이 한 글자라도 있는가".
    private static string? ValidatePassword(string password)
    {
        if (password.Length is < 8 or > 64) return "비밀번호는 8~64자로 입력하세요.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)) return "비밀번호에 영문과 숫자를 모두 포함하세요.";
        return null;
    }

    /// <summary>
    /// 사원번호·비밀번호 확인. 성공하면 User에 사용자 정보가 담긴다.
    /// 중복 접속 확인과 성공 기록은 세션 등록에 성공한 뒤 호출하는 쪽(PacketHandler)에서 한다.
    /// </summary>
    public (UserInfo? User, string Message) Login(LoginRequest req)
    {
        // [왜] 사원번호가 없는 경우와 비밀번호가 틀린 경우에 "같은" 문구를 쓴다.
        //   다르게 알려 주면 공격자가 "이 사원번호는 존재한다"는 정보를 얻어(계정 열거 공격) 비밀번호만 집중 공격할 수 있다.
        const string wrong = "사원번호 또는 비밀번호가 올바르지 않습니다."; // 어느 쪽이 틀렸는지 알려 주지 않는다
        string no = req.EmployeeNo.Trim();
        if (no.Length == 0 || req.Password.Length == 0) return (null, wrong);

        // [왜] 같은 사원번호의 로그인 시도는 한 번에 하나씩 처리한다.
        //      "잠금 확인 → 비밀번호 확인 → 실패 기록"이 한 덩어리가 아니면, 연결 수십 개로 동시에 시도해
        //      잠금(5회)이 걸리기 전에 5번보다 훨씬 많이 추측할 수 있다.
        // [개념] 줄무늬 잠금(lock striping): 사원번호마다 lock 객체를 만들면 메모리가 계속 늘어나므로,
        //      해시값으로 64개 중 하나를 골라 쓴다. 다른 사람끼리 같은 lock을 쓸 수도 있지만 잠깐 기다릴 뿐 틀리지는 않는다.
        // [읽는 법] no.GetHashCode()는 음수가 될 수 있다. "& int.MaxValue"로 맨 앞 비트(부호)를 지워 항상 0 이상으로 만든 뒤 % 64로 0~63 중 하나를 고른다.
        //           즉 "같은 사원번호 = 항상 같은 lock"이 보장된다. 주의: string.GetHashCode()는 프로그램을 다시 실행하면 값이 달라지지만, 한 번 실행되는 동안에는 같으므로 이 용도에는 충분하다.
        lock (LoginLocks[(no.GetHashCode() & int.MaxValue) % LoginLocks.Length])
            return LoginLocked(no, req, wrong);
    }

    // [읽는 법] Enumerable.Range(0, 64)는 0~63의 숫자 64개. Select(_ => new object())로 각각 빈 객체(lock 열쇠)를 만들고 ToArray()로 배열을 만든다. 서버가 켜질 때 한 번 만들어 계속 쓴다.
    private static readonly object[] LoginLocks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

    // 없는 사원번호일 때도 비밀번호 확인과 비슷한 시간을 쓰기 위한 가짜 해시 (서버 시작 때 1번 만든다)
    // [읽는 법] 서버 시작 때 한 번 만들어 두는 "가짜" 해시. 없는 사원번호가 들어와도 PasswordHasher.Verify를 한 번 돌려서, 있는 사원번호일 때와 응답 시간을 비슷하게 맞춘다(타이밍 공격 대비).
    private static readonly (string Hash, string Salt) DummyHash = PasswordHasher.Hash("dummy-password-for-timing");

    private (UserInfo? User, string Message) LoginLocked(string no, LoginRequest req, string wrong)
    {
        // [개념] UserRecord? : 못 찾으면 null을 돌려준다.
        // [읽는 법] DB의 users 한 행을 UserRecord(해시·salt·잠금 정보까지 포함한 "내부용" 객체)로 가져온다. 이 객체는 절대 클라이언트로 내보내지 않는다 — 아래에서 UserInfo로 바꿔서 보낸다.
        UserRecord? user = _users.FindByEmployeeNo(no);
        if (user is null)
        {
            // [왜] 없는 사원번호면 바로 답하면, 응답이 "비밀번호 확인(약 0.1초)"보다 눈에 띄게 빨라
            //      시간만 재도 "이 사원번호는 없다"를 알 수 있다(계정 열거). 같은 계산을 한 번 해서 시간을 맞춘다.
            PasswordHasher.Verify(req.Password, DummyHash.Hash, DummyHash.Salt);
            return (null, wrong);
        }

        // [왜] SC-02: 비밀번호를 연속으로 여러 번 틀리면 일정 시간 잠근다 → 비밀번호를 하나씩 대입하는 무차별 대입(brute force) 공격을 막는다.
        //   잠금 확인을 비밀번호 검증보다 "먼저" 해야, 잠긴 동안에는 맞는 비밀번호를 넣어도 시도 자체가 막힌다.
        if (user.LockMinutesLeft > 0)
            return (null, $"로그인 시도 횟수를 초과했습니다. {user.LockMinutesLeft}분 후 다시 시도하세요.");

        LdapProfile? ldapProfile = null;
        // [읽는 법] 삼항 연산자 두 겹: LDAP 계정이면 사내 LDAP 서버에 확인 → 아니면(LOCAL이고 해시·salt가 DB에 있을 때만) PBKDF2 해시를 비교한다. 둘 다 아니면(예: 해시가 없는 계정) false라서 로그인이 거절된다.
        //           out ldapProfile: LDAP 인증에 성공하면 이름·부서·직급 같은 프로필 정보도 함께 돌려받는 출구 매개변수다.
        bool authenticated = user.AuthSource == "LDAP"
            ? VerifyLdap(user.EmployeeNo, req.Password, out ldapProfile)
            : user.AuthSource == "LOCAL" && user.PasswordHash is not null && user.Salt is not null
                && PasswordHasher.Verify(req.Password, user.PasswordHash, user.Salt);
        if (!authenticated)
        {
            // 실패 횟수 +1, 한도에 도달하면 잠금 시각 설정 (UserRepository에서 UPDATE 한 문장으로 원자적으로 처리)
            // [읽는 법] DB에서 "실패 횟수 +1, 한도(기본 5회)에 닿으면 잠금 시각 = 지금 + 잠금 분(기본 10분)"을 한 문장의 UPDATE로 처리한다. 읽고 → 더하고 → 쓰는 세 동작이 아니라 한 문장이라 동시에 실패해도 횟수가 틀어지지 않는다.
            _users.RecordLoginFailure(no, _config.MaxLoginAttempts, _config.LockMinutes);
            return (null, wrong);
        }

        // 계정 상태는 비밀번호가 맞을 때만 알려 준다
        // [왜] 계정 상태(승인 대기/정지/퇴사)는 비밀번호가 맞을 때만 알려 준다. 비밀번호를 모르는 사람에게 계정 정보를 흘리지 않기 위해.
        // [개념] switch 문: 값(Status)에 따라 여러 갈래로 나눈다. ACTIVE는 case가 없으므로 그대로 아래로 내려가 로그인 성공.
        switch (user.Status)
        {
            case "PENDING": return (null, "관리자 승인 대기 중인 계정입니다.");
            case "SUSPENDED": return (null, "정지된 계정입니다. 관리자에게 문의하세요.");
            case "RESIGNED": return (null, "사용할 수 없는 계정입니다.");
        }

        // [왜] 옛 반복 횟수(10만)로 저장된 해시는 비밀번호를 아는 지금(로그인 성공 직후)에만 새 횟수로 다시 만들 수 있다.
        //      실패해도 로그인 자체는 막지 않는다(다음 로그인 때 다시 시도).
        if (user.AuthSource == "LOCAL" && user.PasswordHash is not null && PasswordHasher.NeedsRehash(user.PasswordHash))
        {
            try
            {
                var (newHash, newSalt) = PasswordHasher.Hash(req.Password);
                _users.UpgradePasswordHash(user.EmployeeNo, user.PasswordHash, newHash, newSalt);
            }
            catch (System.Data.Common.DbException) { }
        }

        // [읽는 법] LDAP 로그인에 성공했고 프로필을 받았다면 이름·부서·직급을 DB에 동기화하고, 갱신된 행을 다시 읽는다(?? user = 못 읽으면 기존 값 사용). 단, 계정을 새로 만들거나 권한을 주지는 않는다 — 이미 DB에 있는 사용자만 대상이다.
        if (ldapProfile is not null)
        {
            _users.UpdateLdapProfile(user.EmployeeNo, ldapProfile.Name, ldapProfile.Department, ldapProfile.Position);
            user = _users.FindByEmployeeNo(user.EmployeeNo) ?? user;
        }

        // [왜] DB 행(UserRecord)에는 비밀번호 해시·salt가 있으므로 그대로 보내면 안 된다.
        //   화면에 필요한 정보만 담은 UserInfo로 옮겨서 클라이언트에 보낸다(민감 정보 노출 방지).
        // [읽는 법] UserRecord(내부용) → UserInfo(화면용) 변환. 상태 메시지·임시 비밀번호 여부·사진 버전을 DB에서 함께 채운다. TryLoad~ 형태 함수는 DB 칸이 없거나 실패해도 예외 대신 기본값을 돌려주는 "안전한 읽기"다.
        var info = new UserInfo
        {
            EmployeeNo = user.EmployeeNo,
            Name = user.Name,
            Department = user.Department,
            Position = user.Position,
            Role = user.Role,
            StatusMessage = _users.TryLoadStatusMessage(user.EmployeeNo) ?? "", // 지난번에 정한 상태 메시지
            MustChangePassword = _users.TryGetMustChangePassword(user.EmployeeNo), // 관리자가 초기화한 임시 비밀번호인지
            PhotoVersion = _users.GetPhotoVersion(user.EmployeeNo), // 프로필 사진 (0 = 없음)
            BackgroundVersion = _users.GetPhotoVersion(user.EmployeeNo, PhotoKind.Background), // 배경 사진
        };
        return (info, $"{info.DisplayName}님, 환영합니다!");
    }

    private sealed record LdapProfile(string? Name, string? Department, string? Position);

    // [전체 흐름] ① 설정 확인(꺼져 있거나 서버·도메인 없음 → false) ② LDAPS 연결 만들기(포트, 타임아웃 1~20초로 제한) ③ connection.Bind() = 사용자 계정으로 로그인 시도(성공하면 비밀번호가 맞은 것)
    //             ④ (설정했다면) 같은 연결로 이름·부서·직급 조회 — 이 조회가 실패해도 로그인 성공은 그대로 유지(프로필은 "있으면 좋은" 정보라서)
    //             ⑤ 네트워크·LDAP 오류는 false로 처리해 로그인 실패로 보이게 한다(서버가 죽지 않는다).
    private bool VerifyLdap(string employeeNo, string password, out LdapProfile? profile)
    {
        profile = null;
        LdapConfig ldap = _config.Ldap;
        if (!ldap.Enabled || string.IsNullOrWhiteSpace(ldap.Server) || string.IsNullOrWhiteSpace(ldap.UserPrincipalNameSuffix) || password.Length == 0)
            return false;
        try
        {
            var identifier = new LdapDirectoryIdentifier(ldap.Server, ldap.Port, fullyQualifiedDnsHostName: true, connectionless: false);
            using var connection = new LdapConnection(identifier,
                new NetworkCredential($"{employeeNo}@{ldap.UserPrincipalNameSuffix.TrimStart('@')}", password), AuthType.Basic);
            connection.Timeout = TimeSpan.FromSeconds(Math.Clamp(ldap.TimeoutSeconds, 1, 20));
            connection.SessionOptions.ProtocolVersion = 3;
            // [왜] 이 연결로 사용자의 비밀번호가 지나간다. SSL(LDAPS)을 켜서 암호화하지 않으면 사내망이라도 중간에서 엿볼 수 있다. AuthType.Basic은 비밀번호를 그대로 보내므로 반드시 암호화 연결과 함께 써야 한다.
            connection.SessionOptions.SecureSocketLayer = true;
            connection.Bind();
            if (!string.IsNullOrWhiteSpace(ldap.ProfileBaseDn) && IsLdapAttributeName(ldap.EmployeeNumberAttribute))
            {
                try
                {
                    string[] attributes = new[] { ldap.DisplayNameAttribute, ldap.DepartmentAttribute, ldap.PositionAttribute }
                        .Where(IsLdapAttributeName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var search = new System.DirectoryServices.Protocols.SearchRequest(ldap.ProfileBaseDn,
                        // [왜] LDAP 필터 문자열에 사용자 입력을 끼워 넣을 때는 반드시 이스케이프한다(SQL 인젝션과 같은 원리). 속성 이름은 설정값이지만 IsLdapAttributeName으로 한 번 더 검사해 필터 문법이 깨지지 않게 막는다.
                        $"({ldap.EmployeeNumberAttribute}={EscapeLdapFilter(employeeNo)})", SearchScope.Subtree, attributes);
                    // [읽는 법] "is SearchResponse { Entries.Count: > 0 } response" = 응답이 SearchResponse 형식이고 결과가 1개 이상일 때만 response 변수에 담고 들어간다(속성 패턴). 결과가 없으면 조용히 건너뛴다.
                    if (connection.SendRequest(search) is SearchResponse { Entries.Count: > 0 } response)
                    {
                        SearchResultEntry entry = response.Entries[0];
                        profile = new LdapProfile(ReadLdapValue(entry, ldap.DisplayNameAttribute),
                            ReadLdapValue(entry, ldap.DepartmentAttribute), ReadLdapValue(entry, ldap.PositionAttribute));
                    }
                }
                // [읽는 법] 프로필 조회가 실패해도 이미 비밀번호 확인은 끝났으므로 로그인 자체는 성공으로 둔다(주석: Profile reads are optional). "when (...)" 예외 필터는 예상한 종류의 예외만 잡고, 나머지(버그)는 그대로 위로 올려 숨기지 않는다.
                catch (Exception ex) when (ex is LdapException or DirectoryOperationException or TimeoutException)
                {
                    // Profile reads are optional: successful password verification remains authoritative.
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is LdapException or SocketException or DirectoryOperationException or TimeoutException)
        {
            return false;
        }
    }

    // [읽는 법] 속성 이름이 "비어 있지 않고 64자 이하이며 영문·숫자·하이픈만"인지 검사한다. { Length: > 0 and <= 64 }는 속성 패턴으로, null이면 자동으로 거짓이 되어 별도의 null 검사가 필요 없다.
    private static bool IsLdapAttributeName(string? value)
        => value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // [읽는 법] LDAP 필터에서 특별한 뜻을 갖는 문자(\ * ( ) 그리고 널 문자)를 "\숫자" 형태의 안전한 표기로 바꾼다. 가장 먼저 역슬래시를 바꾸는 이유: 나중에 바꾸면 방금 만든 "\28" 같은 표기의 역슬래시를 또 바꿔 버리기 때문이다.
    private static string EscapeLdapFilter(string value)
        => value.Replace("\\", "\\5c", StringComparison.Ordinal).Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal).Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);

    private static string? ReadLdapValue(SearchResultEntry entry, string attribute)
        => IsLdapAttributeName(attribute) && entry.Attributes[attribute] is DirectoryAttribute values && values.Count > 0
            ? values[0]?.ToString()?.Trim() : null;

    /// <summary>
    /// 로그인 최종 성공 기록(실패 횟수 0으로 초기화, 마지막 로그인 시각 저장).
    /// </summary>
    // [왜] Login() 안에서 바로 기록하지 않는 이유: 비밀번호가 맞아도 "이미 다른 곳에서 접속 중"이면 로그인이 거절될 수 있다.
    //   그래서 PacketHandler가 세션 등록(TryLogin)까지 성공한 뒤에 이 메서드를 부른다.
    public void RecordLoginSuccess(string employeeNo) => _users.RecordLoginSuccess(employeeNo);

    /// <summary>
    /// 본인 비밀번호 변경. 현재 비밀번호를 확인한 뒤 새 비밀번호로 바꾼다(임시 비밀번호 상태도 풀린다).
    /// </summary>
    // [왜] 로그인한 상태여도 "현재 비밀번호"를 다시 묻는다: 잠깐 자리를 비운 사이 다른 사람이 내 PC에서
    //      비밀번호를 바꿔 계정을 빼앗는 일을 막기 위해서다(대부분의 서비스가 이렇게 한다).
    // [검사 순서] 계정 확인 → 현재 비밀번호 확인 → 새 비밀번호 규칙 → 현재와 같은지 → 새 해시를 만들어 저장(mustChange: false = 임시 상태 해제).
    //             LDAP 계정은 이 서버에 비밀번호 해시가 없으므로 첫 검사에서 "바꿀 수 없는 계정"으로 막힌다(비밀번호 관리는 사내 LDAP 쪽).
    public (bool Success, string Message) ChangePassword(string employeeNo, ChangePasswordRequest req)
    {
        UserRecord? user = _users.FindByEmployeeNo(employeeNo);
        if (user?.PasswordHash is null || user.Salt is null)
            return (false, "비밀번호를 바꿀 수 없는 계정입니다.");
        if (!PasswordHasher.Verify(req.CurrentPassword, user.PasswordHash, user.Salt))
            return (false, "현재 비밀번호가 올바르지 않습니다.");
        if (ValidatePassword(req.NewPassword) is string error)
            return (false, error);
        if (req.NewPassword == req.CurrentPassword)
            return (false, "새 비밀번호가 현재 비밀번호와 같습니다.");

        var (hash, salt) = PasswordHasher.Hash(req.NewPassword);
        if (!_users.UpdatePassword(employeeNo, hash, salt, mustChange: false))
            return (false, "비밀번호를 저장하지 못했습니다.");
        return (true, "비밀번호를 바꿨습니다.");
    }

    /// <summary>
    /// 관리자의 비밀번호 초기화: 임시 비밀번호를 만들어 저장하고(다음 로그인 때 변경 필수) 그 값을 돌려준다.
    /// </summary>
    // [읽는 법] 관리자가 호출한다. 임시 비밀번호를 만들어 해시로 저장하고(mustChange: true) 원문은 관리자에게 한 번 돌려준다 — 서버에는 원문이 남지 않는다. 저장에 실패하면 null이라 호출한 쪽이 "초기화 실패"를 안다.
    public string? ResetPassword(string employeeNo)
    {
        string temp = CreateTempPassword();
        var (hash, salt) = PasswordHasher.Hash(temp);
        return _users.UpdatePassword(employeeNo, hash, salt, mustChange: true) ? temp : null;
    }

    /// <summary>헷갈리는 글자(0/O, 1/l/I)를 뺀 영문+숫자 10자 임시 비밀번호.</summary>
    // [개념] RandomNumberGenerator: 암호학적으로 안전한 난수. new Random()은 다음 값을 예측할 수 있어 비밀번호에 쓰면 안 된다.
    private static string CreateTempPassword()
    {
        const string letters = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string all = letters + digits;
        // [개념] stackalloc: 힙(가비지 컬렉션 대상)이 아니라 "스택"에 잠깐 쓰는 작은 배열을 만든다. 10글자짜리라 크기가 작고, 함수가 끝나면 자동으로 사라져 쓰레기가 남지 않는다. Span<char>는 그 메모리를 안전하게 다루는 창이다.
        //        RandomNumberGenerator.GetInt32(n)은 0~n-1 사이의 "예측할 수 없는" 난수를 준다.
        Span<char> chars = stackalloc char[10];
        for (int i = 0; i < chars.Length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        // 비밀번호 규칙(영문+숫자 포함)을 반드시 만족하도록 앞 두 글자를 고정 종류로
        chars[0] = letters[RandomNumberGenerator.GetInt32(letters.Length)];
        chars[1] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        return new string(chars);
    }

    /// <summary>상태 메시지를 DB에 저장한다. DB에 칸이 없으면 false.</summary>
    public bool SaveStatusMessage(string employeeNo, string message) => _users.TrySaveStatusMessage(employeeNo, message);
}
