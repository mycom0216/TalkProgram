using ChatApp.Client.Ui;
using ChatApp.Common.Models;

namespace ChatApp.Client.Chat;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ChatFile.cs - 파일·사진·동영상 말풍선 하나의 "상태"
// - 역할: 서버가 알려 준 파일 정보(SharedFile)에 더해, 내 PC에서의 진행 상황(보내는 중/받는 중 %, 실패 이유,
//         내 PC에 저장된 경로)을 담는다. BubbleChatView가 이 값을 보고 말풍선을 그린다.
// - 스레드: UI 스레드에서만 값을 바꾼다. 전송 엔진(FileTransferService)의 진행률 이벤트는 작업/수신 스레드에서
//           발생하지만, MainForm이 BeginInvoke로 UI 스레드에 넘긴 뒤에 이 객체를 바꾼다.
// - 연결: ChatItem.File → ChatFile → SharedFile(Common, 서버와 주고받는 정보)
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   파일 말풍선 하나의 "내 PC 기준 상태"(보내는 중/받는 중/실패/저장 경로)를 담는 작은 데이터 클래스. 서버와 주고받는 정보(SharedFile)와 내 PC만의 상태를 분리해 둔 것이 핵심.
//
// ■ 상태 흐름 (FileState)
//   보내기 : Uploading(%) → Processing(서버 저장 중) → Ready(대화에 올라감)   / 실패 Failed, 취소 Canceled
//   받기   : Ready → Queued(동시 3개 제한으로 대기) → Downloading(%) → Ready(LocalPath 채워짐)
//   갱신하는 곳: MainForm.Files.cs의 전송 이벤트 핸들러(UploadProgress, UploadSent, UploadFailed 등)가 UI 스레드에서 바꾼다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - enum + 패턴 매칭(is A or B) ............ "C# enum switch pattern matching is or"
//   - required 속성 ......................... "C# 11 required members"
//   - 지연 로딩(Lazy loading) + 시도 여부 플래그 .. "lazy loading cache failed attempt flag"
//   - 상태 기계(State machine) .............. "UI state machine progress download upload"
//
// ■ 면접 예상 질문과 답
//   Q1. 서버가 주는 SharedFile만 쓰지 않고 ChatFile로 한 번 더 감싼 이유는요?
//       A. SharedFile은 서버와 공유하는 "사실"(이름, 크기, 번호)이라 직렬화되어 오가고, 진행률·로컬 경로·실패 이유는 이 PC에서만 의미 있는 "화면 상태"입니다. 섞으면 프로토콜 모델이 UI 사정으로 오염되므로 분리했습니다.
//   Q2. Thumbnail에서 _thumbnailLoaded 플래그를 따로 둔 이유는요?
//       A. 이미지 변환에 실패하면 결과가 null인데, null만 검사하면 그릴 때마다(초당 수십 번) 변환을 다시 시도합니다. "이미 시도했다"를 따로 기억해 단 한 번만 시도합니다. 지연 로딩이라 화면에 그려지는 말풍선만 이미지로 바뀌어 메모리도 아낍니다.
//   Q3. 이 클래스에 lock이 없는데 스레드 안전한가요?
//       A. UI 스레드에서만 읽고 쓰기 때문입니다. 전송 스레드의 진행률은 MainForm이 BeginInvoke로 UI 스레드에 넘긴 뒤에 이 객체를 바꿉니다. 규칙이 깨지면(다른 스레드에서 직접 수정) 안전하지 않다는 한계가 있어 주석으로 규칙을 남겼습니다.
//   Q4. HasLocalFile이 File.Exists까지 확인하는 이유는요?
//       A. LocalPath 값이 있어도 사용자가 파일을 지우거나 옮겼을 수 있습니다. 실제로 있는지 확인해서 없으면 다시 받게 합니다. 대신 디스크 확인 비용이 있어 그리기 루프 안에서 자주 부르면 느려질 수 있다는 점을 알고 써야 합니다.
//   Q5. 한계는요?
//       A. (1) 속성이 모두 공개 set이라 상태 전이 규칙(예: Ready에서만 Downloading으로)을 코드가 강제하지 않고 호출하는 쪽이 지킨다. (2) Thumbnail이 만든 Image는 Dispose하지 않아 GDI 핸들을 쥔 채 남을 수 있다(대화가 매우 길면 부담).
//
// ■ 문제가 났을 때 어디를 볼까
//   - 진행률이 안 움직인다 ................. MainForm.Files.cs의 UpdateFile 호출, 전송 이벤트가 BeginInvoke로 UI에 넘어오는지
//   - 받은 파일을 또 받으라고 한다 ........... LocalPath가 비었거나 파일이 삭제됨(HasLocalFile)
//   - 취소 버튼이 안 눌린다 ................. CanCancel(Processing 중에는 의도적으로 불가)
// ───────────────────────────────────────────────────────────────────────────

/// <summary>파일 말풍선의 진행 상태. 말풍선에 표시되는 문구와 진행 막대가 이 값으로 정해진다.</summary>
// [개념] enum(열거형): 정해진 몇 가지 값 중 하나만 가질 수 있는 형식. 문자열("uploading")보다 오타 걱정이 없고,
//   switch에서 빠뜨린 경우를 찾기 쉽다.
internal enum FileState
{
    Uploading,    // 내가 보내는 중 (Percent)
    Processing,   // 다 보냈고 서버가 저장하는 중
    Ready,        // 대화에 올라감 (받을 수 있음)
    Downloading,  // 받는 중 (Percent)
    Queued,       // 받기를 눌렀지만 차례를 기다리는 중 (동시에 3개까지만 받으므로)
    Failed,       // 보내기 실패 (Error)
    Canceled,     // 내가 보내기를 취소함
}

/// <summary>
/// 말풍선에 붙은 파일. 진행률·상태가 바뀌면 Conversation.NotifyChanged로 화면에 알린다.
/// UI 스레드에서만 바꾼다.
/// </summary>
internal sealed class ChatFile
{
    // [개념] required: 객체를 만들 때 반드시 값을 넣어야 하는 속성. new ChatFile { Info = ... } 처럼 빠뜨리면 컴파일 오류.
    //   파일 정보 없는 파일 말풍선은 의미가 없으므로 실수를 컴파일 단계에서 막는다.
    public required SharedFile Info { get; set; }
    public FileState State { get; set; } = FileState.Ready;
    public int Percent { get; set; }   // 0~100, Uploading/Downloading일 때 진행 막대에 쓰인다
    public string? Error { get; set; } // Failed일 때 말풍선에 보여 줄 실패 이유

    /// <summary>내 PC에 있는 경로(내가 보낸 원본 또는 받은 파일). 있으면 열 때 다시 받지 않는다.</summary>
    public string? LocalPath { get; set; }

    /// <summary>받기가 끝나면 열지(true) 저장만 할지(false).</summary>
    // [왜] 말풍선 클릭(열기)과 "다른 이름으로 저장"은 둘 다 내려받기를 하지만, 끝난 뒤의 동작이 다르기 때문에 기억해 둔다.
    public bool OpenWhenDownloaded { get; set; }

    private Image? _thumbnail;
    private bool _thumbnailLoaded;

    /// <summary>이미지 미리보기 (없으면 null → 파일 카드로 그림).</summary>
    // [개념] 지연 로딩(Lazy loading): 미리보기 바이트 → Image 변환을 처음 필요할 때 딱 한 번만 한다.
    //   지난 대화 50개를 불러와도 화면에 실제로 그려지는 말풍선만 이미지로 바뀌므로 빠르고 메모리도 아낀다.
    // [왜] _thumbnailLoaded를 따로 두는 이유: 변환에 실패하면 _thumbnail이 null인데, null인지만 보면
    //   그릴 때마다(초당 수십 번) 변환을 다시 시도하게 된다. "이미 시도했음"을 따로 기억해서 한 번만 시도한다.
    public Image? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;
                _thumbnail = ImageHelper.Load(Info.Thumbnail);
            }
            return _thumbnail;
        }
    }

    // [주의] File.Exists는 디스크를 확인하므로, 사용자가 받은 파일을 지웠거나 옮겼으면 false가 되어 다시 받게 된다.
    public bool HasLocalFile => LocalPath is not null && File.Exists(LocalPath);

    // [개념] is A or B or C : C# 패턴 매칭. State == A || State == B || State == C 와 같다.
    // 전송 중에는 같은 파일을 또 열기/저장하지 못하게 막는 데 쓴다.
    // [읽는 법] "지금 전송 관련 작업 중인가?" 전송 중에 같은 파일을 또 열거나 저장하지 못하게 막는 용도. Ready/Failed/Canceled일 때만 새 작업을 시작할 수 있다.
    public bool IsBusy => State is FileState.Uploading or FileState.Processing or FileState.Downloading or FileState.Queued;

    /// <summary>지금 취소할 수 있는지. "서버에 저장하는 중"(Processing)은 이미 다 보낸 뒤라 취소하지 않는다.</summary>
    public bool CanCancel => State is FileState.Uploading or FileState.Downloading or FileState.Queued;
}
