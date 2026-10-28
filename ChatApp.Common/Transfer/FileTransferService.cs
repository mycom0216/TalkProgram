using System.Collections.Concurrent;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Common.Transfer;

/// <summary>
/// 클라이언트 쪽 파일 올리기/내려받기 엔진. WinForms와 콘솔 클라이언트가 함께 쓴다.
///
/// 스레드 구성
/// - 올리기: 파일마다 작업 스레드(Upload-xxxx)가 256KB씩 읽어 보낸다. 조각 사이사이에 채팅 패킷도 나가므로 채팅이 멈추지 않는다.
///           동시에 3개까지만 보내고 나머지는 세마포어에서 차례를 기다린다.
/// - 내려받기: 조각은 수신 스레드에서 TryHandle로 받아 바로 파일에 쓴다(화면 스레드로 넘기지 않아 UI가 느려지지 않음).
/// - 이벤트는 작업 스레드 또는 수신 스레드에서 발생한다. 화면을 바꾸려면 BeginInvoke로 넘길 것.
///   진행률 이벤트는 1% 바뀔 때만 발생한다(초당 수천 번 화면을 다시 그리지 않도록).
/// </summary>
public sealed class FileTransferService
{
    private sealed class UploadJob(string fileId, string path)
    {
        public string FileId { get; } = fileId;
        public string Path { get; } = path;
        public readonly TaskCompletionSource<string?> Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously); // null = 허락, 문자열 = 거절 이유
        public volatile string? FailReason; // 보내는 도중 서버가 실패를 알려 오면 채워진다
    }

    private sealed class DownloadJob(string fileId, string destination)
    {
        public string FileId { get; } = fileId;
        public string Destination { get; } = destination;
        public string TempPath { get; } = destination + ".part";
        // 요청마다 새로 만드는 짧은 ID. 서버가 이 요청의 패킷(시작·조각·끝)에 그대로 붙여 보낸다.
        // [왜] 받기를 취소하고 바로 같은 파일을 다시 받으면, 앞 요청의 조각이 늦게 도착할 수 있다.
        //      파일 ID만으로는 둘을 구분할 수 없어서 새 파일에 옛 조각이 섞여 파일이 깨진다 → 요청 ID로 걸러낸다.
        public string RequestId { get; } = Guid.NewGuid().ToString("N")[..8];
        public FileStream? Stream;
        public long Size;
        public long Received;
        public int LastPercent = -1;
    }

    private static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<Packet, bool> _send;
    private readonly ConcurrentDictionary<string, UploadJob> _uploads = new();
    private readonly SemaphoreSlim _uploadSlots = new(FileRules.MaxActiveTransfers);
    private readonly object _downloadLock = new();
    private readonly Dictionary<string, DownloadJob> _downloads = [];

    /// <param name="send">패킷 전송 함수(스레드 안전해야 함). 연결이 끊겼으면 false를 돌려줘야 한다.</param>
    public FileTransferService(Func<Packet, bool> send) => _send = send;

    /// <summary>파일 1개 최대 크기. 서버 안내(ServerInfo)를 받으면 바꿔 준다.</summary>
    public long MaxFileSize { get; set; } = FileRules.DefaultMaxFileSize;

    public event Action<string, int>? UploadProgress;      // (파일 ID, 0~100)
    public event Action<string>? UploadSent;               // 다 보냄 → 곧 FileMsg가 온다
    public event Action<string, string>? UploadFailed;     // (파일 ID, 이유)
    public event Action<string, int>? DownloadProgress;
    public event Action<string, string>? DownloadCompleted; // (파일 ID, 저장 경로)
    public event Action<string, string>? DownloadFailed;

    public bool IsDownloading(string fileId)
    {
        lock (_downloadLock) return _downloads.ContainsKey(fileId);
    }

    // ───────────────────────── 올리기 ─────────────────────────

    /// <summary>
    /// 파일 보내기를 시작하고 파일 ID를 돌려준다(실제 전송은 작업 스레드).
    /// 방이면 roomId, 1:1이면 toEmployeeNo. fileName을 주면 그 이름으로 보낸다(붙여 넣은 캡처 등).
    /// 크기·존재 여부가 틀리면 ArgumentException.
    /// </summary>
    public string StartUpload(string path, int? roomId, string? toEmployeeNo, byte[]? thumbnail = null, string? fileName = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new ArgumentException($"파일을 찾을 수 없습니다: {path}");
        if (info.Length == 0) throw new ArgumentException($"빈 파일은 보낼 수 없습니다: {info.Name}");
        if (info.Length > MaxFileSize)
            throw new ArgumentException($"{info.Name}: 파일이 너무 큽니다 ({FileRules.FormatSize(info.Length)}). 최대 {FileRules.FormatSize(MaxFileSize)}까지 보낼 수 있습니다.");
        if (thumbnail is { Length: > FileRules.MaxThumbnailBytes }) thumbnail = null;

        string fileId = FileRules.NewFileId();
        var job = new UploadJob(fileId, path);
        _uploads[fileId] = job;

        var offer = new FileOffer
        {
            FileId = fileId,
            FileName = FileRules.SanitizeFileName(fileName ?? info.Name),
            Size = info.Length,
            RoomId = roomId,
            ToEmployeeNo = toEmployeeNo,
            Thumbnail = thumbnail,
        };
        var thread = new Thread(() => RunUpload(job, offer)) { IsBackground = true, Name = $"Upload-{fileId[..6]}" };
        thread.Start();
        return fileId;
    }

    private void RunUpload(UploadJob job, FileOffer offer)
    {
        _uploadSlots.Wait(); // 동시에 3개까지
        try
        {
            if (!_uploads.ContainsKey(job.FileId)) return; // 차례를 기다리는 동안 취소됨
            if (!_send(Packet.Create(PacketType.FileUploadReq).WithData(offer)))
            {
                Fail(job, "서버와 연결이 끊어졌습니다.");
                return;
            }

            // 서버의 허락(FileUploadRes)을 기다린다. 수신 스레드가 TryHandle에서 결과를 넣어 준다.
            if (!job.Accepted.Task.Wait(AcceptTimeout))
            {
                Fail(job, "서버가 응답하지 않습니다.");
                return;
            }
            if (job.Accepted.Task.Result is string rejected)
            {
                Fail(job, rejected);
                return;
            }

            using var stream = new FileStream(job.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            if (stream.Length != offer.Size)
            {
                Fail(job, "보내는 도중 파일이 바뀌었습니다.");
                return;
            }

            long sent = 0;
            int lastPercent = -1;
            while (true)
            {
                if (job.FailReason is string reason) { Fail(job, reason); return; } // 서버가 중간에 취소

                byte[] buffer = new byte[FileRules.ChunkSize];
                int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                if (read == 0) break;
                if (read < buffer.Length) Array.Resize(ref buffer, read);

                if (!_send(new Packet { Type = PacketType.FileChunk, Target = job.FileId, Bin = buffer }))
                {
                    Fail(job, "보내는 도중 서버와 연결이 끊어졌습니다.");
                    return;
                }
                sent += read;
                int percent = (int)(sent * 100 / offer.Size);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    UploadProgress?.Invoke(job.FileId, percent);
                }
            }

            if (sent != offer.Size)
            {
                Fail(job, "보내는 도중 파일이 바뀌었습니다.");
                return;
            }
            _send(Packet.Create(PacketType.FileUploadEnd, target: job.FileId));
            UploadSent?.Invoke(job.FileId);
            // 이후 서버가 저장에 실패하면 FileUploadRes(실패)가 오므로 job은 FileMsg를 받을 때까지 남겨 둔다
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(job, $"파일을 읽을 수 없습니다: {ex.Message}");
        }
        finally
        {
            _uploadSlots.Release();
        }
    }

    private void Fail(UploadJob job, string reason)
    {
        if (_uploads.TryRemove(job.FileId, out _))
            UploadFailed?.Invoke(job.FileId, reason);
    }

    // ───────────────────────── 내려받기 ─────────────────────────

    /// <summary>내려받기 요청. 같은 파일을 이미 받는 중이면 false.</summary>
    public bool StartDownload(string fileId, string destination)
    {
        DownloadJob job;
        lock (_downloadLock)
        {
            if (_downloads.ContainsKey(fileId)) return false;
            job = new DownloadJob(fileId, destination);
            _downloads[fileId] = job;
        }

        if (!_send(Packet.Create(PacketType.FileDownloadReq, body: job.RequestId, target: fileId)))
        {
            FinishDownload(fileId, "서버와 연결이 끊어졌습니다.");
            return false;
        }
        return true;
    }

    // ───────────────────────── 수신 (수신 스레드) ─────────────────────────

    /// <summary>
    /// 파일 전송용 패킷이면 여기서 처리하고 true. 화면에 보여 줄 패킷(FileMsg 등)은 false.
    /// 수신 스레드에서 호출한다.
    /// </summary>
    public bool TryHandle(Packet packet)
    {
        string fileId = packet.Target ?? "";
        switch (packet.Type)
        {
            case PacketType.FileUploadRes:
                if (_uploads.TryGetValue(fileId, out UploadJob? up))
                {
                    if (packet.Success) up.Accepted.TrySetResult(null);
                    else
                    {
                        string reason = packet.Body ?? "파일을 보내지 못했습니다.";
                        if (!up.Accepted.TrySetResult(reason))
                        {
                            // 이미 보내는 중이거나 다 보낸 뒤의 실패
                            up.FailReason = reason;
                            Fail(up, reason);
                        }
                    }
                }
                return true;

            case PacketType.FileMsg:
                _uploads.TryRemove(packet.GetData<SharedFile>()?.FileId ?? "", out _); // 내 파일이 무사히 올라감
                return false; // 대화에 표시해야 하므로 화면으로 넘긴다

            case PacketType.FileDownloadRes:
                if (!packet.Success) FinishDownload(fileId, packet.Body ?? "파일을 받지 못했습니다.");
                else BeginDownload(fileId, packet.Body, packet.GetData<SharedFile>()?.Size ?? 0);
                return true;

            case PacketType.FileChunk:
                WriteChunk(fileId, packet.Body, packet.Bin ?? []);
                return true;

            case PacketType.FileDownloadEnd:
                lock (_downloadLock)
                {
                    if (!_downloads.TryGetValue(fileId, out DownloadJob? job) || job.RequestId != packet.Body) return true; // 취소한 옛 요청의 끝
                    if (job.Received != job.Size)
                    {
                        FinishDownload(fileId, "파일이 끝까지 오지 않았습니다.");
                        return true;
                    }
                }
                FinishDownload(fileId, null);
                return true;
        }
        return false;
    }

    private void BeginDownload(string fileId, string? requestId, long size)
    {
        string? error = null;
        lock (_downloadLock)
        {
            if (!_downloads.TryGetValue(fileId, out DownloadJob? job) || job.RequestId != requestId) return;
            job.Size = size;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.TempPath))!);
                job.Stream = new FileStream(job.TempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"저장할 수 없습니다: {ex.Message}";
            }
        }
        if (error is not null) FinishDownload(fileId, error);
    }

    private void WriteChunk(string fileId, string? requestId, byte[] data)
    {
        string? error = null;
        int percent = -1;
        lock (_downloadLock)
        {
            // 취소한 받기의 늦게 온 조각, 또는 다른(옛) 요청의 조각은 버린다
            if (!_downloads.TryGetValue(fileId, out DownloadJob? job) || job.Stream is null || job.RequestId != requestId) return;
            try
            {
                job.Stream.Write(data);
                job.Received += data.Length;
                int p = job.Size > 0 ? (int)(job.Received * 100 / job.Size) : 100;
                if (p != job.LastPercent) percent = job.LastPercent = p;
            }
            catch (IOException ex)
            {
                error = $"디스크에 쓰지 못했습니다: {ex.Message}";
            }
        }
        if (error is not null) FinishDownload(fileId, error);
        else if (percent >= 0) DownloadProgress?.Invoke(fileId, percent);
    }

    /// <summary>내려받기 마무리. error가 null이면 성공(임시 파일 → 최종 경로).</summary>
    private void FinishDownload(string fileId, string? error)
    {
        DownloadJob? job;
        lock (_downloadLock)
        {
            if (!_downloads.Remove(fileId, out job)) return;
            job.Stream?.Dispose();
        }

        if (error is null)
        {
            try
            {
                File.Move(job.TempPath, job.Destination, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"저장할 수 없습니다: {ex.Message}";
            }
        }

        if (error is null)
        {
            DownloadCompleted?.Invoke(fileId, job.Destination);
        }
        else
        {
            try { File.Delete(job.TempPath); } catch (IOException) { }
            DownloadFailed?.Invoke(fileId, error);
        }
    }

    // ───────────────────────── 취소 ─────────────────────────

    /// <summary>
    /// 보내기 취소 (UI 스레드에서 호출). 작업 스레드는 다음 조각을 보내기 전에 멈추고, 서버에도 취소를 알려 임시 파일을 지우게 한다.
    /// 취소할 것이 없으면(이미 다 보냈거나 실패) false. 이벤트는 발생하지 않는다(부른 쪽이 화면을 직접 바꾼다).
    /// </summary>
    public bool CancelUpload(string fileId)
    {
        if (!_uploads.TryRemove(fileId, out UploadJob? job)) return false;
        job.FailReason = "취소";             // 보내는 중이면 다음 조각 전에 멈춘다
        job.Accepted.TrySetResult("취소");   // 서버 허락을 기다리는 중이면 바로 깨운다
        _send(Packet.Create(PacketType.FileUploadCancel, target: fileId));
        return true;
    }

    /// <summary>받기 취소. 받던 임시 파일(.part)을 지우고, 서버에 그만 보내라고 알린다. 이후 늦게 오는 조각은 버려진다.</summary>
    public bool CancelDownload(string fileId)
    {
        DownloadJob? job;
        lock (_downloadLock)
        {
            if (!_downloads.Remove(fileId, out job)) return false;
            job.Stream?.Dispose();
        }
        try { File.Delete(job.TempPath); } catch (IOException) { }
        _send(Packet.Create(PacketType.FileDownloadCancel, target: fileId));
        return true;
    }

    /// <summary>연결이 끊겼을 때 진행 중인 전송을 모두 실패 처리한다.</summary>
    public void CancelAll(string reason)
    {
        foreach (UploadJob job in _uploads.Values.ToList())
        {
            job.FailReason = reason;
            job.Accepted.TrySetResult(reason);
            Fail(job, reason);
        }
        List<string> ids;
        lock (_downloadLock) ids = _downloads.Keys.ToList();
        foreach (string id in ids) FinishDownload(id, reason);
    }
}
