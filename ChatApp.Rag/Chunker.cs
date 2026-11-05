using System.Text;

namespace ChatApp.Rag;

/// <summary>
/// 대화를 검색 단위(청크)로 묶는다.
/// [왜] 채팅 한 줄("네 알겠습니다")은 혼자서는 뜻이 없다. 같은 방에서 시간상 가까운 줄을 묶어야 맥락이 살아난다.
/// 규칙: 같은 대화(ConversationKey) 안에서 ① 앞 메시지와 gap 이상 떨어지면 새 청크 ② 글자 수가 maxChars를 넘기면 새 청크.
/// 새 청크는 직전 청크의 마지막 overlap줄을 앞에 겹쳐 시작한다(경계에 걸친 질문의 답을 놓치지 않으려고).
/// </summary>
public static class Chunker
{
    public static List<Chunk> Build(IEnumerable<ChatLine> lines, int maxChars = 500, TimeSpan? gap = null, int overlap = 1)
    {
        TimeSpan maxGap = gap ?? TimeSpan.FromMinutes(30);
        var chunks = new List<Chunk>();

        foreach (var conv in lines.GroupBy(l => l.ConversationKey))
        {
            var current = new List<ChatLine>();
            int chars = 0;

            void Flush()
            {
                if (current.Count == 0) return;
                chunks.Add(Make(conv.Key, current));
            }

            foreach (ChatLine line in conv.OrderBy(l => l.Time).ThenBy(l => l.MessageId))
            {
                bool tooFar = current.Count > 0 && line.Time - current[^1].Time > maxGap;
                bool tooLong = current.Count > 0 && chars + line.Text.Length > maxChars;
                if (tooFar || tooLong)
                {
                    Flush();
                    // 시간 간격 때문에 끊었으면 겹치지 않는다(다른 주제일 가능성이 큼). 길이 때문에 끊었을 때만 겹친다.
                    List<ChatLine> carry = tooFar ? [] : current.TakeLast(overlap).ToList();
                    current = carry;
                    chars = carry.Sum(c => c.Text.Length);
                }
                current.Add(line);
                chars += line.Text.Length;
            }
            // 마지막 청크가 "겹친 줄만"이면 이미 앞 청크에 들어 있으므로 버린다
            if (current.Count > 0) Flush();
        }
        return chunks;
    }

    private static Chunk Make(string key, List<ChatLine> lines)
    {
        var sb = new StringBuilder();
        foreach (ChatLine l in lines)
            sb.Append('[').Append(l.Time.ToString("yyyy-MM-dd HH:mm")).Append("] ").Append(l.Sender).Append(": ").AppendLine(l.Text);
        string id = $"{key}#{lines[0].MessageId}-{lines[^1].MessageId}";
        return new Chunk(id, key, lines.Select(l => l.MessageId).ToList(), sb.ToString().TrimEnd(), lines[0].Time, lines[^1].Time);
    }
}
