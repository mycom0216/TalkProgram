using System.Buffers.Binary;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatApp.Common.Protocol;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] Packet 객체 ⇄ 네트워크로 보낼 바이트 배열(byte[]) 변환을 담당한다.
//   - 보낼 때: Packet → JSON(UTF-8 바이트) → 앞에 길이 4바이트를 붙인 byte[]
//   - 받을 때: (NetworkHelper가 길이만큼 읽어 온) JSON 바이트 → Packet
// [연결 관계] NetworkHelper.WritePacket/ReadPacket이 이 클래스를 호출한다.
//   Packet.WithData/GetData도 ToJson/FromJson을 써서 같은 JSON 설정을 공유한다.
// [스레드] 상태(필드)가 읽기 전용 설정(Options)뿐이라 여러 스레드가 동시에 불러도 안전하다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Packet &lt;-&gt; byte[] 변환.
/// 전송 형식: [본문 길이 int32, 리틀엔디언 4바이트] + [UTF-8 JSON 본문]
/// 길이 헤더가 있어야 TCP에서 패킷이 붙거나(병합) 잘려서(분할) 와도 경계를 정확히 복원할 수 있다.
/// </summary>
// [개념] TCP는 "바이트의 강물"이다. 보낸 쪽이 Write를 3번 해도 받는 쪽 Read 한 번에 3개가 붙어서 오거나,
//   하나가 반쪽씩 두 번에 나뉘어 올 수 있다(메시지 경계가 없음). 그래서 "이번 패킷은 몇 바이트다"라는
//   길이 헤더를 앞에 붙여 받는 쪽이 정확히 그만큼만 잘라 읽게 한다. 이것을 "길이 접두(length-prefix) 프레이밍"이라 한다.
// [개념] static class: 인스턴스를 만들 수 없고 정적 멤버만 가진 클래스. 상태 없는 도우미 함수 모음에 쓴다.
public static class PacketSerializer
{
    /// <summary>길이 헤더 크기(int32 = 4바이트).</summary>
    public const int HeaderSize = 4;

    // [주의] 상대가 길이 칸에 20억 같은 엉터리 값을 보내면 그만큼 메모리를 잡으려다 서버가 죽을 수 있다.
    //   그래서 최대 크기를 정해 두고 넘으면 연결을 끊는다(방어적 프로그래밍).
    //   파일은 256KB 조각으로 나눠 보내므로(Base64로 약 350KB) 1MB면 충분하다.
    public const int MaxBodySize = 1024 * 1024; // 1MB. 비정상 길이로 메모리를 잡아먹는 것을 막는다.

    // [왜] JsonSerializerOptions는 만들 때 비용이 크고 내부에 캐시를 갖고 있어서, 매번 new 하지 않고
    //   static readonly로 하나만 만들어 재사용하는 것이 권장 방식이다(스레드 안전하게 공유 가능).
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }, // enum을 숫자 대신 "ChatMsg" 같은 문자열로
        // [왜] enum을 숫자(예: 7)로 보내면 PacketType 중간에 새 값을 끼워 넣는 순간 번호가 밀려
        //   옛 클라이언트와 새 서버가 서로 다른 뜻으로 해석한다. 이름 문자열이면 순서가 바뀌어도 안전하고 로그도 읽기 쉽다.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 한글을 \uXXXX로 바꾸지 않고 그대로(패킷 크기↓, 디버깅 편함)
        // [주의] 이름의 "Unsafe"는 HTML 페이지에 그대로 끼워 넣을 때 위험하다는 뜻이다. 여기서는 웹 페이지가 아니라
        //   우리 프로그램끼리 주고받는 데이터이므로 문제없다.
    };

    /// <summary>Packet → [길이 4바이트][JSON] 바이트 배열.</summary>
    public static byte[] ToBytes(Packet packet)
    {
        // [개념] SerializeToUtf8Bytes: 객체를 JSON 텍스트로 만들되, string을 거치지 않고 곧바로 UTF-8 바이트로 만든다(더 빠름).
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(packet, Options);
        if (body.Length > MaxBodySize)
            throw new InvalidDataException($"패킷이 너무 큽니다: {body.Length} bytes");

        // 헤더 + 본문을 한 배열에 담는다. [왜] 따로 두 번 Write하는 것보다 한 번에 쓰는 편이 네트워크 효율이 좋다.
        byte[] buffer = new byte[HeaderSize + body.Length];
        // [개념] 리틀엔디언: 여러 바이트로 된 숫자를 "작은 자리 바이트부터" 저장하는 순서. (예: 0x00000105 → 05 01 00 00)
        //   보내는 쪽과 받는 쪽이 같은 순서를 약속해야 같은 숫자로 읽힌다. BinaryPrimitives는 CPU 종류와 관계없이
        //   항상 지정한 순서로 써 주므로, 서로 다른 컴퓨터끼리도 안전하다.
        BinaryPrimitives.WriteInt32LittleEndian(buffer, body.Length);
        body.CopyTo(buffer, HeaderSize); // 본문은 4번 위치부터 복사
        return buffer;
    }

    /// <summary>JSON 본문 바이트 → Packet. 내용이 null("null" 문자열)이면 예외.</summary>
    // [개념] ReadOnlySpan<byte>: 배열의 일부를 복사 없이 가리키는 "읽기 전용 창". 메모리 복사를 줄인다.
    // [개념] ?? throw : 왼쪽이 null이면 오른쪽(예외 던지기)을 실행하는 null 병합 연산자.
    // [왜] 잘못된 데이터는 InvalidDataException으로 알려서, 호출한 쪽(수신 스레드)이 "이상한 클라이언트"로 보고 연결을 끊게 한다.
    public static Packet FromBody(ReadOnlySpan<byte> body)
        => JsonSerializer.Deserialize<Packet>(body, Options)
           ?? throw new InvalidDataException("패킷 본문이 비어 있습니다.");

    /// <summary>Packet.Data에 넣을 객체 → JSON 문자열.</summary>
    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Packet.Data의 JSON → 객체. 비어 있거나 잘못된 형식이면 null.</summary>
    // [왜] 여기서는 예외를 삼키고 null을 돌려준다. Data는 패킷 안의 "부가 정보"라서, 하나가 깨졌다고
    //   연결 전체를 끊기보다 그 패킷만 무시하는 편이 낫기 때문이다(호출하는 쪽에서 null 검사).
    public static T? FromJson<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
