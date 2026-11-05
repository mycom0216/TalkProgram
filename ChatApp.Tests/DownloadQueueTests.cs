using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Transfer;

namespace ChatApp.Tests;

// 클라이언트 내려받기 대기열 (FileTransferService): 동시에 3개까지 요청하고 나머지는 "대기 중"으로 기다린다.
// 서버 없이, 보낸 패킷을 목록에 모으는 가짜 send로 확인한다.
public class DownloadQueueTests : IDisposable
{
    private readonly List<Packet> _sent = [];
    private readonly FileTransferService _svc;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chatapp-dlq-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _started = [], _done = [], _failed = [];

    // 파일 전송 도구와 가짜 전송 함수를 준비합니다. 실제 서버에 접속하지 않아도 테스트할 수 있습니다.
    public DownloadQueueTests()
    {
        Directory.CreateDirectory(_dir);
        _svc = new FileTransferService(p => { lock (_sent) _sent.Add(p); return true; });
        _svc.DownloadStarted += id => { lock (_started) _started.Add(id); };
        _svc.DownloadCompleted += (id, _) => { lock (_done) _done.Add(id); };
        _svc.DownloadFailed += (id, _) => { lock (_failed) _failed.Add(id); };
    }

    // 테스트가 끝나면 진행 중인 전송을 취소하고 임시 파일을 지웁니다.
    public void Dispose()
    {
        _svc.CancelAll("테스트 끝");
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // 여러 개의 파일 받기를 시작하고, 각 파일을 구분할 번호를 돌려줍니다.
    private List<string> Start(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => FileRules.NewFileId()).ToList();
        foreach (string id in ids) Assert.True(_svc.StartDownload(id, Path.Combine(_dir, id + ".bin")));
        return ids;
    }

    // 기록해 둔 전송 내역에서 지정한 종류의 패킷이 몇 개인지 셉니다.
    private int Count(PacketType type) { lock (_sent) return _sent.Count(p => p.Type == type); }

    // 지정한 파일을 받으려고 서버에 보낸 요청 번호를 찾습니다.
    private string RequestIdOf(string id) { lock (_sent) return _sent.Last(p => p.Type == PacketType.FileDownloadReq && p.Target == id).Body!; }

    // 서버가 파일 설명, 파일 조각, 전송 완료 신호를 차례로 보낸 상황을 흉내 냅니다.
    private void Complete(string id)
    {
        string rq = RequestIdOf(id);
        _svc.TryHandle(Packet.Create(PacketType.FileDownloadRes, rq, target: id, success: true).WithData(new SharedFile { FileId = id, FileName = "a.bin", Size = 3 }));
        _svc.TryHandle(new Packet { Type = PacketType.FileChunk, Target = id, Body = rq, Bin = [1, 2, 3] });
        _svc.TryHandle(Packet.Create(PacketType.FileDownloadEnd, rq, target: id));
    }

// ===============
    // 이 테스트는 “다섯개를 받으면 세개만 요청하고 둘은 대기한다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 다섯개를_받으면_세개만_요청하고_둘은_대기한다()
    {
        List<string> ids = Start(5);
        Assert.Equal(FileRules.MaxActiveTransfers, Count(PacketType.FileDownloadReq));
        Assert.False(_svc.IsDownloadQueued(ids[0]));
        Assert.True(_svc.IsDownloadQueued(ids[3]));
        Assert.True(_svc.IsDownloadQueued(ids[4]));
        Assert.Equal(ids.Take(3), _started);
    }

// ===============
    // 이 테스트는 “하나가 끝나면 대기하던 다음 파일을 자동으로 요청한다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 하나가_끝나면_대기하던_다음_파일을_자동으로_요청한다()
    {
        List<string> ids = Start(5);
        Complete(ids[0]);
        Assert.Equal(4, Count(PacketType.FileDownloadReq));
        Assert.False(_svc.IsDownloadQueued(ids[3]));
        Assert.Contains(ids[3], _started);
        foreach (string id in ids.Skip(1)) Complete(id);
        Assert.Equal(5, _done.Count);
        Assert.Empty(_failed);
        Assert.True(File.Exists(Path.Combine(_dir, ids[4] + ".bin")));
    }

// ===============
    // 이 테스트는 “서버가 동시에 제한으로 거절하면 실패하지 않고 다시 요청한다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 서버가_동시에_제한으로_거절하면_실패하지_않고_다시_요청한다()
    {
        List<string> ids = Start(1);
        string first = RequestIdOf(ids[0]);
        _svc.TryHandle(Packet.Create(PacketType.FileDownloadRes, "파일은 동시에 3개까지 받을 수 있습니다.", target: ids[0], success: false));
        DateTime end = DateTime.UtcNow.AddSeconds(3);
        while (Count(PacketType.FileDownloadReq) < 2 && DateTime.UtcNow < end) Thread.Sleep(50);
        Assert.Equal(2, Count(PacketType.FileDownloadReq));
        Assert.NotEqual(first, RequestIdOf(ids[0])); // 새 요청 번호 (늦게 온 옛 응답과 섞이지 않게)
        Assert.Empty(_failed);
        Complete(ids[0]);
        Assert.Single(_done);
    }

// ===============
    // 이 테스트는 “대기중인 파일을 취소하면 서버에 아무것도 보내지 않는다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 대기중인_파일을_취소하면_서버에_아무것도_보내지_않는다()
    {
        List<string> ids = Start(4);
        Assert.True(_svc.CancelDownload(ids[3]));
        Assert.Equal(0, Count(PacketType.FileDownloadCancel));
        Complete(ids[0]);
        Assert.Equal(3, Count(PacketType.FileDownloadReq)); // 취소한 4번째는 요청되지 않는다
    }

// ===============
    // 이 테스트는 “받는중인 파일을 취소하면 서버에 알리고 다음 파일이 시작된다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 받는중인_파일을_취소하면_서버에_알리고_다음_파일이_시작된다()
    {
        List<string> ids = Start(4);
        Assert.True(_svc.CancelDownload(ids[1]));
        Assert.Equal(1, Count(PacketType.FileDownloadCancel));
        Assert.Equal(4, Count(PacketType.FileDownloadReq));
        Assert.False(_svc.IsDownloadQueued(ids[3]));
    }
}
