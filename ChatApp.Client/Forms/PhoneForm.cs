using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] PhoneForm.cs - 내 휴대폰 번호 등록·변경·삭제 창
// - 등록해 두면 다른 사람이 [친구 추가 → 휴대폰 번호]로 나를 찾을 수 있다. 번호 자체는 아무에게도 보이지 않는다.
// - 창이 뜨면 PhoneInfoReq로 지금 번호를 받아 오고, 저장·삭제 결과도 PhoneInfo로 받는다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   내 휴대폰 번호를 등록·변경·삭제하는 작은 창. 창이 뜨면 PhoneInfoReq로 지금 번호를 받아 오고, 저장/삭제(PhoneUpdate) 결과도 같은 PhoneInfo 패킷으로 받아 화면을 갱신한다. 저장과 삭제가 하나의 패킷 종류(PhoneUpdate)를 쓰고, 본문이 빈 문자열이면 "삭제"다.
//
// ■ 흐름
//   ① Shown: PhoneInfoReq 전송 → 응답(PhoneInfo)의 번호를 입력칸에 채우고, 번호가 없으면 [번호 지우기]를 끈다
//   ② [저장]: PhoneRules.Normalize로 형식 확인 → PhoneUpdate(본문 = 입력 그대로). 서버가 다시 정리·검사해서 저장
//   ③ [번호 지우기]: 확인 대화상자 → PhoneUpdate(본문 = "")
//   ④ 요청을 보내면 두 버튼을 모두 끄고, 응답이 오면 다시 켠다(중복 클릭 방지)
//
// ■ 서버 쪽에서 일어나는 일 (코드로 확인함: FriendService.HandlePhoneUpdate)
//   - 본문이 비면 삭제, 아니면 PhoneRules.Normalize로 숫자만 남겨 검사한다(010·011·016~019, 10~11자리, +82 허용). 형식이 틀리면 success=false 응답.
//   - 다른 계정이 이미 쓰는 번호는 DB의 UNIQUE 제약이 막는다("먼저 조회 후 저장"이 아니라 저장을 시도하고 실패를 확인하므로, 동시에 두 명이 같은 번호를 저장해도 한쪽만 성공한다).
//   - 로그에는 "등록/삭제"만 남고 번호 자체는 남기지 않는다. 내 번호는 본인에게만 돌려주고, 남이 번호로 나를 찾았을 때도 가려진 값(010-****-5678)만 보인다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 정규화(Normalize)와 표시 형식(Format/Mask) ..... "phone number normalization E.164 masking"
//   - DB UNIQUE 제약으로 중복 방지(경쟁 상태 방지) .... "unique constraint race condition check then insert"
//   - 개인정보 최소 노출(번호는 찾는 용도로만) ........ "privacy by design masking minimal disclosure"
//   - 한 패킷으로 저장/삭제를 구분(빈 값 = 삭제) ...... "empty value means delete API design tradeoff"
//   - 같은 응답 패킷으로 조회/저장 결과를 모두 받기 .... "single response type for read and write"
//
// ■ 면접 예상 질문과 답
//   Q1. 같은 번호를 두 사람이 동시에 등록하면요?
//       A. "조회해서 없으면 저장" 방식은 두 요청이 동시에 통과할 수 있습니다. 그래서 DB의 UNIQUE 제약에 맡기고, 저장이 실패하면 "다른 계정에 이미 등록된 번호"로 응답합니다. 검사와 저장이 한 번에 원자적으로 일어납니다.
//   Q2. 번호를 평문으로 저장·표시해도 되나요?
//       A. 번호는 본인에게만 보이고, 다른 사람이 번호로 찾았을 때는 가운데를 가린 값만 보입니다. 로그에도 번호는 남기지 않습니다. 더 강하게 하려면 해시로 저장해 "찾기"만 가능하게 할 수 있지만(그러면 본인에게 번호를 보여 줄 수 없음) 이 프로젝트는 본인 확인 표시를 택했습니다.
//   Q3. 화면에서도 검사하고 서버에서도 검사하는 이유는요?
//       A. 화면 검사는 빠른 안내용이고, 서버가 같은 규칙(Common의 PhoneRules)으로 다시 검사해 직접 요청을 보내는 경우까지 막습니다. 규칙을 Common에 한 번만 두어 화면과 서버가 어긋나지 않게 했습니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - 저장 응답이 오기 전에 창을 닫으면 결과를 화면에서 못 보지만, 서버에는 이미 저장된다(구독은 FormClosed에서 해제되어 응답은 버려진다).
//   - 응답이 오지 않으면 두 버튼이 계속 꺼져 있다(창을 닫았다 열면 해결).
//   - 서버가 돌려준 번호를 PhoneRules.Format으로 "010-1234-5678" 모양으로 입력칸에 채우므로, 사용자가 다른 모양으로 입력했어도 저장 후에는 통일되어 보인다.
// ───────────────────────────────────────────────────────────────────────────

/// <summary>내 휴대폰 번호 설정 창.</summary>
internal sealed class PhoneForm : Form
{
    private readonly ChatClient _client;
    // [읽는 법] MaxLength 20: "+82 10-1234-5678"처럼 국가번호·하이픈이 들어와도 서버의 정규화가 처리하므로 여유 있게 허용한다.
    private readonly TextBox _phone = new() { MaxLength = 20, PlaceholderText = "010-1234-5678", Name = "MyPhone" };
    private readonly Button _save = UiHelper.CreateButton("저장", primary: true);
    private readonly Button _clear = UiHelper.CreateButton("번호 지우기");
    private readonly Label _status = new() { AutoSize = true, ForeColor = UiHelper.Muted, Margin = new Padding(0, 8, 0, 0) };

    /// <summary>내 휴대폰 번호 설정 창을 만든다. client로 번호 저장·삭제를 서버에 요청한다.</summary>
    public PhoneForm(ChatClient client)
    {
        _client = client;
        Text = $"{Theme.AppName} - 내 휴대폰 번호";
        Font = UiHelper.BaseFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        var table = UiHelper.CreateFormTable();
        UiHelper.AddSpanRow(table, new Label
        {
            AutoSize = true,
            ForeColor = UiHelper.Muted,
            Margin = new Padding(0, 0, 0, 10),
            Text = "등록하면 동료가 [친구 추가 → 휴대폰 번호]로 나를 찾을 수 있습니다.\n번호는 다른 사람에게 보이지 않습니다. (검색 결과에도 010-****-5678처럼 가려짐)",
        });
        UiHelper.AddFieldRow(table, "휴대폰 번호", _phone, 200);
        UiHelper.AddSpanRow(table, _status);
        var close = UiHelper.CreateButton("닫기");
        close.Click += (_, _) => Close();
        UiHelper.AddSpanRow(table, UiHelper.CreateButtonRow(close, _clear, _save));
        UiHelper.AddDialogHeader(this, table, "내 휴대폰 번호");
        Controls.Add(table);
        AcceptButton = _save;
        CancelButton = close;

        // [읽는 법] 화면에서는 "형식이 맞는지"만 보고, 입력 원문을 그대로 서버에 보낸다. 숫자만 남기는 정리(Normalize)는 서버도 직접 하므로 클라이언트 가공값을 믿지 않는다.
        _save.Click += (_, _) =>
        {
            if (PhoneRules.Normalize(_phone.Text) is null)
            {
                ShowStatus("휴대폰 번호 형식이 올바르지 않습니다. (예: 010-1234-5678)", error: true);
                return;
            }
            Send(_phone.Text.Trim());
        };
        // [읽는 법] 삭제는 되돌릴 수 없고(다시 등록해야 함) 다른 사람이 나를 번호로 못 찾게 되므로, 확인을 한 번 받는다.
        _clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "휴대폰 번호를 지울까요?\n지우면 다른 사람이 번호로 나를 찾을 수 없습니다.", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Send("");
        };

        _client.PacketReceived += OnPacketReceived;
        FormClosed += (_, _) => _client.PacketReceived -= OnPacketReceived;
        Shown += (_, _) =>
        {
            ShowStatus("지금 등록된 번호를 불러오는 중...", error: false);
            if (!_client.Send(Packet.Create(PacketType.PhoneInfoReq))) ShowStatus("서버와 연결이 끊어졌습니다.", error: true);
        };
    }

    // [왜] 저장과 삭제가 같은 PhoneUpdate 패킷을 쓴다(본문이 "" 이면 삭제). 패킷 종류가 늘지 않는 대신, "빈 값 = 삭제"라는 약속을 서버와 클라이언트가 함께 알아야 한다.
    private void Send(string phone)
    {
        if (!_client.Send(Packet.Create(PacketType.PhoneUpdate, body: phone)))
        {
            ShowStatus("서버와 연결이 끊어졌습니다.", error: true);
            return;
        }
        // [개념] C#의 연쇄 대입: 오른쪽부터 평가되어 두 버튼 모두 false가 된다. 응답을 받는 OnPacketReceived에서 다시 켠다(번호가 없으면 지우기 버튼만 계속 꺼 둠).
        _save.Enabled = _clear.Enabled = false;
        ShowStatus("저장하는 중...", error: false);
    }

    // [스레드] 수신 스레드 → UI 스레드
    private void OnPacketReceived(Packet packet)
    {
        if (packet.Type != PacketType.PhoneInfo) return;
        UiHelper.RunOnUi(this, () =>
        {
            _save.Enabled = _clear.Enabled = true;
            if (!packet.Success)
            {
                ShowStatus(packet.Body ?? "저장하지 못했습니다.", error: true);
                return;
            }
            // [읽는 법] PhoneInfo 응답 하나로 ① 처음 조회 ② 저장 결과 ③ 삭제 결과를 모두 받는다. 서버가 "지금 등록된 번호"를 항상 돌려주므로, 화면은 받은 값으로 입력칸을 다시 채우기만 하면 된다.
            string phone = packet.GetData<PhoneInfo>()?.Phone ?? "";
            _phone.Text = phone;
            _clear.Enabled = phone.Length > 0;
            ShowStatus(packet.Body ?? (phone.Length > 0 ? "등록된 번호입니다." : "아직 등록한 번호가 없습니다."), error: false);
        });
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? UiHelper.Danger : UiHelper.Muted;
    }
}
