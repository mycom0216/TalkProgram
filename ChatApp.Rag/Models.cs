namespace ChatApp.Rag;

/// <summary>검색 대상이 되는 대화 한 줄. ConversationKey는 "room:3" 또는 "dm:사번A|사번B"처럼 "누가 볼 수 있는가"의 단위다.</summary>
public sealed record ChatLine(long MessageId, string ConversationKey, string Sender, string Text, DateTime Time);

/// <summary>같은 대화에서 시간상 이어진 메시지 몇 줄을 묶은 검색 단위.</summary>
public sealed record Chunk(string Id, string ConversationKey, IReadOnlyList<long> MessageIds, string Text, DateTime Start, DateTime End);

/// <summary>검색 결과 한 건. Score는 검색 방법마다 의미가 달라(BM25 점수 / 코사인 / RRF) 비교용이 아니다. Cosine은 질문과의 의미 유사도(벡터를 쓰지 않은 검색이면 0).</summary>
public sealed record Hit(Chunk Chunk, double Score, int Rank, double Cosine);

/// <summary>답변 + 근거. Sources가 비어 있으면 "근거를 찾지 못했다"는 뜻이고 LLM은 부르지 않았다.</summary>
public sealed record RagAnswer(string Text, IReadOnlyList<Hit> Sources, bool Grounded);

/// <summary>텍스트를 벡터로 바꾸는 모델 (테스트에서는 가짜로 바꿔 끼운다).</summary>
public interface IEmbedder
{
    Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
    Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default);
}

/// <summary>질문 + 근거를 받아 답을 만드는 언어 모델.</summary>
public interface IChatModel
{
    Task<string> CompleteAsync(string system, string user, CancellationToken ct = default);
}
