using System.Text.Json.Serialization;

namespace ChatApp.Common.Protocol;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] 서버와 클라이언트가 주고받는 "편지 봉투" 하나(Packet)를 정의한다.
//   - 로그인, 채팅, 방 입장, 파일 조각 등 모든 통신이 이 클래스 하나로 오간다.
//   - 어떤 종류의 편지인지는 Type(PacketType enum)으로 구분하고, 나머지 칸은 종류에 따라 골라 쓴다.
// [연결 관계]
//   Packet → PacketSerializer(바이트로 변환) → NetworkHelper(소켓 스트림에 쓰기/읽기)
//   서버의 PacketHandler, 클라이언트의 ChatClient/MainForm이 이 객체를 만들고 읽는다.
// [스레드] Packet 자체는 단순한 데이터 묶음이다. 보통 만든 스레드가 채운 뒤 다른 스레드로 넘기고,
//   넘긴 뒤에는 고치지 않는다(그래야 여러 스레드가 동시에 읽어도 안전하다).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 모든 통신에 쓰는 공통 패킷. JSON으로 직렬화되어 [길이 4바이트][JSON] 형태로 전송된다.
/// </summary>
// [왜] 패킷 종류마다 클래스를 따로 만들지 않고 "칸이 넉넉한 봉투 하나"를 쓴다.
//   장점: 직렬화/역직렬화 코드가 하나로 끝나고, 새 기능은 PacketType에 값만 추가하면 된다.
//   종류별로 다른 구조화된 데이터는 Data 칸에 JSON 문자열로 넣는다(WithData / GetData).
// [개념] sealed: 이 클래스를 상속하지 못하게 막는다. 상속할 이유가 없는 데이터 클래스에 붙이면
//   의도가 분명해지고, JIT가 가상 호출을 최적화하기도 쉽다.
public sealed class Packet
{
    /// <summary>패킷 종류(로그인 요청, 채팅, 파일 조각 ...). 받는 쪽은 이 값으로 switch해서 처리할 함수를 고른다.</summary>
    public PacketType Type { get; set; }

    /// <summary>보낸 사람 표시 이름(예: "홍길동 대리"). 서버가 직접 채운다(클라이언트 값은 믿지 않음).</summary>
    // [주의] 클라이언트가 보낸 Sender 값을 그대로 쓰면 다른 사람인 척(사칭)할 수 있다.
    //   그래서 서버는 로그인할 때 확인한 사용자 정보로 이 칸을 덮어쓴다.
    // [개념] string? : 'null일 수 있는 문자열'이라는 표시(Nullable 참조 형식). 컴파일러가 null 검사를 빠뜨리면 경고해 준다.
    public string? Sender { get; set; }

    /// <summary>보낸 사람 사원번호. 서버가 직접 채운다.</summary>
    public string? SenderId { get; set; }

    /// <summary>받는 대상(귓속말 상대 사원번호, 이후 단계에서는 방 ID 등).</summary>
    // [왜] 대상이 사원번호일 때도, 방 번호일 때도, 파일 ID일 때도 있어서 가장 범용적인 string으로 둔다.
    //   방 번호는 받는 쪽에서 int.TryParse로 숫자로 바꿔 쓴다.
    public string? Target { get; set; }

    /// <summary>메시지 본문 또는 안내 문구.</summary>
    public string? Body { get; set; }

    /// <summary>구조화된 데이터(JSON). 로그인 요청, 사용자 정보, 접속자 목록 등.</summary>
    // [왜] 객체를 그대로 두지 않고 "JSON 문자열"로 담는 이유:
    //   Packet은 종류가 수십 가지인데, 칸 하나에 서로 다른 타입(LoginRequest, List<UserInfo> ...)을 넣어야 한다.
    //   문자열로 담아 두면 Packet의 모양은 항상 같고, 받는 쪽이 Type을 보고 알맞은 타입으로 꺼내면 된다.
    public string? Data { get; set; }

    /// <summary>요청 결과(LoginRes, RegisterRes 등 응답 패킷에서 사용).</summary>
    // [개념] { get; set; } = true; : 자동 속성의 초깃값. 새 Packet을 만들면 Success는 기본적으로 true다.
    public bool Success { get; set; } = true;

    /// <summary>패킷을 만든 시각. 서버가 채팅을 보낼 때 정한 시각이 화면의 "16:19" 같은 시간 표시가 된다.</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>바이너리 데이터(파일 조각). JSON에서는 Base64 문자열로 바뀐다. 없으면 JSON에 아예 넣지 않는다.</summary>
    // [개념] Base64: 바이트(0~255 아무 값)를 A~Z, a~z, 0~9, +, / 64개 글자로만 표현하는 방법.
    //   JSON은 텍스트 형식이라 바이트를 그대로 넣을 수 없어서, System.Text.Json은 byte[]를 자동으로 Base64 문자열로 바꾼다.
    //   대신 크기가 약 4/3배(33%) 커진다. 3바이트 → 글자 4개.
    // [개념] [JsonIgnore(Condition = WhenWritingNull)] : 이 값이 null이면 JSON에 "Bin":null 칸 자체를 쓰지 않는다.
    //   채팅 패킷처럼 Bin을 안 쓰는 대부분의 패킷 크기를 줄이기 위함.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Bin { get; set; }

    /// <summary>자주 쓰는 칸만 골라 패킷을 만드는 지름길(팩토리 메서드).</summary>
    // [개념] 선택적 매개변수(string? body = null ...): 호출할 때 생략하면 기본값이 들어간다.
    //   예) Packet.Create(PacketType.Ping)  /  Packet.Create(PacketType.ChatMsg, body: "안녕", target: "1")
    // [개념] => : 식 본문(expression-bodied) 멤버. 한 줄짜리 메서드를 { return ...; } 대신 짧게 쓰는 문법.
    // [개념] new() { ... } : 대상 형식으로 추론되는 new(여기서는 Packet). 중괄호는 "객체 이니셜라이저"로 속성을 한 번에 채운다.
    public static Packet Create(PacketType type, string? body = null, string? sender = null, string? target = null, bool success = true)
        => new() { Type = type, Body = body, Sender = sender, Target = target, Success = success };

    /// <summary>객체를 JSON으로 바꿔 Data에 넣는다. 체이닝용으로 자기 자신을 반환.</summary>
    // [개념] 제네릭 <T>: 어떤 타입이든 받을 수 있는 메서드. WithData(loginRequest), WithData(userList) 모두 가능.
    // [왜] this를 돌려주면 Packet.Create(...).WithData(x) 처럼 이어서(메서드 체이닝) 쓸 수 있어 코드가 짧아진다.
    public Packet WithData<T>(T value)
    {
        Data = PacketSerializer.ToJson(value);
        return this;
    }

    /// <summary>Data를 지정한 타입으로 꺼낸다. 비어 있거나 형식이 틀리면 null.</summary>
    // [개념] where T : class : T는 참조 형식(클래스)만 가능하다는 제약. 그래야 "실패하면 null"을 돌려줄 수 있다.
    // [왜] 예외 대신 null을 돌려주는 이유: 네트워크로 온 데이터는 언제든 깨져 있을 수 있다.
    //   받는 쪽은 `if (packet.GetData<LoginRequest>() is not { } req) return;` 처럼 간단히 걸러낸다.
    public T? GetData<T>() where T : class => PacketSerializer.FromJson<T>(Data);

    /// <summary>로그·디버깅 출력용 요약 문자열. 파일 조각은 내용 대신 크기만 보여 준다.</summary>
    // [개념] $"..." : 문자열 보간. {중괄호} 안의 식이 값으로 바뀐다.
    //   (Bin is null ? "" : ...) 는 삼항 연산자: 조건이 참이면 앞, 거짓이면 뒤 값.
    // 한 번 만든 전송용 바이트를 보관 (아래 ToWireBytes)
    private byte[]? _wire;

    /// <summary>
    /// 전송용 바이트([길이 4바이트][JSON]). 처음 한 번만 만들고 그다음부터는 보관한 것을 돌려준다.
    /// [왜] 방 메시지 1개를 300명에게 보내면 같은 패킷을 300번 JSON으로 바꾸게 된다(부하 테스트에서 CPU를 가장 많이 쓴 곳).
    ///      한 번만 바꾸고 300명이 같은 바이트 배열을 함께 쓰면 직렬화 비용이 1/300로 준다.
    /// [스레드] 여러 송신 스레드가 동시에 불러도 안전하다: 둘이 동시에 만들어도 결과가 같고,
    ///          CompareExchange로 먼저 넣은 것 하나만 남는다(lock 없이).
    /// [주의] 그래서 패킷은 "보내기 시작한 뒤에는 고치지 않는다". 고쳐도 이미 만든 바이트가 나간다.
    /// </summary>
    public byte[] ToWireBytes()
    {
        byte[]? wire = Volatile.Read(ref _wire);
        if (wire is not null) return wire;
        wire = PacketSerializer.ToBytes(this);
        return Interlocked.CompareExchange(ref _wire, wire, null) ?? wire;
    }

    public override string ToString() => $"{Type} from={SenderId} to={Target} body={Body}{(Bin is null ? "" : $" bin={Bin.Length}B")}";
}
