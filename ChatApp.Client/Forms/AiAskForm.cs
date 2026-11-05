using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] AiAskForm.cs - "AI 비서" 창: 내 대화 기록에 대해 한국어로 질문한다.
// - 질문을 서버(AiAskReq)에 보내면 서버의 로컬 LLM이 "내가 볼 수 있는 대화"만 근거로 답(AiAskRes)을 보낸다.
// - 답 아래에 근거로 쓴 대화 조각(방 이름, 시각, 원문)을 함께 보여 줘서 사용자가 직접 확인할 수 있다.
// - CPU로 추론하므로 답이 오기까지 수십 초 걸릴 수 있다. 기다리는 동안 버튼을 끄고 경과 시간을 보여 준다.
// - 스레드: 화면은 UI 스레드. 서버 응답은 수신 스레드에서 와서 RunOnUi로 넘긴다.
// - 서버가 실패를 알릴 때도 AiAskRes(Success = false)로 보내므로, 다른 기능의 Error 패킷과 섞이지 않는다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>AI 비서 창. 질문 → 답변 + 근거 대화.</summary>
public sealed class AiAskForm : Form
{
    private readonly ChatClient _client;
    private readonly TextBox _question = new() { Dock = DockStyle.Fill, MaxLength = AiAskRequest.MaxLength, PlaceholderText = "예) 지난주 배포 일정이 어떻게 정해졌지?" };
    private readonly Button _askButton = UiHelper.CreateButton("질문", primary: true);
    private readonly RichTextBox _answer = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, DetectUrls = false,
    };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = UiHelper.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private DateTime _askedAt;
    private bool _waiting;

    public AiAskForm(ChatClient client)
    {
        _client = client;
        Text = $"{Theme.AppName} - AI 비서";
        Font = UiHelper.BaseFont;
        StartPosition = FormStartPosition.CenterScreen;
        Size = LogicalToDeviceUnits(new Size(760, 600));
        MinimumSize = LogicalToDeviceUnits(new Size(480, 360));
        BackColor = Color.White;

        _askButton.Dock = DockStyle.Right;
        _askButton.Click += (_, _) => Ask();
        var top = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(34), Padding = new Padding(0, 0, 0, LogicalToDeviceUnits(6)) };
        top.Controls.Add(_question);
        top.Controls.Add(_askButton);
        _question.BringToFront();   // Dock: 오른쪽 버튼이 먼저 자리 잡고, 입력칸이 남은 자리를 채운다

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(LogicalToDeviceUnits(10)) };
        body.Controls.Add(_answer);
        body.Controls.Add(top);
        body.Controls.Add(_status);
        _answer.BringToFront();
        Controls.Add(body);
        AcceptButton = _askButton;

        _answer.Text = "내가 참여한 방과 1:1 대화 기록을 근거로 답합니다. 남의 대화는 볼 수 없습니다.\r\n"
                     + "답은 서버 PC의 AI가 만들며 대화 내용은 외부로 나가지 않습니다. 틀릴 수 있으니 아래 근거 대화를 꼭 확인하세요.";
        _status.Text = "질문을 입력하고 Enter를 누르세요.";

        _timer.Tick += (_, _) => _status.Text = $"답을 만드는 중... {(int)(DateTime.UtcNow - _askedAt).TotalSeconds}초 (CPU로 돌려 수십 초 걸릴 수 있습니다)";
        _client.PacketReceived += OnPacketReceived;
        FormClosed += (_, _) => { _client.PacketReceived -= OnPacketReceived; _timer.Dispose(); };
        Shown += (_, _) => _question.Focus();
    }

    private void Ask()
    {
        string question = _question.Text.Trim();
        if (_waiting) return;
        if (question.Length < AiAskRequest.MinLength)
        {
            _status.Text = $"질문을 {AiAskRequest.MinLength}자 이상 입력하세요.";
            return;
        }
        if (!_client.Send(Packet.Create(PacketType.AiAskReq).WithData(new AiAskRequest { Question = question })))
        {
            _status.Text = "서버와 연결이 끊어져 질문할 수 없습니다.";
            return;
        }
        _waiting = true;
        _askButton.Enabled = false;
        _askedAt = DateTime.UtcNow;
        _timer.Start();
        _answer.Text = $"Q. {question}\r\n\r\n";
        _status.Text = "답을 만드는 중...";
    }

    // [스레드] 수신 스레드 → UI 스레드
    private void OnPacketReceived(Packet packet)
    {
        if (packet.Type != PacketType.AiAskRes) return;
        UiHelper.RunOnUi(this, () =>
        {
            if (!_waiting) return;   // 이 창이 보낸 질문의 답이 아니면 무시
            _waiting = false;
            _timer.Stop();
            _askButton.Enabled = true;

            if (!packet.Success || packet.GetData<AiAnswer>() is not { } answer)
            {
                _answer.AppendText($"⚠ {packet.Body ?? "답을 받지 못했습니다."}");
                _status.Text = "실패했습니다. 잠시 후 다시 시도하세요.";
                return;
            }
            ShowAnswer(answer);
        });
    }

    private void ShowAnswer(AiAnswer answer)
    {
        _answer.Clear();
        Append($"Q. {answer.Question}\r\n\r\n", bold: true);
        Append($"{answer.Text}\r\n", bold: false);
        if (answer.Sources.Count > 0)
        {
            Append("\r\n── 근거 대화 ──\r\n", bold: true);
            foreach (AiSource source in answer.Sources)
            {
                Append($"\r\n# {source.Title}  ({source.Time.ToLocalTime():yyyy-MM-dd HH:mm})\r\n", bold: true);
                Append(source.Excerpt + "\r\n", bold: false);
            }
        }
        _answer.SelectionStart = 0;
        _answer.ScrollToCaret();
        _status.Text = answer.Grounded
            ? $"{answer.ElapsedSeconds:F1}초 · 근거 {answer.Sources.Count}건 · AI 답변은 틀릴 수 있으니 근거를 확인하세요."
            : $"{answer.ElapsedSeconds:F1}초 · 대화 기록에서 근거를 찾지 못했습니다.";
    }

    private void Append(string text, bool bold)
    {
        _answer.SelectionStart = _answer.TextLength;
        _answer.SelectionFont = new Font(_answer.Font, bold ? FontStyle.Bold : FontStyle.Regular);
        _answer.AppendText(text);
    }
}
