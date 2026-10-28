using System.Security.Cryptography;
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

        // [개념] 비밀번호는 절대 원문으로 저장하지 않는다. PBKDF2(여러 번 반복하는 해시) + salt(사람마다 다른 무작위 값)로 저장.
        //   - 해시: 되돌릴 수 없는 변환. DB가 유출돼도 원래 비밀번호를 바로 알 수 없다.
        //   - salt: 같은 비밀번호라도 사람마다 해시가 달라져, 미리 계산해 둔 표(레인보우 테이블) 공격을 막는다.
        // [개념] var (hash, salt) = ...: 튜플 분해. 반환된 두 값을 각각 변수에 담는다.
        var (hash, salt) = PasswordHasher.Hash(req.Password);
        // [왜] AutoApprove = true면 가입 즉시 사용(ACTIVE), false면 관리자 승인 대기(PENDING). 사내 메신저 운영 방식에 맞게 설정으로 선택.
        string status = _config.AutoApprove ? "ACTIVE" : "PENDING";

        // [주의] "먼저 SELECT로 있는지 확인 → 없으면 INSERT" 방식은 두 사람이 동시에 같은 사원번호로 가입하면 둘 다 통과할 수 있다(경쟁 상태).
        //   그래서 확인 없이 바로 INSERT하고, DB의 PRIMARY KEY/UNIQUE 제약이 중복을 거절하게 한다 → 동시에 와도 딱 한 명만 성공.
        if (!_users.TryInsert(no, email, name, dept, pos, hash, salt, status))
            return (false, "이미 가입된 사원번호 또는 이메일입니다.");

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

        // [개념] UserRecord? : 못 찾으면 null을 돌려준다.
        UserRecord? user = _users.FindByEmployeeNo(no);
        if (user is null) return (null, wrong);

        // [왜] SC-02: 비밀번호를 연속으로 여러 번 틀리면 일정 시간 잠근다 → 비밀번호를 하나씩 대입하는 무차별 대입(brute force) 공격을 막는다.
        //   잠금 확인을 비밀번호 검증보다 "먼저" 해야, 잠긴 동안에는 맞는 비밀번호를 넣어도 시도 자체가 막힌다.
        if (user.LockMinutesLeft > 0)
            return (null, $"로그인 시도 횟수를 초과했습니다. {user.LockMinutesLeft}분 후 다시 시도하세요.");

        // [왜] 나중에 회사 계정(LDAP/AD)으로 로그인하는 사용자는 비밀번호를 우리 DB에 저장하지 않는다(AuthSource = LDAP).
        if (user.AuthSource != "LOCAL" || user.PasswordHash is null || user.Salt is null)
            return (null, "사내 계정(LDAP) 로그인은 아직 지원하지 않습니다."); // TODO(7단계): LdapAuthService

        // [개념] 비밀번호 검증 = 입력한 비밀번호를 저장된 salt로 똑같이 해시해서 저장된 해시와 비교한다(원문을 복원하는 게 아님).
        if (!PasswordHasher.Verify(req.Password, user.PasswordHash, user.Salt))
        {
            // 실패 횟수 +1, 한도에 도달하면 잠금 시각 설정 (UserRepository에서 UPDATE 한 문장으로 원자적으로 처리)
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

        // [왜] DB 행(UserRecord)에는 비밀번호 해시·salt가 있으므로 그대로 보내면 안 된다.
        //   화면에 필요한 정보만 담은 UserInfo로 옮겨서 클라이언트에 보낸다(민감 정보 노출 방지).
        var info = new UserInfo
        {
            EmployeeNo = user.EmployeeNo,
            Name = user.Name,
            Department = user.Department,
            Position = user.Position,
            Role = user.Role,
            StatusMessage = _users.TryLoadStatusMessage(user.EmployeeNo) ?? "", // 지난번에 정한 상태 메시지
            MustChangePassword = _users.TryGetMustChangePassword(user.EmployeeNo), // 관리자가 초기화한 임시 비밀번호인지
        };
        return (info, $"{info.DisplayName}님, 환영합니다!");
    }

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
