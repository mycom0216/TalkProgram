using System.Collections.Concurrent;
using System.Data.Common;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

// ═══════════════════════════════════════════════════════════════════════════
// FileService.cs — 서버의 파일·사진·동영상 "올리기(업로드) / 내려받기(다운로드)" 담당
//
// [큰 그림]
//   파일은 최대 3GB까지 될 수 있다. 한 번에 보내면 (1) 패킷 최대 크기(1MB)를 넘고 (2) 보내는 동안 채팅이 전부 멈춘다.
//   그래서 파일을 256KB "조각(chunk)"으로 잘라 기존 채팅 연결(TCP 하나)로 보낸다.
//   조각과 조각 사이에 채팅 패킷이 끼어들 수 있으므로 큰 파일을 보내는 중에도 채팅이 된다.
//
// [올리기 흐름]  클라이언트 → 서버
//   1) FileUploadReq (Data = FileOffer: 파일ID·이름·크기·어느 방/누구에게·미리보기)
//   2) 서버가 검사 후 FileUploadRes(Success = true) ← "보내도 좋다"
//   3) FileChunk × N (Bin = 256KB 바이트) → 서버는 uploads/tmp/파일ID.part 임시 파일에 이어 쓴다
//   4) FileUploadEnd → 서버가 크기 확인 후 임시 파일을 uploads/년월/파일ID 로 옮기고 DB에 DONE 표시
//   5) FileMsg (Data = SharedFile) 를 방 사람들(또는 1:1 상대와 나)에게 보낸다 → 대화창에 파일 말풍선 표시
//   ※ 어느 단계에서든 실패하면 FileUploadRes(Success = false, Body = 이유)
//
// [내려받기 흐름]  서버 → 클라이언트
//   FileDownloadReq(Target = 파일ID) → 권한 확인 → 전용 "Download" 스레드가
//   FileDownloadRes(시작, Data = SharedFile) → FileChunk × N → FileDownloadEnd 를 순서대로 보낸다.
//
// [스레드]
//   - HandleUploadRequest / HandleChunk / HandleUploadEnd / HandleDownloadRequest:
//       그 파일을 보낸(요청한) 사람의 "수신 스레드(Session-N)"에서 실행된다.
//   - RunDownload: 파일마다 새로 만드는 "Download-N" 스레드에서 실행된다.
//   - OnDisconnected: 연결이 끊긴 것을 먼저 알아챈 스레드(수신 또는 송신 스레드)에서 실행된다.
//   → 같은 올리기(Upload)를 수신 스레드(조각 쓰기)와 다른 스레드(접속 종료 정리)가 동시에 건드릴 수 있어서
//     Upload마다 lock을 하나씩 둔다.
//
// [연결 관계]
//   PacketHandler ──(File* 패킷)──▶ FileService ──▶ FileRepository(DB files 테이블)
//                                                └─▶ RoomManager(방 멤버 확인·방 전체 전송), SessionManager(1:1 상대 찾기)
//                                                └─▶ ClientSession.Send / SendBulk(전송 큐)
//   MessageStore(대화 기록)는 ReadThumbnail로 저장해 둔 미리보기를 읽어 간다.
// ═══════════════════════════════════════════════════════════════════════════
namespace ChatApp.Server.Services;

/// <summary>
/// 파일·이미지·동영상 올리기/내려받기.
///
/// 올리기: FileUploadReq → (허락) FileUploadRes → FileChunk × N → FileUploadEnd → 방/상대에게 FileMsg
///   - 조각은 보낸 사람의 수신 스레드에서 임시 파일(uploads/tmp/ID.part)에 이어 쓴다.
///   - 256KB씩 나눠 오므로 그사이 다른 패킷(채팅)도 처리된다.
/// 내려받기: FileDownloadReq → 전용 스레드가 FileDownloadRes → FileChunk × N → FileDownloadEnd
///   - 요청한 사람의 수신 스레드를 막지 않도록 파일마다 스레드를 따로 만든다(한 사람당 최대 3개).
///   - ClientSession.SendBulk는 큐가 차면 기다리므로, 느린 사람에게 보내도 서버 메모리가 늘지 않는다.
///
/// 동시성: 진행 중인 올리기는 ConcurrentDictionary에 두고, 각 올리기는 자기 lock으로 보호한다.
///   (조각 쓰기는 수신 스레드, 접속 종료 정리는 송신 스레드에서 동시에 일어날 수 있기 때문)
/// </summary>
public sealed class FileService
{
    // [개념] 중첩 클래스(nested class): FileService 안에서만 쓰는 "진행 중인 올리기 1건"의 상태 묶음.
    //   private이라 바깥에서는 보이지 않는다.
    // [개념] required: 객체를 만들 때 반드시 값을 넣어야 하는 속성(빠뜨리면 컴파일 오류).
    // [개념] init: 만들 때만 값을 넣을 수 있고 그 뒤로는 바꿀 수 없는 속성(읽기 전용처럼 동작).
    private sealed class Upload
    {
        public required ClientSession Owner { get; init; }
        public required FileRecord Record { get; init; }
        public required string TempPath { get; init; }
        public required FileStream Stream { get; init; }
        public byte[]? Thumbnail { get; init; }
        // [개념] lock에 쓸 "열쇠" 객체. lock(upload.Lock) { ... } 안에는 한 번에 한 스레드만 들어갈 수 있다.
        // [왜] 올리기마다 따로 lock을 두면, 서로 다른 파일 올리기끼리는 기다리지 않고 동시에 진행된다(전체를 하나의 lock으로 묶는 것보다 빠름).
        public readonly object Lock = new();
        // 지금까지 받은 바이트 수 (3GB는 int 최대값 약 21억을 넘으므로 long)
        public long Received;
        // true = 끝났거나 취소됨. 이후 도착하는 조각은 무시한다. (Lock 안에서만 읽고 쓴다)
        public bool Closed;
    }

    // [왜] files 테이블은 나중에 추가한 기능이라, 예전 DB에서는 없을 수 있다. 그때 사용자에게 보여 줄 안내 문구.
    private const string TableMissingMessage =
        "서버 DB에 파일 테이블(files)이 없어 파일을 보낼 수 없습니다. 서버 관리자가 db/migrate_002_files.sql을 실행해야 합니다.";

    // [개념] ConcurrentDictionary: 여러 스레드가 동시에 넣고/빼고/찾아도 안전한 Dictionary.
    //   일반 Dictionary를 여러 스레드에서 동시에 고치면 내부 구조가 깨져 예외나 무한 루프가 생길 수 있다.
    // [스레드] 여러 사람의 수신 스레드가 각자 자기 파일을 넣고 빼므로 동시 접근이 일어난다. 키 = 파일 ID.
    private readonly ConcurrentDictionary<string, Upload> _uploads = new();

    // 진행 중인 내려받기의 "멈춤 신호". 키 = "세션번호:파일ID".
    // [개념] CancellationTokenSource: 다른 스레드에게 "그만해"를 알리는 표준 방법. Cancel()을 부르면
    //   그 토큰을 가진 쪽에서 IsCancellationRequested가 true가 되고, 작업 스레드가 스스로 확인하고 멈춘다.
    //   (스레드를 밖에서 강제로 죽이는 방법은 .NET에 없고, 있더라도 파일·lock이 정리되지 않아 위험하다)
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _downloads = new();
    private readonly SessionManager _sessions;
    private readonly RoomManager _rooms;
    private readonly FileRepository _repository;
    private readonly ServerLogger _log;
    private readonly AuditLog _audit;
    private readonly long _maxFileSize;
    private readonly string _root;
    private readonly string _tempDir;

    public FileService(SessionManager sessions, RoomManager rooms, FileRepository repository, ServerConfig config, ServerLogger log, AuditLog audit)
    {
        _audit = audit;
        _sessions = sessions;
        _rooms = rooms;
        _repository = repository;
        _log = log;
        _maxFileSize = config.MaxFileSizeBytes;
        _root = config.UploadRoot;
        _tempDir = Path.Combine(_root, "tmp");
        // [개념] CreateDirectory: 폴더가 없으면 만들고, 이미 있으면 아무 일도 하지 않는다(예외 없음).
        Directory.CreateDirectory(_tempDir);

        // 서버가 비정상 종료돼 남은 임시 파일 정리
        // [왜] 서버가 올리기 도중 꺼지면 반쯤 받은 .part 파일이 남는다. 시작할 때 지워서 디스크 낭비를 막는다.
        //   (DB에 남은 UPLOADING 행은 DONE이 아니므로 내려받기 대상이 되지 않는다)
        foreach (string part in Directory.EnumerateFiles(_tempDir, "*.part"))
        {
            // 다른 프로그램이 파일을 잡고 있으면 실패할 수 있다 → 무시하고 다음 파일로 (서버 시작을 막을 정도의 일은 아님)
            try { File.Delete(part); } catch (IOException) { }
        }
    }

    /// <summary>파일이 저장되는 최상위 폴더(절대 경로).</summary>
    public string Root => _root;

    /// <summary>대화 기록에 붙일 미리보기 (업로드할 때 함께 저장해 둔 "파일ID.thumb"). 없으면 null.</summary>
    public byte[]? ReadThumbnail(string storedPath)
    {
        try
        {
            // [왜] 올리기가 끝날 때 미리보기(JPEG)를 "파일ID.thumb"로 함께 저장해 둔다.
            //   나중에 방에 들어온 사람에게 지난 대화(사진 말풍선)를 보여 줄 때 이 파일을 읽어 붙인다.
            string thumb = FullPath(storedPath) + ".thumb";
            // [개념] 예외 필터 catch (...) when (조건): 조건에 맞는 예외만 잡는다. 그 밖의 예상 못 한 예외는 위로 올라가 버그를 숨기지 않는다.
            return File.Exists(thumb) ? File.ReadAllBytes(thumb) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ───────────────────────── 올리기 ─────────────────────────

    /// <summary>
    /// ① 올리기 요청(FileUploadReq) 검사 → DB에 UPLOADING 행 추가 → 임시 파일 열기 → 허락(FileUploadRes) 보내기.
    /// </summary>
    // [스레드] 보낸 사람의 수신 스레드에서 실행된다.
    public void HandleUploadRequest(ClientSession session, Packet packet)
    {
        FileOffer? offer = packet.GetData<FileOffer>();
        // [개념] ?. (null 조건 연산자): offer가 null이면 .FileId를 읽지 않고 null을 돌려준다(NullReferenceException 방지).
        // [개념] ?? (null 병합 연산자): 왼쪽이 null이면 오른쪽 값("")을 쓴다.
        string fileId = offer?.FileId ?? "";
        // [개념] 로컬 함수(local function): 메서드 안에서만 쓰는 작은 함수. 아래에서 실패 응답을 여러 번 보내므로 중복을 줄인다.
        //   바깥 변수(session, fileId)를 그대로 쓸 수 있다(캡처).
        void Fail(string message) => session.Send(UploadFail(fileId, message));

        // [주의] 파일 ID는 나중에 서버 디스크의 "파일 이름"으로 쓰인다.
        //   "../../etc/passwd" 같은 값이 들어오면 업로드 폴더 밖에 파일을 쓸 수 있으므로(경로 조작 공격),
        //   16진수 32자(GUID)인지 엄격하게 확인한다. 클라이언트가 보낸 값은 절대 그대로 믿지 않는다.
        if (offer is null || !FileRules.IsValidFileId(fileId)) { Fail("잘못된 파일 요청입니다."); return; }
        if (offer.Size <= 0) { Fail("빈 파일은 보낼 수 없습니다."); return; }
        // [왜] 크기 제한은 클라이언트도 미리 확인하지만, 조작된 클라이언트가 있을 수 있으므로 서버가 최종 확인한다.
        if (offer.Size > _maxFileSize) { Fail($"파일이 너무 큽니다. 최대 {FileRules.FormatSize(_maxFileSize)}까지 보낼 수 있습니다."); return; }
        // [개념] 속성 패턴 is { Length: > N }: "null이 아니고, Length가 N보다 크면". null 확인과 크기 비교를 한 번에 한다.
        // [왜] 미리보기는 FileMsg에 담겨 방 사람 전원에게 가므로 크면 안 된다(1000명 방이면 1000배).
        if (offer.Thumbnail is { Length: > FileRules.MaxThumbnailBytes }) { Fail("미리보기 이미지가 너무 큽니다."); return; }
        // [왜] 한 사람이 동시에 너무 많은 파일을 올리면 서버 디스크·대역폭을 독차지한다. 1인당 동시 3개로 제한.
        // [개념] u => u.Owner == session : 람다식. "각 u에 대해 주인이 이 세션인가?"를 검사하는 짧은 익명 함수.
        if (_uploads.Values.Count(u => u.Owner == session) >= FileRules.MaxActiveTransfers)
        {
            Fail($"파일은 동시에 {FileRules.MaxActiveTransfers}개까지 보낼 수 있습니다. 잠시 후 다시 보내세요.");
            return;
        }

        // 서버 디스크 여유 공간 확인 (3GB 파일도 받으므로 1GB는 남겨 둔다)
        try
        {
            // [개념] DriveInfo: 디스크(드라이브) 정보. Path.GetPathRoot("C:\\chat\\uploads") = "C:\\" → C 드라이브의 남은 공간.
            // [개념] ! (null 무시 연산자): "여기선 null이 아님을 내가 보장한다"고 컴파일러 경고를 끄는 표시. 실행에는 영향 없음.
            // [왜] 받는 도중 디스크가 가득 차면 서버 전체(로그, DB 등)가 문제를 일으킨다. 미리 확인해서 1GB 여유를 남긴다.
            long free = new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace;
            if (free - offer.Size < 1024L * 1024 * 1024)
            {
                _log.Error($"디스크 공간 부족: 남은 공간 {FileRules.FormatSize(free)}, 요청 {FileRules.FormatSize(offer.Size)}");
                Fail("서버 저장 공간이 부족해 파일을 받을 수 없습니다.");
                return;
            }
        }
        // [왜] 네트워크 드라이브 등에서는 여유 공간 확인이 실패할 수 있다. 확인 실패 때문에 파일 기능 전체를 막지는 않는다.
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { /* 확인 실패 → 일단 진행 */ }

        // [왜] 여기까지 왔다면 로그인한 세션이다(PacketHandler가 로그인 전에는 File* 패킷을 막는다). 그래서 ! 로 null 아님을 표시.
        UserInfo me = session.User!;
        int? roomId = null;
        string? receiverNo = null;
        // [개념] is int rid: RoomId(int?)에 값이 있으면 그 값을 rid 변수로 꺼낸다. 없으면(null) else로.
        // [주의] 권한 확인: 내가 참여하지 않은 방에 파일을 올리면 안 된다(방 번호만 알면 아무 방에나 보낼 수 있는 구멍 방지).
        if (offer.RoomId is int rid)
        {
            if (_rooms.GetJoinedRoom(session, rid) is null) { Fail("참여하지 않은 방에는 파일을 보낼 수 없습니다."); return; }
            roomId = rid;
        }
        else
        {
            // 1:1 전송: 받는 사람이 지금 접속 중이어야 한다(귓속말과 같은 규칙).
            ClientSession? target = string.IsNullOrWhiteSpace(offer.ToEmployeeNo) ? null : _sessions.FindByEmployeeNo(offer.ToEmployeeNo.Trim());
            if (target?.User is null) { Fail("상대가 접속 중이 아니라 파일을 보낼 수 없습니다."); return; }
            if (target == session) { Fail("자기 자신에게는 파일을 보낼 수 없습니다."); return; }
            receiverNo = target.User.EmployeeNo;
        }

        // [주의] 파일 이름도 클라이언트 입력이다. "..\\..\\a.exe" 같은 경로나 쓸 수 없는 문자를 제거한다.
        //   (실제 디스크에는 원래 이름이 아니라 파일 ID로 저장하지만, 이름은 다른 사람의 PC에 저장될 때 쓰이므로 정리해 둔다)
        string name = FileRules.SanitizeFileName(offer.FileName);
        // [개념] record: 값만 담는 간단한 불변(immutable) 클래스. 생성자 인자 순서대로 속성이 정해진다.
        // [왜] 저장 경로 "년월/파일ID" (예: 202610/abc...): 한 폴더에 파일이 수만 개 쌓이면 탐색기·파일 시스템이 느려지므로 달별로 나눈다.
        var record = new FileRecord(fileId, roomId, me.EmployeeNo, receiverNo, name, offer.Size,
                                    FileRules.GetKind(name), $"{DateTime.Now:yyyyMM}/{fileId}", Done: false);
        try
        {
            // [왜] 다 받은 뒤가 아니라 "시작할 때" DB에 넣는다.
            //   DB에 문제가 있으면(테이블 없음 등) 3GB를 다 받은 뒤가 아니라 지금 바로 알 수 있다.
            _repository.InsertUploading(record);
        }
        catch (DbException ex)
        {
            // [개념] SqlState: DB 오류의 표준 코드. 42S02 = 테이블이 없음, 23000 = 제약 조건 위반(중복 키 등).
            //   오류 문구(언어마다 다름) 대신 코드로 구분하는 것이 안정적이다.
            if (ex.SqlState == FileRepository.TableMissingState)
            {
                _log.Error("files 테이블이 없습니다. db/migrate_002_files.sql을 root로 실행하세요.");
                Fail(TableMissingMessage);
            }
            else if (ex.SqlState == "23000") Fail("파일 ID가 겹칩니다. 다시 보내 주세요.");
            else
            {
                _log.Error($"[{session.Id}] DB 오류 (FileUploadReq): {ex.Message}");
                Fail("서버 DB 오류로 파일을 받을 수 없습니다.");
            }
            return;
        }

        // [개념] Path.Combine: 운영체제에 맞는 구분자(\\ 또는 /)로 경로를 이어 붙인다. 문자열 + 로 붙이는 것보다 안전하다.
        // [왜] 바로 최종 위치에 쓰지 않고 임시 파일(.part)에 쓴다. 다 받기 전에는 "완성된 파일"처럼 보이면 안 되기 때문.
        //   다 받으면 File.Move로 한 번에 옮긴다(이동은 거의 즉시 끝나므로 반쯤 쓴 파일이 노출되지 않는다).
        string tempPath = Path.Combine(_tempDir, fileId + ".part");
        Upload upload;
        try
        {
            upload = new Upload
            {
                Owner = session,
                Record = record,
                TempPath = tempPath,
                // [개념] FileMode.CreateNew: 같은 이름 파일이 이미 있으면 예외 → 실수로 다른 파일을 덮어쓰지 않는다.
                // [개념] FileShare.None: 쓰는 동안 다른 프로그램이 이 파일을 열 수 없게 잠근다.
                // [개념] 1 << 16 = 65536(64KB) 버퍼. 비트 시프트(1을 왼쪽으로 16칸) = 2의 16제곱. 디스크에 작게 자주 쓰지 않고 모아서 쓴다.
                // [왜] 파일을 조각마다 열고 닫지 않고, 올리기가 끝날 때까지 열어 둔다(조각 수천 개마다 열고 닫으면 느리다).
                Stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16),
                Thumbnail = offer.Thumbnail,
            };
        }
        catch (IOException ex)
        {
            _log.Error($"[{session.Id}] 임시 파일을 만들 수 없음: {ex.Message}");
            // 임시 파일을 못 만들었으면 방금 넣은 DB 행도 지운다(DB와 디스크 상태를 맞춤)
            SafeDeleteRecord(fileId);
            Fail("서버에 파일을 저장할 수 없습니다.");
            return;
        }

        // [개념] 인덱서 대입 _uploads[key] = value: 없으면 추가, 있으면 덮어쓴다(ConcurrentDictionary에서도 원자적).
        _uploads[fileId] = upload;
        // ② "보내도 좋다" 응답. 클라이언트의 올리기 스레드는 이 패킷을 기다렸다가 조각을 보내기 시작한다.
        // [개념] target: fileId 처럼 "이름: 값"으로 인자를 넘기는 것을 명명된 인자(named argument)라고 한다. 순서를 건너뛰고 원하는 인자만 줄 수 있다.
        session.Send(Packet.Create(PacketType.FileUploadRes, target: fileId, success: true));
        _log.Info($"파일 받기 시작: {name} ({FileRules.FormatSize(offer.Size)}) from {me} → {(roomId is null ? $"1:1 {receiverNo}" : $"방 #{roomId}")}");

        // 받는 도중에 접속이 끊겼다면(정리가 먼저 끝났다면) 여기서 정리한다
        // [주의] 경쟁 상태 방지: 위 과정 도중 다른 스레드에서 접속 종료 정리(OnDisconnected)가 "먼저" 끝났을 수 있다.
        //   그 정리는 _uploads에 아직 없던 이 올리기를 못 봤으므로, 여기서 한 번 더 확인해 직접 정리한다(임시 파일·DB 행 누수 방지).
        if (session.IsClosed) Abort(upload, null, "접속 종료");
    }

    /// <summary>③ 파일 조각(FileChunk)을 임시 파일 끝에 이어 쓴다.</summary>
    // [스레드] 보낸 사람의 수신 스레드. 같은 사람의 조각은 TCP 순서대로 한 스레드에서 오므로 순서가 보장된다.
    public void HandleChunk(ClientSession session, Packet packet)
    {
        // 이미 실패 처리된 파일의 남은 조각은 조용히 버린다
        // [주의] upload.Owner != session 확인: 남의 파일 ID를 알아내 조각을 끼워 넣는 것을 막는다.
        // [개념] TryGetValue(key, out value): 찾으면 true와 함께 value에 담아 준다. 없을 때 예외가 나지 않는다.
        if (packet.Target is null || !_uploads.TryGetValue(packet.Target, out Upload? upload) || upload.Owner != session) return;
        // [개념] [] (컬렉션 식): 빈 배열. Bin이 null이면 빈 배열로 대신해 아래에서 null 확인을 안 해도 되게 한다.
        byte[] data = packet.Bin ?? [];

        lock (upload.Lock)
        {
            if (upload.Closed) return;
            // [스레드] lock 안: 이 사이에 다른 스레드가 Abort(Stream 닫기)를 할 수 없다. 닫힌 Stream에 쓰는 오류를 막는다.
            // [주의] 처음에 말한 크기보다 많이 보내면 거절. 크기 제한(3GB)을 우회해 디스크를 채우는 공격을 막는다.
            if (upload.Received + data.Length > upload.Record.Size)
            {
                Abort(upload, "보낸 데이터가 파일 크기보다 큽니다.");
                return;
            }
            try
            {
                // [개념] Write는 버퍼에 쓰고, 버퍼가 차면 디스크에 기록한다.
                upload.Stream.Write(data);
                upload.Received += data.Length;
            }
            catch (IOException ex)
            {
                _log.Error($"[{session.Id}] 파일 쓰기 실패: {ex.Message}");
                Abort(upload, "서버 디스크에 파일을 쓰지 못했습니다.");
            }
        }
    }

    /// <summary>④ 다 보냈다는 신호(FileUploadEnd). 크기 확인 → 최종 위치로 이동 → DB 완료 표시 → ⑤ FileMsg 전송.</summary>
    public void HandleUploadEnd(ClientSession session, Packet packet)
    {
        if (packet.Target is null || !_uploads.TryGetValue(packet.Target, out Upload? upload) || upload.Owner != session) return;

        FileRecord record = upload.Record;
        lock (upload.Lock)
        {
            if (upload.Closed) return;
            // [왜] 말한 크기와 실제 받은 크기가 다르면 파일이 깨진 것이다. 깨진 파일을 다른 사람에게 보여 주지 않는다.
            if (upload.Received != record.Size)
            {
                Abort(upload, "파일이 끝까지 오지 않았습니다.");
                return;
            }
            upload.Closed = true;
            upload.Stream.Dispose();
            // [개념] out _ (버리기, discard): 꺼낸 값이 필요 없을 때 _ 로 버린다.
            _uploads.TryRemove(record.FileId, out _);
        }

        // [주의] 큰 파일은 몇 분씩 걸린다. 그동안 보낸 사람이 방에서 나갔다면 그 방에 파일을 올리면 안 된다(권한을 끝에서 한 번 더 확인).
        // 그사이 방에서 나갔으면 취소
        Room? room = null;
        // [개념] (room = ...) is null: 대입하면서 동시에 결과를 검사하는 짧은 표현. 방이면 room 변수가 채워진다.
        if (record.RoomId is int roomId && (room = _rooms.GetJoinedRoom(session, roomId)) is null)
        {
            Cleanup(upload);
            session.Send(UploadFail(record.FileId, "방에서 나가 파일 전송이 취소되었습니다."));
            return;
        }

        try
        {
            string finalPath = FullPath(record.StoredPath);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            // [개념] File.Move: 같은 드라이브 안에서는 데이터를 복사하지 않고 "이름표"만 바꾸므로 3GB도 거의 즉시 끝난다.
            File.Move(upload.TempPath, finalPath);
            if (upload.Thumbnail is { Length: > 0 })
                File.WriteAllBytes(finalPath + ".thumb", upload.Thumbnail); // 나중에 대화 기록을 불러올 때 쓰려고 보관
            // DB 상태 UPLOADING → DONE. 이제부터 내려받기·대화 기록에 나타난다.
            _repository.MarkDone(record.FileId);
        }
        // [왜] 옮기기·DB 기록 중 하나라도 실패하면 "반쯤 성공" 상태가 남지 않도록 임시 파일·최종 파일·DB 행을 모두 지운다.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbException)
        {
            _log.Error($"[{session.Id}] 파일 저장 실패: {ex.Message}");
            Cleanup(upload);
            try { File.Delete(FullPath(record.StoredPath)); } catch (IOException) { }
            session.Send(UploadFail(record.FileId, "서버에 파일을 저장하지 못했습니다."));
            return;
        }

        UserInfo me = session.User!;
        // [왜] SharedFile은 클라이언트에 보낼 정보만 담는다. 서버 저장 경로(StoredPath) 같은 내부 정보는 보내지 않는다.
        var shared = new SharedFile
        {
            FileId = record.FileId,
            FileName = record.FileName,
            Size = record.Size,
            Kind = record.Kind,
            RoomId = record.RoomId,
            ToEmployeeNo = record.ReceiverNo,
            SenderNo = me.EmployeeNo,
            SenderName = me.DisplayName,
            Thumbnail = upload.Thumbnail,
        };
        // [주의] 사칭 방지: 보낸 사람 이름(sender)과 사원번호(SenderId)는 클라이언트가 보낸 값이 아니라
        //   서버가 로그인 정보(session.User)로 직접 채운다. 그래서 다른 사람인 척 파일을 보낼 수 없다.
        // Target: 방이면 방 번호, 1:1이면 받는 사람 사원번호 → 클라이언트가 어느 대화창에 넣을지 결정한다.
        var message = Packet.Create(PacketType.FileMsg, $"[파일] {record.FileName}", sender: me.DisplayName,
                                     target: room?.Id.ToString() ?? record.ReceiverNo).WithData(shared);
        message.SenderId = me.EmployeeNo;

        if (room is not null)
        {
            // 방 멤버 전원(보낸 사람 포함)에게. 보낸 사람은 이 FileMsg를 받고 "보내는 중" 말풍선을 "완료"로 바꾼다.
            _rooms.SendToRoom(room, message);
        }
        else
        {
            // 1:1: 상대(지금 접속 중이면)와 나에게만
            _sessions.FindByEmployeeNo(record.ReceiverNo!)?.Send(message);
            session.Send(message);
        }
        _log.Info($"파일 받기 완료: {record.FileName} ({FileRules.FormatSize(record.Size)}) from {me}");
        // [왜] 파일 송수신은 정보 유출 조사에서 가장 먼저 보는 기록이다: 누가 어떤 파일을 어디(방/상대)로 보냈는지.
        _audit.Record(AuditActions.FileUpload, me.EmployeeNo, record.ReceiverNo,
                      $"{record.FileName} ({FileRules.FormatSize(record.Size)}){(room is null ? "" : $" → 방 #{room.Id}")}", session.RemoteEndPoint);
    }

    // ───────────────────────── 내려받기 ─────────────────────────

    /// <summary>
    /// 내려받기 요청(FileDownloadReq, Target = 파일 ID). 권한 확인 후 파일마다 전용 스레드를 만들어 보낸다.
    /// </summary>
    // [스레드] 요청한 사람의 수신 스레드. 여기서 직접 3GB를 보내면 그동안 이 사람의 다른 요청(채팅 등)을 처리하지 못하므로
    //   실제 전송은 새 스레드(RunDownload)에 맡기고 바로 돌아간다.
    public void HandleDownloadRequest(ClientSession session, Packet packet)
    {
        string fileId = packet.Target ?? "";
        void Fail(string message) => session.Send(Packet.Create(PacketType.FileDownloadRes, message, target: fileId, success: false));

        if (!FileRules.IsValidFileId(fileId)) { Fail("잘못된 파일 요청입니다."); return; }

        FileRecord? record;
        try
        {
            record = _repository.Find(fileId);
        }
        catch (DbException ex)
        {
            _log.Error($"[{session.Id}] DB 오류 (FileDownloadReq): {ex.Message}");
            Fail(ex.SqlState == FileRepository.TableMissingState ? TableMissingMessage : "서버 DB 오류로 파일을 찾지 못했습니다.");
            return;
        }
        // [개념] is not { Done: true }: "null이거나 Done이 true가 아니면". 아직 올리는 중(UPLOADING)인 파일은 내려받을 수 없다.
        if (record is not { Done: true }) { Fail("파일을 찾을 수 없습니다."); return; }

        // [주의] 파일 ID만 알면 누구나 받을 수 있으면 안 된다(링크 유출 = 파일 유출). 서버가 매번 권한을 확인한다.
        // 권한: 방 파일은 지금 그 방에 있는 사람, 1:1 파일은 보낸 사람과 받은 사람만
        string me = session.User!.EmployeeNo;
        bool allowed = record.RoomId is int roomId
            ? _rooms.GetJoinedRoom(session, roomId) is not null
            : me == record.SenderNo || me == record.ReceiverNo;
        if (!allowed) { Fail("이 파일을 받을 권한이 없습니다. (방에서 나갔거나 사라진 방)"); return; }

        string path;
        // [개념] C#은 "확실히 값이 대입된 변수"만 읽게 한다(확정 대입). catch에서 return하므로 아래에서는 path가 항상 채워져 있다.
        try { path = FullPath(record.StoredPath); }
        catch (IOException) { Fail("파일을 찾을 수 없습니다."); return; }
        if (!File.Exists(path)) { Fail("서버에서 파일이 삭제되었습니다."); return; }

        // [개념] Interlocked.Increment: "읽고 +1 하고 저장"을 쪼갤 수 없는 한 동작(원자적 연산)으로 한다.
        //   그냥 session.ActiveDownloads++ 를 쓰면, 두 스레드가 동시에 같은 값을 읽고 둘 다 +1 해서 하나가 사라질 수 있다.
        //   lock 없이도 숫자 하나를 안전하게 바꿀 수 있는 가장 가벼운 방법이다.
        // [개념] ref: 값을 복사하지 않고 변수 자체(필드)를 넘겨, 메서드가 원래 변수를 바꿀 수 있게 한다.
        // [왜] 먼저 +1 하고 한도를 넘으면 다시 -1: "확인 후 증가"로 나누면 그 사이에 다른 스레드가 끼어들어 한도를 넘길 수 있다.
        if (Interlocked.Increment(ref session.ActiveDownloads) > FileRules.MaxActiveTransfers)
        {
            Interlocked.Decrement(ref session.ActiveDownloads);
            Fail($"파일은 동시에 {FileRules.MaxActiveTransfers}개까지 받을 수 있습니다. 잠시 후 다시 시도하세요.");
            return;
        }

        // [개념] new Thread(람다): 새 스레드를 만든다. () => RunDownload(...) 는 "이 스레드가 실행할 일".
        //   람다가 session, record, path를 캡처(붙잡아)서 새 스레드에서도 쓸 수 있다.
        // [왜] 스레드 풀(Task.Run) 대신 전용 스레드를 쓰는 이유: 다운로드는 몇 분씩 블로킹(SendBulk 대기)하는 긴 작업이라
        //   스레드 풀 스레드를 오래 붙잡으면 다른 짧은 작업이 밀린다. 또 이름을 붙여 디버거에서 구분하기 쉽다.
        // [개념] IsBackground = true: 메인 스레드가 끝나면(서버 종료) 이 스레드도 같이 끝난다. false면 다운로드가 끝날 때까지 프로세스가 안 꺼진다.
        // [개념] fileId[..6]: 범위 연산자. 앞 6글자만 잘라 스레드 이름에 넣는다.
        // 요청 ID(Body)를 모든 내려받기 패킷에 다시 실어 보낸다 (PacketType.cs의 참고 설명)
        string requestId = packet.Body ?? "";
        var cancel = new CancellationTokenSource();
        string key = $"{session.Id}:{fileId}";
        // 같은 파일을 받던 이전 스레드가 아직 남아 있으면(취소 직후 재요청) 확실히 멈추게 하고 새 것으로 바꾼다
        _downloads.AddOrUpdate(key, cancel, (_, old) => { old.Cancel(); return cancel; });

        _audit.Record(AuditActions.FileDownload, me, record.SenderNo,
                      $"{record.FileName} ({FileRules.FormatSize(record.Size)})", session.RemoteEndPoint);

        var thread = new Thread(() => RunDownload(session, record, path, requestId, key, cancel))
        {
            IsBackground = true,
            Name = $"Download-{session.Id}-{fileId[..6]}",
        };
        thread.Start();
    }

    /// <summary>내려받기 전용 스레드. 시작·조각·끝을 모두 SendBulk로 보내 순서를 지킨다.</summary>
    // [스레드] "Download-N" 전용 스레드에서 실행.
    // [개념] 배압(backpressure): SendBulk는 송신 큐(최대 8조각 ≈ 2MB)가 꽉 차면 자리가 날 때까지 기다린다.
    //   받는 사람의 네트워크가 느리면 파일 읽기도 그 속도에 맞춰 느려지므로, 3GB 파일이라도 서버 메모리에는 몇 MB만 올라간다.
    //   (기다리지 않고 계속 큐에 넣으면 3GB가 통째로 메모리에 쌓여 서버가 죽을 수 있다)
    private void RunDownload(ClientSession session, FileRecord record, string path, string requestId, string key, CancellationTokenSource cancel)
    {
        CancellationToken token = cancel.Token;
        try
        {
            // [개념] using var: 이 메서드가 끝날 때(예외가 나도) 자동으로 Dispose → 파일이 닫힌다. 파일 핸들 누수 방지.
            // [개념] FileShare.Read: 읽는 동안 다른 스레드도 같은 파일을 읽을 수 있다(여러 사람이 같은 파일을 동시에 받는 경우).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var info = new SharedFile
            {
                FileId = record.FileId, FileName = record.FileName, Size = stream.Length, Kind = record.Kind,
                RoomId = record.RoomId, ToEmployeeNo = record.ReceiverNo, SenderNo = record.SenderNo,
            };
            // [주의] 시작·조각·끝을 "모두 같은 큐(SendBulk)"로 보내야 순서가 지켜진다.
            //   시작 패킷만 일반 큐(Send)로 보내면, 일반 큐가 우선이라 순서가 바뀌거나 끝 패킷이 조각보다 먼저 도착할 수 있다.
            // SendBulk가 false = 상대 연결이 끊김 → 더 보낼 필요 없이 끝낸다.
            if (!session.SendBulk(Packet.Create(PacketType.FileDownloadRes, requestId, target: record.FileId, success: true).WithData(info))) return;

            while (true)
            {
                // 큐에 쌓인 조각이 같은 배열을 공유하면 안 되므로 조각마다 새 배열
                byte[] buffer = new byte[FileRules.ChunkSize];
                // [개념] ReadAtLeast: 버퍼가 찰 때까지(또는 파일 끝까지) 읽는다. 그냥 Read는 요청보다 적게 읽고 돌아올 수 있어서 조각 크기가 들쭉날쭉해진다.
                int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                if (read == 0) break;
                // 마지막 조각은 256KB보다 작을 수 있다 → 실제 읽은 만큼만 남긴다(뒤의 0 바이트를 보내면 파일이 깨짐)
                if (read < buffer.Length) Array.Resize(ref buffer, read);
                // 받는 사람이 취소했으면 여기서 멈춘다 (조각마다 확인 → 취소 후 최대 한 조각만 더 나간다)
                if (token.IsCancellationRequested)
                {
                    _log.Info($"파일 보내기 취소: {record.FileName} → {session}");
                    return;
                }
                if (!session.SendBulk(new Packet { Type = PacketType.FileChunk, Target = record.FileId, Body = requestId, Bin = buffer })) return;
            }

            session.SendBulk(Packet.Create(PacketType.FileDownloadEnd, requestId, target: record.FileId));
            _log.Info($"파일 보내기 완료: {record.FileName} → {session}");
        }
        catch (IOException ex)
        {
            _log.Error($"[{session.Id}] 파일 읽기 실패: {ex.Message}");
            var fail = Packet.Create(PacketType.FileDownloadRes, "서버에서 파일을 읽지 못했습니다.", target: record.FileId, success: false);
            session.SendBulk(fail);
        }
        finally
        {
            // [개념] finally: 성공·실패·중간 return 어느 경우든 반드시 실행된다. 동시 다운로드 개수를 꼭 되돌려 놓아야
            //   "한도 3개"가 영원히 찬 상태로 남는 버그가 생기지 않는다.
            Interlocked.Decrement(ref session.ActiveDownloads);
            // 내 것일 때만 목록에서 뺀다 (그사이 같은 파일의 새 요청이 내 자리를 차지했을 수 있다)
            _downloads.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cancel));
            cancel.Dispose();
        }
    }

    // ───────────────────────── 취소 ─────────────────────────

    /// <summary>보내는 사람이 올리기를 취소함 → 임시 파일과 DB 행을 지운다 (보낸 사람에게 따로 알리지 않음, 본인이 누른 것이므로).</summary>
    // [스레드] 그 사람의 수신 스레드. 같은 사람의 조각(HandleChunk)도 같은 스레드에서 처리되므로 순서가 꼬이지 않는다.
    public void HandleUploadCancel(ClientSession session, Packet packet)
    {
        if (packet.Target is not null && _uploads.TryGetValue(packet.Target, out Upload? upload) && upload.Owner == session)
            Abort(upload, null, "보낸 사람이 취소");
    }

    /// <summary>받는 사람이 내려받기를 취소함 → 그 파일을 보내던 스레드에 멈춤 신호.</summary>
    public void HandleDownloadCancel(ClientSession session, Packet packet)
    {
        if (packet.Target is not null && _downloads.TryGetValue($"{session.Id}:{packet.Target}", out CancellationTokenSource? cancel))
        {
            try { cancel.Cancel(); }
            catch (ObjectDisposedException) { } // 그사이 다 보내고 끝난 경우
        }
    }

    // ───────────────────────── 정리 ─────────────────────────

    /// <summary>접속이 끊기면 그 사람이 올리던 파일을 모두 취소한다(어느 스레드에서든 호출될 수 있음).</summary>
    // [스레드] 연결 종료를 처음 알아챈 스레드(수신 또는 송신)에서 PacketHandler.OnDisconnected를 거쳐 호출된다.
    public void OnDisconnected(ClientSession session)
    {
        // [왜] ToList()로 목록을 먼저 복사한다. 돌면서 Abort가 _uploads에서 항목을 빼므로, 원본을 직접 돌면 헷갈리는 결과가 생길 수 있다.
        foreach (Upload upload in _uploads.Values.Where(u => u.Owner == session).ToList())
            Abort(upload, null, "접속 종료");
    }

    /// <summary>올리기 취소. reason이 있으면 보낸 사람에게 알린다.</summary>
    // [주의] Abort는 여러 곳(HandleChunk, HandleUploadEnd, OnDisconnected, HandleUploadRequest)에서, 서로 다른 스레드가 동시에 부를 수 있다.
    //   lock + Closed 확인으로 "정리는 딱 한 번만" 일어나게 한다(Stream을 두 번 닫거나 DB 행을 두 번 지우는 일 방지).
    // [개념] lock은 같은 스레드가 다시 들어가는 것(재진입)을 허용한다. HandleChunk의 lock 안에서 Abort를 불러도 멈추지 않는다.
    private void Abort(Upload upload, string? reason, string? logReason = null)
    {
        lock (upload.Lock)
        {
            if (upload.Closed) return;
            upload.Closed = true;
            upload.Stream.Dispose();
            _uploads.TryRemove(upload.Record.FileId, out _);
        }
        Cleanup(upload);
        // [왜] 느린 작업(파일 삭제, DB, 전송)은 lock 밖에서 한다. lock을 오래 잡고 있으면 그동안 다른 스레드가 기다려야 한다.
        if (reason is not null) upload.Owner.Send(UploadFail(upload.Record.FileId, reason));
        _log.Info($"파일 받기 취소: {upload.Record.FileName} ({reason ?? logReason})");
    }

    /// <summary>임시 파일과 DB 행 삭제.</summary>
    private void Cleanup(Upload upload)
    {
        try { File.Delete(upload.TempPath); } catch (IOException) { }
        SafeDeleteRecord(upload.Record.FileId);
    }

    /// <summary>DB 행 삭제. 실패해도 예외를 밖으로 던지지 않고 로그만 남긴다(정리 작업이 서버를 멈추게 하면 안 됨).</summary>
    private void SafeDeleteRecord(string fileId)
    {
        try { _repository.Delete(fileId); }
        catch (DbException ex) { _log.Error($"files 행 삭제 실패({fileId}): {ex.Message}"); }
    }

    /// <summary>DB에 저장된 상대 경로(예: 202610/파일ID) → 실제 절대 경로.</summary>
    // [개념] 경로 조작(Path Traversal) 방어: storedPath에 "..\\..\\Windows\\system.ini" 같은 값이 들어 있어도
    //   GetFullPath로 ".."를 풀어 실제 경로를 계산한 뒤, 그 경로가 업로드 폴더(_root)로 시작하는지 확인한다.
    //   시작하지 않으면 업로드 폴더 밖이므로 거부한다. (DB가 조작되거나 버그가 있어도 서버의 다른 파일은 읽히지 않는다)
    private string FullPath(string storedPath)
    {
        string full = Path.GetFullPath(Path.Combine(_root, storedPath));
        // DB 값이 조작돼도 업로드 폴더 밖은 읽지 않는다
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("업로드 폴더 밖의 경로입니다.");
        return full;
    }

    /// <summary>올리기 실패 응답 패킷 (Target = 파일 ID로 어느 파일의 실패인지 알려 준다).</summary>
    private static Packet UploadFail(string fileId, string message)
        => Packet.Create(PacketType.FileUploadRes, message, target: fileId, success: false);
}
