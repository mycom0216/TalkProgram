using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Net.Sockets;
using System.Security.Authentication;
using ChatApp.Client.Chat;
using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Security;
using ChatApp.Common.Transfer;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] 파일·이미지·동영상 전송: 보내기, 받기, 취소, 이미지 뷰어, 받은 파일 보관 위치, 방 파일 모아보기.
// MainForm은 C#의 partial class(클래스를 여러 파일로 나누는 기능)로 쪼개 두었다.
// 이 파일도 MainForm.cs와 "같은 클래스"다. 필드(_client, _me 등)와 메서드를 파일 구분 없이 그대로 쓴다.
// 나눈 기준은 "기능별"이고, 동작은 나누기 전과 완전히 같다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   파일 전송의 "화면 쪽 접착제". 실제 전송(조각 나누기·재시도·동시 3개 제한)은 FileTransferService가 하고, 여기서는 그 이벤트를 받아 말풍선(ChatFile)의 상태를 바꾸고, 클릭·메뉴 동작과 이미지 뷰어를 연결한다.
//
// ■ 역할 분담
//   FileTransferService(Common) ... 전송 엔진: 조각 읽고 쓰기, 진행률, 취소, 대기열. 화면을 모른다.
//   MainForm.Files.cs ............. 엔진 이벤트 → UI 스레드 → ChatFile.State/Percent 갱신 → conv.NotifyChanged → 말풍선 다시 그리기
//   _fileItems 사전 ............... "파일 번호 → (말풍선, 대화)": 진행률 이벤트가 어느 말풍선을 바꿀지 찾는 색인
//
// ■ 보내기 흐름
//   SendFiles(크기 미리 검사) → Task.Run으로 미리보기(썸네일) 만들기(큰 사진도 UI가 안 멈춤) → UI 스레드에서 StartUpload
//   → "보내는 중" 말풍선 즉시 표시(Uploading, LocalPath=원본) → 엔진 진행률 → Processing(서버 저장 중) → 서버 FileMsg 방송 → Ready
//
// ■ 받기 흐름
//   말풍선 클릭(Open) → 이미 내 PC에 있으면 바로 열기 → 예전 캐시(%LocalAppData%\ChatApp\files\파일ID\이름)가 크기까지 같으면 재사용 → 아니면 StartDownload
//   → Queued(동시 3개 제한이면 대기) → Downloading(%) → OnDownloadCompleted → 열기/저장 안내
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 작업 스레드(Task.Run) + UI 복귀(ContinueWith/RunOnUi) .. "Task.Run ContinueWith UI thread WinForms"
//   - 늦게 도착하는 이벤트 무시(상태 확인) ................... "stale event ignore state guard cancellation race"
//   - 경로 조작(path traversal) 방지: 파일 이름·번호 검증 ...... "path traversal sanitize file name"
//   - 실행 파일 경고(확장자 검사) ........................... "warn executable download untrusted"
//   - 캐시 재사용(크기 비교) ................................ "file cache validate size"
//   - 이미지 뷰어와 앞/뒤 이동 ............................... "image viewer navigation list"
//
// ■ 면접 예상 질문과 답
//   Q1. 큰 파일을 보내도 화면이 안 멈추는 이유는요?
//       A. 세 군데에서 UI 스레드를 비워 둡니다. (1) 미리보기는 Task.Run으로 작업 스레드에서 만든다 (2) 파일 읽기·조각 전송은 엔진의 작업 스레드가 하고 (3) 조각은 ChatClient의 대량 큐(최대 16개)로 보내 채팅이 먼저 나가게 한다. 진행률 이벤트만 BeginInvoke로 UI에 넘깁니다.
//   Q2. 진행률 이벤트가 취소 뒤에 늦게 도착하면 어떻게 되나요?
//       A. 이벤트는 작업 스레드에서 BeginInvoke로 넘어오므로 UI에서 처리될 때는 이미 상태가 바뀌었을 수 있습니다. UpdateFile은 실패·취소 상태면 무시하고, DownloadProgress는 엔진에 "아직 받는 중인가"(IsDownloading)를 다시 물어서 취소된 말풍선을 "받는 중"으로 되돌리지 않습니다.
//   Q3. 서버가 보낸 파일 이름·번호를 그대로 경로에 쓰면 어떤 위험이 있나요?
//       A. "../../" 같은 값으로 의도하지 않은 폴더에 파일을 쓰는 경로 조작이 가능해집니다. FileRules.IsValidFileId로 번호 모양을 검사하고(통과 못 하면 무시), SanitizeFileName으로 이름에서 경로 문자를 제거해 CachePath를 만듭니다. 서버를 믿더라도 입력으로 취급하는 방어적 코딩입니다.
//   Q4. 받은 파일이 이미 있는지 어떻게 판단하나요?
//       A. 캐시 경로에 파일이 있고 크기가 서버가 알려 준 크기와 같으면 재사용합니다. 한계: 크기만 같고 내용이 다른 경우(손상·변조)는 못 잡습니다. 정확히 하려면 서버가 해시(SHA-256)를 함께 주고 비교하면 됩니다.
//   Q5. 실행 파일(.exe 등)을 받아서 열려고 하면요?
//       A. OpenLocalFile이 확장자를 검사해 "보낸 사람을 믿을 수 있을 때만 여세요. 열까요?"를 기본 버튼 "아니요"로 한 번 더 묻습니다. 사내 메신저라도 계정이 탈취될 수 있으므로 사용자에게 마지막 판단 기회를 줍니다. 프로그램이 자동으로 실행하지는 않습니다.
//   Q6. 동시에 4개 이상 받으려 하면요?
//       A. 엔진이 동시 3개까지만 받고 나머지는 대기열에 둡니다. 말풍선은 Queued("받기 대기")로 보이고, 차례가 오면 DownloadStarted가 "받는 중 0%"로 바꿉니다. 서버도 같은 제한을 두며(과부하 방지), 서버가 "동시에 N개"라고 거절하면 엔진이 잠시 뒤 다시 요청합니다.
//   Q7. 한계는요? (솔직하게 답하기)
//       A. (1) 파일 이름 중복·재전송 판단이 크기에 의존한다(해시 없음). (2) 받은 파일 캐시가 계속 쌓인다(정리 정책 없음) — 서버는 보관 기간 후 파일을 지우지만 클라이언트 캐시는 별개다. (3) 이미지 뷰어가 대화의 사진 목록을 매번 새로 만들어 대화가 아주 길면 열 때 약간 느릴 수 있다. (4) ChatFile.Thumbnail의 Image를 Dispose하지 않는다.
//
// ■ 문제가 났을 때 어디를 볼까
//   - 진행률 막대가 멈춘다 ................... 엔진 이벤트 → UpdateFile → _fileItems에 등록됐는지(StartUpload/HandleHistory/OnFileCommand의 TryAdd)
//   - "받기 대기"에서 안 넘어간다 ............ 다른 3개의 전송 종료 여부, DownloadStarted, 서버 FileDownloadRes의 "동시에" 응답
//   - 받은 파일이 열리지 않는다 .............. LocalPath/CachePath 존재, 실행 파일 확인 창에서 "아니요", Win32Exception(연결 프로그램 없음)
//   - 취소했는데 다시 "받는 중"으로 보인다 .... DownloadProgress의 IsDownloading 확인, UpdateFile의 상태 검사
//   - 이미지 뷰어 이동이 안 된다 ............. _imageViewerItems(대화의 사진 목록), LoadImageViewerItem의 다운로드 시작 조건
// ───────────────────────────────────────────────────────────────────────────

/// <summary>메인 창(partial) - 파일·사진·동영상 보내기/받기/취소, 이미지 뷰어, 방 파일 모아보기 부분.</summary>
public sealed partial class MainForm
{
    // ───────────────────────── 파일·이미지·동영상 ─────────────────────────

    /// <summary>전송 엔진의 이벤트(작업/수신 스레드) → UI 스레드에서 말풍선 갱신.</summary>
    private void WireTransfers()
    {
        _transfers.UploadProgress += (id, percent) => UpdateFile(id, f => { f.State = FileState.Uploading; f.Percent = percent; });
        _transfers.UploadSent += id => UpdateFile(id, f => f.State = FileState.Processing);
        _transfers.UploadFailed += (id, reason) => UpdateFile(id, f => { f.State = FileState.Failed; f.Error = reason; });
        _transfers.DownloadProgress += (id, percent) => UpdateFile(id, f =>
        {
            // [주의] 진행률 이벤트는 BeginInvoke로 늦게 도착한다. 그사이 받기를 취소했다면 "받는 중"으로 되돌리면 안 된다.
            if (!_transfers.IsDownloading(id)) return;
            f.State = FileState.Downloading;
            f.Percent = percent;
        });
        // 대기열에서 차례가 옴 → "대기 중"을 "받는 중 0%"로
        _transfers.DownloadStarted += id => UpdateFile(id, f =>
        {
            if (f.State != FileState.Queued || !_transfers.IsDownloading(id)) return;
            f.State = FileState.Downloading;
            f.Percent = 0;
        });
        _transfers.DownloadCompleted += (id, path) => UiHelper.RunOnUi(this, () => OnDownloadCompleted(id, path));
        _transfers.DownloadFailed += (id, reason) => UiHelper.RunOnUi(this, () =>
        {
            if (!_fileItems.TryGetValue(id, out var entry) || entry.Item.File is not ChatFile file) return;
            file.State = FileState.Ready; // 다시 시도할 수 있게
            entry.Conv.NotifyChanged(entry.Item);
            entry.Conv.Add(new ChatItem { Kind = ChatItemKind.Error, Text = $"'{file.Info.FileName}'을(를) 받지 못했습니다: {reason}" });
        });
    }

    private void UpdateFile(string fileId, Action<ChatFile> change) => UiHelper.RunOnUi(this, () =>
    {
        if (!_fileItems.TryGetValue(fileId, out var entry) || entry.Item.File is not ChatFile file) return;
        // [읽는 법] 실패·취소 뒤에 늦게 도착한 진행률 이벤트를 버린다. 이 한 줄이 없으면 "취소됨" 말풍선이 다시 "보내는 중 37%"로 되살아나는 깜빡임 버그가 생긴다.
        if (file.State is FileState.Failed or FileState.Canceled) return; // 실패·취소 뒤에 늦게 온 진행률은 무시
        change(file);
        entry.Conv.NotifyChanged(entry.Item);
    });

    /// <summary>
    /// 대화창에서 고른 파일 보내기. 사진 미리보기는 작업 스레드에서 만들고(큰 사진도 UI가 멈추지 않게),
    /// 다 되면 UI 스레드에서 "보내는 중" 말풍선을 넣고 전송을 시작한다.
    /// </summary>
    private void SendFiles(Conversation conv, List<string> paths, int? roomId, string? toEmployeeNo)
    {
        // 너무 큰 파일은 미리 걸러서 바로 알려 준다
        foreach (string path in paths.ToList())
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= _transfers.MaxFileSize) continue; // 없는 파일은 StartUpload가 알려 준다
            long size = info.Length;
            paths.Remove(path);
            conv.Add(new ChatItem
            {
                Kind = ChatItemKind.Error,
                Text = $"{Path.GetFileName(path)}: 파일이 너무 큽니다 ({FileRules.FormatSize(size)}). 최대 {FileRules.FormatSize(_transfers.MaxFileSize)}까지 보낼 수 있습니다.",
            });
        }
        if (paths.Count == 0) return;

        // [읽는 법] 두 단계 비동기: ① Task.Run: 작업 스레드(스레드 풀)에서 모든 파일의 미리보기를 만든다 ② ContinueWith: 다 끝나면(정상 완료일 때만) RunOnUi로 UI 스레드에 돌아와 업로드를 시작한다. 큰 사진을 줄이는 일이 UI를 막지 않게 하려는 구조.
        Task.Run(() => paths.Select(path => (Path: path, Thumb: ImageHelper.CreateThumbnail(path))).ToList())
            .ContinueWith(task => UiHelper.RunOnUi(this, () =>
            {
                if (task.Result is not { } prepared) return;
                foreach (var (path, thumb) in prepared) StartUpload(conv, path, thumb, roomId, toEmployeeNo);
            }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void StartUpload(Conversation conv, string path, byte[]? thumbnail, int? roomId, string? toEmployeeNo)
    {
        string fileId;
        try
        {
            fileId = _transfers.StartUpload(path, roomId, toEmployeeNo, thumbnail);
        }
        catch (ArgumentException ex)
        {
            conv.Add(new ChatItem { Kind = ChatItemKind.Error, Text = ex.Message });
            return;
        }

        var info = new FileInfo(path);
        string name = FileRules.SanitizeFileName(info.Name);
        var item = new ChatItem
        {
            Kind = ChatItemKind.Mine,
            Sender = _me.DisplayName,
            SenderKey = _me.EmployeeNo,
            Text = $"[파일] {name}",
            File = new ChatFile
            {
                Info = new SharedFile
                {
                    FileId = fileId, FileName = name, Size = info.Length, Kind = FileRules.GetKind(name),
                    RoomId = roomId, ToEmployeeNo = toEmployeeNo, SenderNo = _me.EmployeeNo, SenderName = _me.DisplayName,
                    Thumbnail = thumbnail,
                },
                State = FileState.Uploading,
                LocalPath = path, // 내가 보낸 파일은 원본을 바로 열 수 있다
            },
        };
        _fileItems[fileId] = (item, conv);
        conv.Add(item);
    }

    /// <summary>서버가 파일을 저장하고 대화에 올렸다 (방: Target = 방 번호, 1:1: Target = 받는 사람).</summary>
    private void HandleFileMessage(Packet packet)
    {
        // [읽는 법] 서버가 준 파일 번호의 모양을 검사한다(허용 문자만). 이 번호는 아래 CachePath에서 폴더 이름으로 쓰이므로, "../" 같은 값이 들어오면 경로 조작이 된다. 통과하지 못하면 패킷을 통째로 무시한다.
        if (packet.GetData<SharedFile>() is not { } shared || !FileRules.IsValidFileId(shared.FileId)) return;
        bool mine = packet.SenderId == _me.EmployeeNo;

        // 내가 보낸 파일: "보내는 중" 말풍선을 완료로 바꾼다
        if (mine && _fileItems.TryGetValue(shared.FileId, out var pending) && pending.Item.File is ChatFile own)
        {
            own.Info = shared;
            own.State = FileState.Ready;
            pending.Conv.NotifyChanged(pending.Item);
            return;
        }

        Conversation conv;
        ChatWindow? window;
        if (shared.RoomId is int roomId)
        {
            if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
            conv = GetRoomConversation(room);
            window = _roomWindows.GetValueOrDefault(roomId);
        }
        else
        {
            string otherNo = mine ? shared.ToEmployeeNo ?? "" : packet.SenderId ?? "";
            if (otherNo.Length == 0) return;
            UserInfo other = _users.FirstOrDefault(u => u.EmployeeNo == otherNo)
                             ?? new UserInfo { EmployeeNo = otherNo, Name = mine ? otherNo : packet.Sender ?? otherNo, Position = "" };
            window = OpenDirect(other, activate: false);
            conv = window.Conversation;
        }

        var item = new ChatItem
        {
            Kind = mine ? ChatItemKind.Mine : ChatItemKind.Other,
            Sender = packet.Sender ?? "",
            SenderKey = packet.SenderId ?? "",
            Text = $"[파일] {shared.FileName}",
            Time = packet.Timestamp,
            File = new ChatFile { Info = shared },
        };
        _fileItems[shared.FileId] = (item, conv);
        AddMessage(conv, window, item);
    }

    /// <summary>파일 말풍선 클릭(열기) / 오른쪽 클릭 메뉴.</summary>
    private void OnFileCommand(ChatWindow window, ChatItem item, FileCommand command)
    {
        if (item.File is not ChatFile file) return;
        // [읽는 법] 방어적 등록: 기록에서 불러왔거나 다른 경로로 만든 말풍선이 사전에 빠져 있을 수 있어, 클릭 시점에 한 번 더 넣어 둔다(이미 있으면 그대로). 이후 진행률 이벤트가 이 말풍선을 찾을 수 있게 하는 조치.
        _fileItems.TryAdd(file.Info.FileId, (item, window.Conversation));

        switch (command)
        {
            case FileCommand.Cancel:
                CancelTransfer(window, item);
                break;

            case FileCommand.Open:
                // 보내는 중·받는 중에 누르면 취소할지 묻는다 (실수로 누를 수 있으니 확인)
                if (file.CanCancel)
                {
                    string what = file.State == FileState.Uploading ? "보내기" : file.State == FileState.Queued ? "받기 대기" : "받기";
                    if (MessageBox.Show(window, $"'{file.Info.FileName}' {what}를 취소할까요?", "전송 취소",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        CancelTransfer(window, item);
                    return;
                }
                if (file.HasLocalFile)
                {
                    if (file.Info.Kind == FileKind.Image) OpenImageViewer(window.Conversation, item);
                    else OpenLocalFile(window, file.LocalPath!, file.Info.FileName);
                    return;
                }
                if (file.IsBusy || file.State == FileState.Failed) return;

                // 예전에 받아 둔 파일이 있으면 다시 받지 않는다
                string cache = CachePath(file.Info);
                // [읽는 법] "예전에 받아 둔 파일이면 다시 받지 않는다"의 판단 기준: 존재하고 크기가 같을 것. 중간에 끊겨 반쯤 받은 파일(크기가 작음)이 재사용되는 것을 막는 최소한의 무결성 검사다(엔진은 완료 전에는 .part 임시 파일에 쓴다).
                if (File.Exists(cache) && new FileInfo(cache).Length == file.Info.Size)
                {
                    file.LocalPath = cache;
                    window.Conversation.NotifyChanged(item);
                    if (file.Info.Kind == FileKind.Image) OpenImageViewer(window.Conversation, item);
                    else OpenLocalFile(window, cache, file.Info.FileName);
                    return;
                }
                StartDownload(window, item, cache, openWhenDone: true);
                break;

            case FileCommand.SaveAs:
                if (file.IsBusy) return;
                using (var dialog = new SaveFileDialog { FileName = file.Info.FileName, Title = "다른 이름으로 저장", OverwritePrompt = true })
                {
                    string ext = FileRules.Extension(file.Info.FileName);
                    dialog.Filter = ext.Length > 0 ? $"{ext.TrimStart('.').ToUpperInvariant()} 파일 (*{ext})|*{ext}|모든 파일 (*.*)|*.*" : "모든 파일 (*.*)|*.*";
                    if (dialog.ShowDialog(window) != DialogResult.OK) return;

                    if (file.HasLocalFile)
                    {
                        try
                        {
                            File.Copy(file.LocalPath!, dialog.FileName, overwrite: true);
                            window.Conversation.Add(new ChatItem { Kind = ChatItemKind.System, Text = $"저장했습니다: {dialog.FileName}" });
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            MessageBox.Show(window, ex.Message, "저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        return;
                    }
                    StartDownload(window, item, dialog.FileName, openWhenDone: false);
                }
                break;

            case FileCommand.ShowInFolder:
                if (file.HasLocalFile)
                    Process.Start("explorer.exe", $"/select,\"{file.LocalPath}\"");
                break;
        }
    }

    private void OpenRoomFiles(ChatWindow window)
    {
        if (window.Kind != ChatWindowKind.Room) return;
        if (!int.TryParse(window.Conversation.Key["room:".Length..], out int roomId)) return;
        if (_roomFilesForms.TryGetValue(roomId, out RoomFilesForm? existing) && !existing.IsDisposed)
        {
            existing.Activate();
            return;
        }
        using var form = new RoomFilesForm(window.Conversation.Title, window.Conversation.Items);
        _roomFilesForms[roomId] = form;
        form.LoadMoreRequested += (before, beforeFileId) =>
        {
            bool sent = _client.Send(Packet.Create(PacketType.RoomFilesReq).WithData(
                new RoomFilesRequest { RoomId = roomId, Before = before, BeforeFileId = beforeFileId }));
            if (!sent) form.AddPage([], hasMore: false, error: "서버 연결이 끊겨 파일 목록을 요청하지 못했습니다.");
        };
        form.FormClosed += (_, _) => _roomFilesForms.Remove(roomId);
        form.OpenRequested += item => OnFileCommand(window, item, FileCommand.Open);
        form.SaveRequested += item => OnFileCommand(window, item, FileCommand.SaveAs);
        form.RequestNextPage();
        form.ShowDialog(window);
    }

    private void HandleRoomFilesResponse(Packet packet)
    {
        RoomFilesBatch? batch = packet.GetData<RoomFilesBatch>();
        if (batch is null || !_roomFilesForms.TryGetValue(batch.RoomId, out RoomFilesForm? form) || form.IsDisposed) return;
        form.AddPage(batch.Files, batch.HasMore, packet.Success ? null : packet.Body ?? "파일 목록 조회에 실패했습니다.");
    }

    /// <summary>
    /// 보내기·받기 취소. 보내기는 "취소됨"으로 남고(원본은 내 PC에 그대로), 받기는 처음 상태로 돌아가 다시 받을 수 있다.
    /// </summary>
    private void CancelTransfer(ChatWindow window, ChatItem item)
    {
        ChatFile file = item.File!;
        if (file.State == FileState.Uploading && _transfers.CancelUpload(file.Info.FileId))
        {
            file.State = FileState.Canceled;
        }
        else if (file.State is FileState.Downloading or FileState.Queued && _transfers.CancelDownload(file.Info.FileId))
        {
            file.State = FileState.Ready;
            file.Percent = 0;
            window.Conversation.Add(new ChatItem { Kind = ChatItemKind.System, Text = $"'{file.Info.FileName}' 받기를 취소했습니다." });
        }
        window.Conversation.NotifyChanged(item);
    }

    private void StartDownload(ChatWindow window, ChatItem item, string destination, bool openWhenDone)
    {
        ChatFile file = item.File!;
        if (!_connected) return;
        if (!_transfers.StartDownload(file.Info.FileId, destination)) return; // 이미 받는 중
        file.OpenWhenDownloaded = openWhenDone;
        // 동시에 3개를 받는 중이면 차례를 기다린다 → "대기 중"으로 구분해 보여 준다
        file.State = _transfers.IsDownloadQueued(file.Info.FileId) ? FileState.Queued : FileState.Downloading;
        file.Percent = 0;
        window.Conversation.NotifyChanged(item);
    }

    private void OnDownloadCompleted(string fileId, string path)
    {
        if (!_fileItems.TryGetValue(fileId, out var entry) || entry.Item.File is not ChatFile file) return;
        file.State = FileState.Ready;
        file.LocalPath = path;
        entry.Conv.NotifyChanged(entry.Item);

        if (file.OpenWhenDownloaded)
        {
            file.OpenWhenDownloaded = false;
            ChatWindow? owner = OpenWindows().FirstOrDefault(w => w.Conversation == entry.Conv);
            if (file.Info.Kind == FileKind.Image) OpenImageViewer(entry.Conv, entry.Item);
            else OpenLocalFile(owner ?? (IWin32Window)this, path, file.Info.FileName);
        }
        else if (_imageViewer is { IsDisposed: false, Visible: true } && _imageViewerConversation == entry.Conv &&
                 _imageViewerItems.ElementAtOrDefault(_imageViewer.CurrentIndex) == entry.Item)
            _imageViewer.ShowImage(path);
        else
        {
            entry.Conv.Add(new ChatItem { Kind = ChatItemKind.System, Text = $"저장했습니다: {path}" });
        }
    }

    private void OpenImageViewer(Conversation conversation, ChatItem selected)
    {
        _imageViewerItems = conversation.Items.Where(i => i.File?.Info.Kind == FileKind.Image).ToList();
        if (_imageViewerItems.Count == 0) return;
        int index = _imageViewerItems.IndexOf(selected);
        if (index < 0) return;
        _imageViewerConversation = conversation;
        if (_imageViewer is null || _imageViewer.IsDisposed)
        {
            _imageViewer = new ImageViewerForm();
            _imageViewer.NavigationRequested += LoadImageViewerItem;
            _imageViewer.FormClosed += (_, _) => { _imageViewer = null; _imageViewerConversation = null; _imageViewerItems.Clear(); };
        }
        if (!_imageViewer.Visible) _imageViewer.Show(this);
        _imageViewer.ShowItem(index, _imageViewerItems.Count, selected.File!.Info.FileName);
        _imageViewer.BringToFront();
        LoadImageViewerItem(index);
    }

    private void LoadImageViewerItem(int index)
    {
        if (_imageViewer is not { IsDisposed: false } viewer || _imageViewerConversation is not { } conversation ||
            index < 0 || index >= _imageViewerItems.Count) return;
        ChatItem item = _imageViewerItems[index];
        if (item.File is not ChatFile file) return;
        viewer.ShowItem(index, _imageViewerItems.Count, file.Info.FileName);
        if (file.HasLocalFile) { viewer.ShowImage(file.LocalPath!); return; }
        string cache = CachePath(file.Info);
        if (File.Exists(cache) && new FileInfo(cache).Length == file.Info.Size)
        {
            file.LocalPath = cache;
            viewer.ShowImage(cache);
            return;
        }
        if (!_connected || file.IsBusy || file.State == FileState.Failed) return;
        if (!_transfers.StartDownload(file.Info.FileId, cache)) return;
        file.State = _transfers.IsDownloadQueued(file.Info.FileId) ? FileState.Queued : FileState.Downloading;
        file.Percent = 0;
        conversation.NotifyChanged(item);
    }

    /// <summary>받은 파일 보관 위치: %LocalAppData%\ChatApp\files\파일ID\원래이름</summary>
    // [읽는 법] 폴더를 파일 번호로 나누는 이유: 서로 다른 대화에서 같은 이름("image.png")의 파일이 와도 덮어쓰지 않는다. 이름은 SanitizeFileName을 거쳐 경로 문자를 제거한다.
    private static string CachePath(SharedFile info)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ChatApp", "files", info.FileId, FileRules.SanitizeFileName(info.FileName));

    /// <summary>윈도우에 연결된 프로그램으로 연다(사진 → 사진 앱, 동영상 → 미디어 플레이어). 실행 파일은 한 번 더 묻는다.</summary>
    private static void OpenLocalFile(IWin32Window owner, string path, string fileName)
    {
        if (FileRules.IsExecutable(fileName) &&
            MessageBox.Show(owner, $"'{fileName}'은(는) 실행 파일입니다.\n보낸 사람을 믿을 수 있을 때만 여세요. 열까요?", "실행 파일 열기",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            // [읽는 법] UseShellExecute = true: 윈도우의 "연결 프로그램"으로 연다(사진 앱, 미디어 플레이어 등). false로 두면 실행 파일만 직접 실행하려 해서 문서·이미지가 열리지 않는다.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(owner, $"파일을 열 수 없습니다: {ex.Message}\n오른쪽 클릭 → 폴더에서 보기로 직접 열어 보세요.", Theme.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
