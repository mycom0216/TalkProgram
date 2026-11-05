using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Services;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 프로필 사진·배경 사진 바꾸기·지우기·받기.
// [저장]     uploads\profiles\사원번호.img(프로필) / 사원번호.bg(배경) — 사람마다 한 장씩, 바꾸면 덮어씀
//            + DB users.photo_version / background_version(바꾼 시각). 채팅 파일과 달리 보관 기간 정리 대상이 아니다.
// [흐름]     ProfilePhotoUpdate(Data = 종류, Bin = 사진) → 저장 → 내 세션 UserInfo의 버전 갱신 → 접속자 목록 방송
//            → 다른 사람 화면은 버전이 바뀐 것을 보고 필요할 때 ProfilePhotoReq로 새 사진을 한 번 받아 간다(캐시).
// [스레드]   요청한 사람의 수신 스레드에서 실행된다. 각자 자기 파일만 쓰므로 사람끼리 겹치지 않는다.
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   프로필 사진과 배경 사진을 서버 디스크에 한 사람당 한 장씩 저장·삭제·전달한다. 사진이 "바뀌었는지"는 버전(바꾼 시각)으로 알려 준다.
//
// ■ 한 장 그림 (사진 바꾸기 → 다른 사람 화면까지)
//   내가 사진 선택 → HandleUpdate: 크기·형식 검사 → 임시 파일에 쓰고 교체 → DB에 버전(밀리초 시각) 저장 → 내 세션 정보의 버전 갱신
//   → 나에게는 사진을 바로 돌려줌 / 다른 사람에게는 "접속자 목록 방송"으로 버전만 알림 → 그들의 클라이언트가 버전이 달라진 것을 보고 HandleRequest로 새 사진을 한 번 받아 감(캐시)
//   핵심: 사진 자체를 모든 사람에게 뿌리지 않는다. "버전만" 방송하고, 필요한 사람이 필요할 때 가져간다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 캐시 무효화를 버전 번호로 ............................ "cache busting version number ETag"
//   - 임시 파일에 쓰고 교체(원자적 교체) .................... "atomic file replace write temp then move"
//   - 매직 바이트로 파일 형식 확인 ......................... "file signature magic bytes validation"
//   - 경로 조작 방어(사원번호를 영문·숫자만 허용) .......... "path traversal sanitize filename"
//   - 확장 메서드 this Packet packet ...................... "C# extension methods"
//   - 불변 객체의 with 복사 (u.WithPhoto) .................. "immutable object with-expression copy"
//
// ■ 면접 예상 질문과 답
//   Q1. 사진을 바꿨을 때 다른 사람 화면이 갱신되는 방법은요? 사진을 모두에게 보내나요?
//       A. 아니요. 사진을 보내면 접속자 수만큼 큰 데이터가 나가므로, 접속자 목록에 들어 있는 "사진 버전"(바꾼 시각)만 방송합니다. 다른 클라이언트는 버전이 자기가 캐시한 것과 다르면 그때 한 번 사진을 요청해 갑니다. 버전이 같으면 다시 받지 않습니다.
//   Q2. 업로드된 사진이 정말 이미지인지 어떻게 확인하나요?
//       A. 크기 제한(프로필 256KB, 배경 600KB)을 먼저 확인하고, 파일 맨 앞 몇 바이트(매직 바이트)가 JPEG(FF D8 FF) 또는 PNG(89 50 4E 47 0D 0A) 시그니처인지 봅니다. 확장자나 이름은 믿지 않습니다.
//   Q3. 사진 파일 이름에 사원번호를 쓰는데 경로 조작은 어떻게 막나요?
//       A. 사원번호를 영문·숫자 20자 이하로만 허용합니다(IsSafeNo). "../" 같은 문자는 아예 통과하지 못해 업로드 폴더 밖에 쓸 수 없습니다. 또 파일 확장자는 사용자가 정한 값이 아니라 서버가 정한 .img/.bg만 씁니다.
//   Q4. 임시 파일(.tmp)에 쓰고 Move로 덮어쓰는 이유는요?
//       A. 쓰는 도중에 다른 사람이 사진을 요청하면 반쪽짜리 파일을 읽을 수 있기 때문입니다. 다 쓴 뒤 이름을 바꾸는 한 동작으로 교체하면 읽는 쪽은 항상 이전 사진이나 새 사진 중 하나를 온전히 받습니다.
//   Q5. 사진을 지우면 버전은 어떻게 되나요?
//       A. 파일을 지우고 버전을 0으로 저장합니다. 클라이언트는 버전 0을 "사진 없음"으로 보고 기본 아바타를 보여 줍니다. 요청 시 버전은 있는데 파일이 없어진 경우에도 0으로 응답해 같은 결과가 됩니다.
//   Q6. 같은 사람이 동시에 사진을 바꾸면 문제가 없나요?
//       A. 한 사람의 요청은 그 사람의 수신 스레드 하나에서 순서대로 처리되므로 같은 파일을 동시에 쓰지 않습니다. 사람마다 자기 파일만 쓰기 때문에 서로 다른 사람끼리도 겹치지 않습니다. 세션의 사용자 정보 갱신(UpdateUser)은 lock 안에서 합니다.
//   Q7. 한계는요? (솔직하게 답하기)
//       A. (1) 형식 확인이 앞 몇 바이트뿐이라, 앞부분만 JPEG처럼 꾸민 가짜 파일은 통과할 수 있다(클라이언트가 이미지로 해석하지 못하면 표시되지 않을 뿐이지만, 서버가 이미지를 한 번 해석해 보는 검증이 더 안전하다). (2) 사진 요청(HandleRequest)에는 간격 제한이 없어 큰 사진을 반복해 요청하면 서버 대역폭을 쓴다. (3) 사진은 보관 기간 정리 대상이 아니므로, 퇴사한 사람의 사진 파일이 남아 있을 수 있다(정리 기능이 따로 필요).
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - "사진이 너무 큽니다" ...................... ProfileRules.MaxBytes(프로필 256KB, 배경 600KB). 클라이언트가 먼저 줄여 보내는지 확인
//   - "JPEG 또는 PNG 사진만" .................... IsSupportedImage(매직 바이트). 다른 형식(WEBP 등)은 클라이언트에서 변환 필요
//   - 사진을 바꿨는데 다른 사람 화면은 그대로 .. broadcastUserList 호출 여부, 접속자 목록의 PhotoVersion 값, 클라이언트 캐시
//   - "서버에 사진을 저장하지 못했습니다" ....... uploads\profiles 폴더 쓰기 권한·디스크 공간, 로그의 IOException 메시지
// ───────────────────────────────────────────────────────────────────────────
/// <summary>프로필 사진·배경 사진을 서버 디스크(업로드 폴더\profiles)에 저장하고 보내 주는 서비스.</summary>
// [읽는 법] 마지막 Action broadcastUserList는 "접속자 목록을 모두에게 다시 보내라"는 신호 함수다. 사진 버전이 바뀌면 이 목록으로 다른 사람들에게 변경을 알린다.
public sealed class ProfileService(UserRepository users, string uploadRoot, ServerLogger log, Action broadcastUserList)
{
    private readonly string _dir = Path.Combine(uploadRoot, "profiles");

    /// <summary>사원번호가 파일 이름으로 안전한지 (경로 조작 차단: 영문·숫자만).</summary>
    // [읽는 법] 사원번호가 "1~20자, 영문·숫자만"이어야 통과한다. 이 값이 그대로 파일 이름에 쓰이므로(경로 조작 차단) 모든 진입점(HandleUpdate, HandleRequest)이 가장 먼저 호출한다.
    private static bool IsSafeNo(string no) => no.Length is > 0 and <= 20 && no.All(char.IsAsciiLetterOrDigit);

    private string PathOf(string no, PhotoKind kind)
        => Path.Combine(_dir, no.ToUpperInvariant() + (kind == PhotoKind.Background ? ".bg" : ".img"));

    private static string KindText(PhotoKind kind) => kind == PhotoKind.Background ? "배경 사진" : "프로필 사진";

    /// <summary>내 사진 바꾸기 (Bin이 비어 있으면 지우기).</summary>
    public void HandleUpdate(ClientSession session, Packet packet)
    {
        UserInfo me = session.User!;
        if (!IsSafeNo(me.EmployeeNo)) return;
        PhotoKind kind = packet.GetData<PhotoInfo>()?.Kind ?? PhotoKind.Profile;
        // [읽는 법] 클라이언트가 보낸 종류 값이 enum에 정의된 값(프로필/배경)인지 확인한다. enum은 숫자라서 99 같은 엉뚱한 값도 들어올 수 있기 때문이다.
        if (!Enum.IsDefined(kind)) return;
        byte[]? photo = packet.Bin;
        // [읽는 법] 같은 함수가 "바꾸기"와 "지우기"를 겸한다: 사진 바이트가 비어 있으면 지우기다. 그래서 크기·형식 검사는 !remove일 때만 한다.
        bool remove = photo is null || photo.Length == 0;
        if (!remove && photo!.Length > ProfileRules.MaxBytes(kind))
        {
            Fail(session, kind, $"사진이 너무 큽니다. (최대 {ProfileRules.MaxBytes(kind) / 1024}KB)");
            return;
        }
        if (!remove && !ProfileRules.IsSupportedImage(photo))
        {
            Fail(session, kind, "JPEG 또는 PNG 사진만 쓸 수 있습니다.");
            return;
        }

        string path = PathOf(me.EmployeeNo, kind);
        long version = 0;
        try
        {
            if (remove)
            {
                File.Delete(path);
            }
            else
            {
                Directory.CreateDirectory(_dir);
                // [왜] 임시 파일에 다 쓴 뒤 바꿔치기한다. 쓰는 도중에 다른 사람이 받아 가도 반쪽짜리 사진을 받지 않는다.
                // [순서] ① 임시 파일에 전부 쓰기 → ② Move(overwrite: true)로 이름 바꾸기. ②는 거의 즉시 끝나는 한 동작이라 읽는 쪽은 반쪽 파일을 만나지 않는다.
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, photo!);
                File.Move(temp, path, overwrite: true);
                // [개념] 버전 = 바꾼 시각(1970년부터 경과한 밀리초). 바꿀 때마다 값이 커지므로 "다르면 새 사진"으로 판단할 수 있다. 지우기는 0. 0 이상의 정수라 DB 칸에 그대로 저장된다.
                version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"{KindText(kind)} 저장 실패({me.EmployeeNo}): {ex.Message}");
            Fail(session, kind, "서버에 사진을 저장하지 못했습니다.");
            return;
        }

        users.SetPhotoVersion(me.EmployeeNo, kind, version);
        // [읽는 법] 내 세션이 들고 있는 UserInfo의 버전만 바꾼 새 객체로 교체한다(원본을 직접 고치지 않는 불변 방식 + lock 안의 교체). 다른 스레드가 접속자 목록을 만들 때 이 값이 반영된다.
        session.UpdateUser(u => kind == PhotoKind.Background ? u.WithBackground(version) : u.WithPhoto(version));
        log.Info($"{KindText(kind)} {(remove ? "지움" : "바꿈")}: {me.EmployeeNo}{(remove ? "" : $" ({photo!.Length / 1024}KB)")}");
        // 나에게는 사진을 바로 돌려줘서 화면이 곧바로 바뀌게 하고, 다른 사람에게는 목록 방송으로 "버전이 바뀜"만 알린다
        // [읽는 법] 나에게는 사진을 바로 돌려줘서 화면이 곧 바뀐다. 다른 사람들에게는 바로 아래 broadcastUserList()가 "버전이 바뀜"만 알린다 — 같은 알림을 두 경로로 나눠 큰 데이터 전송을 필요한 사람에게만 한다.
        session.Send(Packet.Create(PacketType.ProfilePhoto, $"{KindText(kind)}을 {(remove ? "지웠습니다" : "바꿨습니다")}.", target: me.EmployeeNo)
            .WithPhoto(kind, version, remove ? null : photo));
        broadcastUserList();
    }

    /// <summary>다른 사람의 사진 받기 (Body = 사원번호, Data = 종류). 사진이 없으면 Bin 없이 버전 0.</summary>
    public void HandleRequest(ClientSession session, Packet packet)
    {
        string no = packet.Body?.Trim() ?? "";
        if (!IsSafeNo(no)) return;
        PhotoKind kind = packet.GetData<PhotoInfo>()?.Kind ?? PhotoKind.Profile;
        if (!Enum.IsDefined(kind)) return;
        long version = users.GetPhotoVersion(no, kind);
        byte[]? photo = null;
        if (version > 0)
        {
            // [읽는 법] 버전이 0보다 크면 사진이 있어야 하지만, 파일이 수동으로 지워졌을 수도 있다. 읽기에 실패하면 예외를 밖으로 내지 않고 버전 0("사진 없음")으로 답한다.
            try { photo = File.ReadAllBytes(PathOf(no, kind)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { version = 0; } // 파일이 없어졌으면 "사진 없음"
        }
        session.Send(Packet.Create(PacketType.ProfilePhoto, target: no).WithPhoto(kind, version, photo));
    }

    private static void Fail(ClientSession session, PhotoKind kind, string message)
        => session.Send(Packet.Create(PacketType.ProfilePhoto, message, success: false).WithData(new PhotoInfo { Kind = kind }));
}

/// <summary>프로필 사진 응답 패킷을 만드는 Packet 확장 메서드 모음.</summary>
internal static class ProfilePacketExtensions
{
    /// <summary>ProfilePhoto 패킷에 종류·버전(Data = PhotoInfo)과 사진 바이트(Bin)를 담는다.</summary>
    // [개념] 확장 메서드: 첫 매개변수 앞의 this 때문에 Packet 클래스를 고치지 않고도 packet.WithPhoto(...)처럼 쓸 수 있다. 사진 종류·버전은 Data에, 사진 바이트는 Bin 칸에 따로 싣는다. Bin은 JSON 안에서 Base64 문자열이 되어 크기가 약 4/3배로 늘어나므로(그래서 사진 최대 크기를 패킷 한도 1MB보다 훨씬 작게 잡았다), 값이 없으면 칸 자체를 쓰지 않는다.
    public static Packet WithPhoto(this Packet packet, PhotoKind kind, long version, byte[]? photo)
    {
        packet.Bin = photo;
        return packet.WithData(new PhotoInfo { Kind = kind, Version = version });
    }
}
