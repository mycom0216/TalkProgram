using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ChangePasswordForm.cs - 비밀번호 변경 대화상자
// - 두 가지로 쓰인다.
//   ① 일반: 메인 창의 내 이름 메뉴 → "비밀번호 변경". 현재 비밀번호 + 새 비밀번호를 입력.
//   ② 강제: 관리자가 초기화한 "임시 비밀번호"로 로그인한 직후(LoginForm). 바꾸기 전에는 채팅을 쓸 수 없다.
//      현재 비밀번호(= 방금 입력한 임시 비밀번호)는 이미 알고 있으므로 입력칸을 숨기고 자동으로 넣는다.
// - 스레드: 화면은 UI 스레드. 응답(ChangePasswordRes)은 수신 스레드에서 와서 RunOnUi로 넘긴다.
// - 성공하면 NewPassword에 새 비밀번호가 남아 있다 → 부르는 쪽이 자동 재접속용 LastLogin을 고친다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   비밀번호 변경 창 하나를 두 가지 상황에 쓴다. ① 일반: 현재 + 새 비밀번호 입력 ② 강제: 관리자가 초기화한 임시 비밀번호로 로그인한 직후(생성자에 currentPassword를 넘김) → 현재 비밀번호 칸을 숨기고 자동으로 채운다. 취소 버튼은 "로그아웃"으로 바뀐다.
//
// ■ 흐름
//   ① Submit(): 화면 검사(현재 비밀번호 있음, 새 비밀번호 8~64자 + 영문 + 숫자, 확인 일치) → ChangePasswordReq 전송, 저장 버튼 비활성화
//   ② 서버(PacketHandler.HandleChangePassword → AuthService.ChangePassword)가 다시 검사하고 결과를 ChangePasswordRes로 응답
//   ③ 응답은 수신 스레드로 오므로 RunOnUi → HandleResponse: 실패면 서버 메시지를 상태 줄에, 성공이면 NewPassword에 보관하고 OK로 닫는다
//   ④ 부르는 쪽(LoginForm 등)이 NewPassword로 자동 재접속용 로그인 정보를 갱신한다(안 하면 재접속 때 옛 비밀번호로 시도해 실패)
//
// ■ 서버 쪽에서 일어나는 일 (코드로 확인함)
//   - 임시 비밀번호 상태(MustChangePassword)에서 성공하면 서버가 그 표시를 풀고 로그인을 마무리(CompleteLogin)한다. 그 전에는 비밀번호 변경·핑·로그아웃 외의 요청을 막는다.
//   - 비밀번호를 바꾸거나 재설정하면 서버가 기존 자동 로그인 토큰을 모두 지운다 → 이 PC에 저장된 토큰도 다음 번 자동 로그인에서 거절되고, LoginForm이 토큰을 지운 뒤 비밀번호 입력으로 돌아간다.
//   - 감사 로그에 PasswordChange가 남는다(임시 비밀번호 변경이면 "임시 비밀번호 변경" 표시). 비밀번호 값은 기록하지 않는다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 강제 비밀번호 변경(첫 로그인 후 변경) ......... "force password change temporary password flow"
//   - 화면 검증 + 서버 최종 검증 ................... "client side validation server side validation password policy"
//   - 비밀번호 변경 시 토큰/세션 무효화 ............. "invalidate tokens after password change"
//   - 비밀번호를 평문 변수로 들고 있는 문제 ......... "password in memory string immutable"
//   - 생성자 매개변수로 모드 전환(forced) ........... "dialog mode flag constructor parameter"
//
// ■ 면접 예상 질문과 답
//   Q1. 비밀번호 규칙 검사를 화면과 서버 양쪽에서 하는 이유는요?
//       A. 화면 검사는 서버 왕복 없이 바로 알려 주는 편의입니다. 규칙을 지키는 것은 서버이고(요청을 직접 만들어 보내는 경우까지 막아야 하므로), 서버가 같은 규칙(8~64자, 영문+숫자)으로 다시 검사합니다.
//   Q2. 임시 비밀번호로 로그인한 사용자가 변경을 건너뛸 수 있나요?
//       A. 서버가 MustChangePassword 상태에서는 변경·핑·로그아웃 외의 요청을 막습니다. 클라이언트도 변경하지 않으면 연결을 끊고 로그인하지 않은 것으로 처리합니다(취소 버튼 = 로그아웃).
//   Q3. 비밀번호를 바꾸면 자동 로그인은 어떻게 되나요?
//       A. 서버가 기존 토큰을 모두 지웁니다. 그래서 이전 토큰으로는 로그인할 수 없고, 새 비밀번호로 다시 로그인하면 새 토큰이 발급됩니다.
//   Q4. 현재 비밀번호를 왜 또 입력받나요?
//       A. 로그인된 PC가 잠시 열려 있는 사이에 다른 사람이 비밀번호를 바꿔 버리는 것을 막기 위해서입니다. 서버가 현재 비밀번호를 확인합니다. (강제 변경에서는 방금 입력한 임시 비밀번호를 자동으로 넣는다.)
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - 비밀번호가 string으로 메모리에 남는다(string은 바꿀 수 없고 GC가 언제 지울지 모른다). 자동 재접속용으로 LastLogin에도 보관된다. 현재 구조에서는 감수하는 부분이며, 토큰 로그인으로 점점 줄이는 방향이다.
//   - 규칙(8~64자, 영문+숫자)이 화면 코드, 안내 문구, 서버 코드에 각각 적혀 있다. Common에 규칙 클래스로 모으면 한 곳만 고치면 된다(전화번호는 PhoneRules가 이미 그렇게 되어 있음).
//   - 변경에 성공한 뒤 메시지 상자를 한 번 더 띄운다(성공 알림). 강제 흐름에서는 곧바로 채팅 화면으로 넘어가므로 생략해도 되는 단계다.
// ───────────────────────────────────────────────────────────────────────────

public sealed class ChangePasswordForm : Form
{
    private readonly ChatClient _client;
    private readonly TextBox _current = new() { UseSystemPasswordChar = true, MaxLength = 64 };
    private readonly TextBox _new = new() { UseSystemPasswordChar = true, MaxLength = 64 };
    private readonly TextBox _confirm = new() { UseSystemPasswordChar = true, MaxLength = 64 };
    private readonly Button _save = UiHelper.CreateButton("변경", primary: true);
    private readonly Button _cancel = UiHelper.CreateButton("취소");
    private readonly Label _status = new() { AutoSize = true, ForeColor = UiHelper.Muted, Margin = new Padding(0, 8, 0, 0) };

    /// <summary>성공한 새 비밀번호 (자동 재접속용).</summary>
    // [왜] 성공했을 때만 채운다. 부르는 쪽이 읽어 자동 재접속용 로그인 정보를 새 비밀번호로 바꾼다. private set이라 바깥에서 바꿀 수 없다.
    public string NewPassword { get; private set; } = "";

    /// <param name="currentPassword">강제 변경일 때 방금 로그인한 임시 비밀번호. null이면 일반 변경(입력칸 보임).</param>
    public ChangePasswordForm(ChatClient client, string? currentPassword = null)
    {
        _client = client;
        // [읽는 법] 매개변수가 null이 아니면 "강제 변경" 모드. 이 한 줄이 아래의 화면 구성(현재 비밀번호 칸 숨김, 안내 문구, "로그아웃" 버튼, 작업 표시줄 표시)을 모두 가른다.
        bool forced = currentPassword is not null;

        Text = $"{Theme.AppName} - 비밀번호 변경";
        Font = UiHelper.BaseFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        // [왜] 강제 모드는 로그인 창 대신 뜨는 창이라 작업 표시줄에 없으면 다른 창에 가려 찾기 어렵다. 일반 모드는 메인 창의 자식 대화상자라 숨긴다.
        ShowInTaskbar = forced; // 로그인 창 대신 뜨는 창이라 작업 표시줄에 보여야 찾기 쉽다
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        var table = UiHelper.CreateFormTable();
        if (forced)
        {
            // [읽는 법] 방금 입력한 임시 비밀번호를 칸에 미리 넣고 그 칸은 화면에 올리지 않는다. 서버에는 이 값이 "현재 비밀번호"로 전달되어 확인된다.
            _current.Text = currentPassword;
            UiHelper.AddSpanRow(table, new Label
            {
                Text = "관리자가 발급한 임시 비밀번호로 로그인했습니다.\n계속하려면 새 비밀번호로 바꾸세요.",
                AutoSize = true,
                ForeColor = UiHelper.Danger,
                Margin = new Padding(0, 0, 0, 10),
            });
        }
        else
        {
            UiHelper.AddFieldRow(table, "현재 비밀번호", _current, 220);
        }
        UiHelper.AddFieldRow(table, "새 비밀번호", _new, 220);
        UiHelper.AddFieldRow(table, "새 비밀번호 확인", _confirm, 220);
        UiHelper.AddSpanRow(table, new Label
        {
            Text = "영문+숫자 8~64자",
            AutoSize = true,
            ForeColor = UiHelper.Muted,
            Margin = new Padding(0, 4, 0, 0),
        });
        UiHelper.AddSpanRow(table, _status);
        // [왜] 강제 모드에서 "취소"는 변경을 포기하는 것 = 로그인하지 않는 것이다. 사용자가 결과를 알 수 있게 글자를 바꾼다(창을 닫으면 LoginForm이 연결을 끊는다).
        if (forced) _cancel.Text = "로그아웃";
        UiHelper.AddSpanRow(table, UiHelper.CreateButtonRow(_cancel, _save));
        Controls.Add(table);
        UiHelper.AddDialogHeader(this, table, "비밀번호 변경");

        AcceptButton = _save;
        CancelButton = _cancel;
        _save.Click += (_, _) => Submit();
        _cancel.Click += (_, _) => Close();

        _client.PacketReceived += OnPacketReceived;
        FormClosed += (_, _) => _client.PacketReceived -= OnPacketReceived;
        Shown += (_, _) => (forced ? _new : _current).Focus();
    }

    private void Submit()
    {
        // [왜] 화면에서 먼저 검사해 서버 왕복 없이 바로 알려 준다. 최종 검사는 서버(AuthService.ChangePassword)가 다시 한다.
        if (_current.Text.Length == 0) { ShowStatus("현재 비밀번호를 입력하세요.", true); return; }
        // [개념] is < 8 or > 64 : 패턴 매칭의 "또는" 조합(8 미만이거나 64 초과). char.IsLetter/IsDigit는 한글 같은 문자도 글자로 보므로, 서버의 규칙과 완전히 같은 함수를 써야 화면·서버 판단이 어긋나지 않는다.
        if (_new.Text.Length is < 8 or > 64 || !_new.Text.Any(char.IsLetter) || !_new.Text.Any(char.IsDigit))
        {
            ShowStatus("새 비밀번호는 영문과 숫자를 포함해 8~64자로 입력하세요.", true);
            return;
        }
        if (_new.Text != _confirm.Text) { ShowStatus("새 비밀번호 확인이 일치하지 않습니다.", true); return; }

        _save.Enabled = false;
        ShowStatus("변경 중...", false);
        // [주의] 비밀번호를 요청 객체에 그대로 담아 전송한다. 보호는 TLS(전송 구간 암호화)에 의존하므로, 암호화를 끈 접속에서는 노출될 수 있다(LoginForm이 그 경우 경고한다).
        var request = new ChangePasswordRequest { CurrentPassword = _current.Text, NewPassword = _new.Text };
        if (!_client.Send(Packet.Create(PacketType.ChangePasswordReq).WithData(request)))
        {
            _save.Enabled = true;
            ShowStatus("서버와 연결이 끊어졌습니다.", true);
        }
    }

    // [스레드] 수신 스레드에서 불린다 → UI 스레드로 넘긴다.
    private void OnPacketReceived(Packet packet)
    {
        if (packet.Type == PacketType.ChangePasswordRes)
            UiHelper.RunOnUi(this, () => HandleResponse(packet));
    }

    private void HandleResponse(Packet packet)
    {
        _save.Enabled = true;
        if (!packet.Success)
        {
            ShowStatus(packet.Body ?? "비밀번호를 바꾸지 못했습니다.", true);
            return;
        }
        // [읽는 법] 서버가 성공 응답을 준 뒤에만 보관한다. 이 값이 LoginForm의 LastLogin(자동 재접속용)에 들어간다.
        NewPassword = _new.Text;
        MessageBox.Show(this, packet.Body ?? "비밀번호를 바꿨습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowStatus(string message, bool error)
    {
        _status.Text = message;
        _status.ForeColor = error ? UiHelper.Danger : UiHelper.Muted;
    }
}
