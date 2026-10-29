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

    public DownloadQueueTests()
    {
        Directory.CreateDirectory(_dir);
        _svc = new FileTransferService(p => { lock (_sent) _sent.Add(p); return true; });
        _svc.DownloadStarted += id => { lock (_started) _started.Add(id); };
        _svc.DownloadCompleted += (id, _) => { lock (_done) _done.Add(id); };
        _svc.DownloadFailed += (id, _) => { lock (_failed) _failed.Add(id); };
    }

    public void Dispose()
    {
        _svc.CancelAll("테스트 끝");
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private List<string> Start(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => FileRules.NewFileId()).ToList();
        foreach (string id in ids) Assert.True(_svc.StartDownload(id, Path.Combine(_dir, id + ".bin")));
        return ids;
    }

    private int Count(PacketType type) { lock (_sent) return _sent.Count(p => p.Type == type); }
    private string RequestIdOf(string id) { lock (_sent) return _sent.Last(p => p.Type == PacketType.FileDownloadReq && p.Target == id).Body!; }

    private void Complete(string id)
    {
        string rq = RequestIdOf(id);
        _svc.TryHandle(Packet.Create(PacketType.FileDownloadRes, rq, target: id, success: true).WithData(new SharedFile { FileId = id, FileName = "a.bin", Size = 3 }));
        _svc.TryHandle(new Packet { Type = PacketType.FileChunk, Target = id, Body = rq, Bin = [1, 2, 3] });
        _svc.TryHandle(Packet.Create(PacketType.FileDownloadEnd, rq, target: id));
    }

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

    [Fact]
    public void 대기중인_파일을_취소하면_서버에_아무것도_보내지_않는다()
    {
        List<string> ids = Start(4);
        Assert.True(_svc.CancelDownload(ids[3]));
        Assert.Equal(0, Count(PacketType.FileDownloadCancel));
        Complete(ids[0]);
        Assert.Equal(3, Count(PacketType.FileDownloadReq)); // 취소한 4번째는 요청되지 않는다
    }

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
