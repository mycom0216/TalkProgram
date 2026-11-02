using System.Buffers.Binary;
using ChatApp.Common.Protocol;

namespace ChatApp.Common.Utils;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] 네트워크 스트림(NetworkStream)에서 "패킷 1개"를 정확히 읽고 쓰는 도우미.
//   PacketSerializer가 정한 형식 [길이 4바이트][JSON]을 실제 소켓 입출력으로 옮긴다.
// [연결 관계]
//   - 서버: ClientSession의 수신 스레드(ReadPacket)와 송신 스레드(WritePacket)
//   - 클라이언트: ChatClient의 수신 스레드(ReadPacket)와 Send(WritePacket), 콘솔 클라이언트
// [스레드] ReadPacket은 데이터가 올 때까지 "블로킹"(멈춰서 기다림)하므로 반드시 전용 수신 스레드에서 부른다.
//   UI 스레드에서 부르면 화면이 얼어 버린다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 스트림에서 패킷 1개를 읽고 쓰는 도우미. 서버와 클라이언트가 똑같이 사용한다.
/// </summary>
// [왜] 서버와 클라이언트가 같은 코드를 쓰게 Common 프로젝트에 두었다. 양쪽 형식이 어긋날 일이 없다.
public static class NetworkHelper
{
    /// <summary>
    /// 패킷 1개를 읽을 때까지 블로킹한다. 상대가 연결을 정상 종료하면 null을 반환한다.
    /// </summary>
    /// <param name="stream">패킷을 읽어 올 스트림 (NetworkStream 또는 TLS를 씌운 SslStream).</param>
    /// <param name="checkTlsHello">
    /// true면 "TLS 인사말(ClientHello)이 왔는지" 확인한다. 평문 서버가 연결의 첫 패킷을 읽을 때만 쓴다.
    /// </param>
    // [개념] Stream: 바이트를 순서대로 읽고 쓰는 통로의 공통 부모 클래스. NetworkStream, FileStream 등이 모두 Stream이다.
    //   매개변수를 Stream으로 받아 두면 나중에 SslStream(TLS 암호화)으로 감싸도 이 코드를 그대로 쓸 수 있다.
    public static Packet? ReadPacket(Stream stream, bool checkTlsHello = false)
    {
        // ① 헤더(4바이트) 읽기 → 이번 패킷 본문이 몇 바이트인지 알아낸다
        byte[] header = new byte[PacketSerializer.HeaderSize];
        try
        {
            // ReadExactly: 요청한 바이트 수가 다 찰 때까지 읽는다(TCP 분할 수신 대응).
            // [주의] 일반 Read는 "있는 만큼만" 읽고 돌아온다(4바이트를 원해도 2바이트만 올 수 있음).
            //   그 경우를 직접 반복문으로 처리하지 않아도 되게 .NET 7부터 ReadExactly가 생겼다.
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException)
        {
            // [개념] 상대가 소켓을 정상적으로 닫으면 스트림이 "끝(EOF)"이 되어 더 읽을 바이트가 없다.
            //   패킷 경계(헤더 시작)에서 끝났다면 정상 종료로 보고 null을 돌려준다.
            return null; // 상대가 연결을 닫음
        }

        // [왜] TLS를 켠 클라이언트가 평문 서버에 붙으면, 첫 바이트(16 03 01 ...)를 길이로 읽어 약 66KB짜리 패킷으로 착각하고
        //      오지 않을 나머지 바이트를 영원히 기다린다(클라이언트는 10초 뒤 시간 초과). 바로 알아보고 끊는다.
        // [개념] TLS 레코드 머리: 0x16(핸드셰이크) + 0x03 0x01~0x04(버전). 첫 패킷(로그인 요청 등)은 작아서 이런 길이가 나올 수 없다.
        if (checkTlsHello && header[0] == 0x16 && header[1] == 0x03 && header[2] is >= 0x01 and <= 0x04)
            throw new InvalidDataException("TLS 연결 요청을 받았습니다 (이 서버는 평문 모드, Tls.Enabled=false)");

        // ② 길이 검사: 음수나 너무 큰 값이면 데이터가 깨졌거나 악의적인 상대다 → 예외로 연결을 끊게 한다
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > PacketSerializer.MaxBodySize)
            throw new InvalidDataException($"잘못된 패킷 길이: {length}");

        // ③ 본문을 정확히 length 바이트만큼 읽는다. 다음 패킷의 바이트는 건드리지 않으므로 경계가 유지된다.
        //   (본문 도중에 끊기면 ReadExactly가 EndOfStreamException을 던지고, 호출한 쪽 catch에서 연결 종료 처리)
        byte[] body = new byte[length];
        stream.ReadExactly(body);
        return PacketSerializer.FromBody(body);
    }

    /// <summary>
    /// 패킷 1개를 쓴다. 스레드 안전하지 않으므로 호출하는 쪽에서 lock을 걸어야 한다.
    /// </summary>
    // [주의] 두 스레드가 동시에 같은 스트림에 Write하면 A 패킷 중간에 B 패킷 바이트가 끼어들어 상대가 해석할 수 없게 된다.
    //   그래서 서버는 "소켓에 쓰는 스레드를 하나(송신 스레드)로" 정했고, 클라이언트는 Send에서 lock을 건다.
    public static void WritePacket(Stream stream, Packet packet)
    {
        byte[] data = packet.ToWireBytes(); // 같은 패킷을 여러 명에게 보낼 때는 처음 만든 바이트를 다시 쓴다
        stream.Write(data);
        // [개념] Flush: 버퍼에 모아 둔 데이터를 지금 바로 내보내라는 요청. 채팅은 지연 없이 가야 하므로 매번 호출한다.
        stream.Flush();
    }
}
