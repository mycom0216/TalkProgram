using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Transfer;
using ChatApp.Server.Data;

namespace ChatApp.Tests;

// 추가 기능(답장·공감·검색·업데이트 안내)과 클라이언트 개선(파일 대량 큐, 실행 파일 경고)에 대한 테스트.
public class FeatureTests
{
    /// <summary>보내는 바이트([길이 4][JSON])로 바꿨다가 다시 읽는다 (실제 통신과 같은 경로).</summary>
    private static Packet RoundTrip(Packet packet) => PacketSerializer.FromBody(PacketSerializer.ToBytes(packet).AsSpan(4));

    // ───────── 실행 파일 경고 확장자 ─────────

// ===============
    // 이 테스트는 “실행으로 이어지는 형식은 경고 대상이다” 상황이 제대로 처리되는지 확인합니다.
    [Theory]
    [InlineData("설치.iso")]
    [InlineData("바로가기.url")]
    [InlineData("도움말.chm")]
    [InlineData("보고서.docm")]
    [InlineData("정산.XLSM")]
    [InlineData("노트.one")]
    public void 실행으로_이어지는_형식은_경고_대상이다(string name) => Assert.True(FileRules.IsExecutable(name), name);

// ===============
    // 이 테스트는 “일반 문서는 경고하지 않는다” 상황이 제대로 처리되는지 확인합니다.
    [Theory]
    [InlineData("보고서.docx")]
    [InlineData("사진.jpg")]
    [InlineData("자료.pdf")]
    public void 일반_문서는_경고하지_않는다(string name) => Assert.False(FileRules.IsExecutable(name));

    // ───────── 패킷: 메시지 번호·답장 ─────────

// ===============
    // 이 테스트는 “메시지 번호와 답장 정보가 전송된다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 메시지_번호와_답장_정보가_전송된다()
    {
        var packet = Packet.Create(PacketType.ChatMsg, "네 확인했습니다", target: "3");
        packet.MessageId = 1234;
        packet.ReplyTo = new ReplyInfo { MessageId = 1200, SenderName = "홍길동 대리", Text = "회의 2시" };

        Packet back = RoundTrip(packet);
        Assert.Equal(1234L, back.MessageId);
        Assert.Equal(1200L, back.ReplyTo!.MessageId);
        Assert.Equal("홍길동 대리", back.ReplyTo.SenderName);
    }

// ===============
    // 이 테스트는 “번호가 없는 패킷은 MessageId 칸을 보내지 않는다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 번호가_없는_패킷은_MessageId_칸을_보내지_않는다()
    {
        // 패킷마다 쓸데없는 "MessageId":0 이 붙지 않게 (Ping처럼 자주 오가는 패킷이 가벼워야 한다)
        string json = System.Text.Encoding.UTF8.GetString(PacketSerializer.ToBytes(Packet.Create(PacketType.Ping)).AsSpan(4));
        Assert.DoesNotContain("MessageId", json);
        Assert.DoesNotContain("ReplyTo", json);
    }

// ===============
    // 이 테스트는 “공감은 정해진 여섯 가지다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 공감은_정해진_여섯_가지다()
    {
        Assert.Equal(6, ReactionRequest.Allowed.Length);
        Assert.Equal(ReactionRequest.Allowed.Length, ReactionRequest.Allowed.Distinct().Count());
    }

// ===============
    // 이 테스트는 “서버 안내에 최신 클라이언트 버전이 실린다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 서버_안내에_최신_클라이언트_버전이_실린다()
    {
        var info = new ServerInfo { LatestClientVersion = "1.3.0", ClientUpdateUrl = @"\\fs\share\setup.exe" };
        Packet back = RoundTrip(Packet.Create(PacketType.ServerInfo).WithData(info));
        ServerInfo read = back.GetData<ServerInfo>()!;
        Assert.Equal("1.3.0", read.LatestClientVersion);
        Assert.Equal(@"\\fs\share\setup.exe", read.ClientUpdateUrl);
    }

// ===============
    // 이 테스트는 “기기별 자동로그인 요청과 파일목록 페이지가 직렬화된다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 기기별_자동로그인_요청과_파일목록_페이지가_직렬화된다()
    {
        const string deviceId = "0123456789abcdef0123456789abcdef";
        Packet login = RoundTrip(Packet.Create(PacketType.TokenLoginReq).WithData(new TokenLoginRequest
        {
            EmployeeNo = "A0001", Token = new string('t', 43), DeviceId = deviceId, ProtocolVersion = 3,
        }));
        Assert.Equal(deviceId, login.GetData<TokenLoginRequest>()!.DeviceId);

        var response = new RoomFilesBatch
        {
            RoomId = 7,
            HasMore = true,
            Files = [new SharedFile { FileId = "0123456789abcdef0123456789abcdef", FileName = "설계.pdf", RoomId = 7, SenderNo = "A0001", SentAt = DateTime.UtcNow }],
        };
        Packet files = RoundTrip(Packet.Create(PacketType.RoomFilesRes).WithData(response));
        RoomFilesBatch read = files.GetData<RoomFilesBatch>()!;
        Assert.Equal(7, read.RoomId);
        Assert.True(read.HasMore);
        Assert.Equal("설계.pdf", Assert.Single(read.Files).FileName);
    }

    // ───────── 검색: LIKE 특수 문자 ─────────

// ===============
    // 이 테스트는 “검색어의 특수문자는 글자 그대로 찾는다” 상황이 제대로 처리되는지 확인합니다.
    [Theory]
    [InlineData("100%", "100\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("c:\\temp", "c:\\\\temp")]
    [InlineData("보고서", "보고서")]
    public void 검색어의_특수문자는_글자_그대로_찾는다(string input, string expected)
        => Assert.Equal(expected, MessageRepository.EscapeLike(input));

// ===============
    // 이 테스트는 “답장 미리보기는 한 줄로 짧게 자른다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 답장_미리보기는_한_줄로_짧게_자른다()
    {
        string preview = MessageRepository.Preview("첫 줄\n둘째 줄 " + new string('가', 200));
        Assert.DoesNotContain("\n", preview);
        Assert.True(preview.Length <= ReplyInfo.MaxPreviewLength);
    }

    // ───────── 파일 보내기: 대량 큐 ─────────

// ===============
    // 이 테스트는 “파일 조각과 끝 알림은 대량 큐로 순서대로 나간다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public async Task 파일_조각과_끝_알림은_대량_큐로_순서대로_나간다()
    {
        string path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, new byte[FileRules.ChunkSize * 2 + 100]); // 조각 3개
        try
        {
            var normal = new List<Packet>();
            var bulk = new List<Packet>();
            var requested = new SemaphoreSlim(0);
            var service = new FileTransferService(
                p => { lock (normal) normal.Add(p); if (p.Type == PacketType.FileUploadReq) requested.Release(); return true; },
                (p, _) => { lock (bulk) bulk.Add(p); return true; });
            var sent = new TaskCompletionSource<string>();
            service.UploadSent += id => sent.TrySetResult(id);

            string fileId = service.StartUpload(path, roomId: 1, toEmployeeNo: null);
            Assert.True(await requested.WaitAsync(5000), "업로드 요청이 나가지 않음");
            service.TryHandle(Packet.Create(PacketType.FileUploadRes, target: fileId, success: true)); // 서버가 허락
            Assert.Equal(fileId, await sent.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            // 일반 큐에는 요청만, 조각 3개와 끝 알림은 모두 대량 큐에 "순서대로" (끝 알림이 조각을 앞지르면 서버가 실패 처리한다)
            Assert.Equal(new[] { PacketType.FileUploadReq }, normal.Select(p => p.Type).ToArray());
            Assert.Equal(new[] { PacketType.FileChunk, PacketType.FileChunk, PacketType.FileChunk, PacketType.FileUploadEnd },
                         bulk.Select(p => p.Type).ToArray());

            // 다 보낸 뒤 서버 확인 전에 끊기면 "저장됐는지 모른다"고 알려 준다
            string? reason = null;
            service.UploadFailed += (_, r) => reason = r;
            service.CancelAll("서버와 연결이 끊어졌습니다.");
            Assert.Contains("확인하지 못했습니다", reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

// ===============
    // 이 테스트는 “대량 큐가 실패하면 업로드도 실패로 끝난다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public async Task 대량_큐가_실패하면_업로드도_실패로_끝난다()
    {
        string path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, new byte[1000]);
        try
        {
            var requested = new SemaphoreSlim(0);
            var service = new FileTransferService(
                p => { if (p.Type == PacketType.FileUploadReq) requested.Release(); return true; },
                (_, _) => false); // 연결이 끊긴 상황
            var failed = new TaskCompletionSource<string>();
            service.UploadFailed += (_, reason) => failed.TrySetResult(reason);

            string fileId = service.StartUpload(path, roomId: 1, toEmployeeNo: null);
            Assert.True(await requested.WaitAsync(5000));
            service.TryHandle(Packet.Create(PacketType.FileUploadRes, target: fileId, success: true));
            Assert.Contains("끊어졌습니다", await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
