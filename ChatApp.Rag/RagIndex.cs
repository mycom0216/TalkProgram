namespace ChatApp.Rag;

public enum SearchMode { Bm25, Vector, Hybrid }

/// <summary>
/// 청크 목록 위에 만든 검색 색인 (BM25 + 벡터). 한 번 만들면 읽기 전용이라 여러 스레드가 동시에 검색해도 안전하다.
/// 가장 중요한 규칙: <b>검색은 항상 "이 사용자가 볼 수 있는 대화"(allowed) 안에서만</b> 한다.
/// 권한 필터를 순위 계산 전에 적용하므로, 못 보는 대화가 결과·점수·답변 근거 어디에도 섞이지 않는다.
/// </summary>
public sealed class RagIndex
{
    private const int RrfK = 60;       // RRF 상수(논문 기본값). 클수록 순위 차이의 영향이 작다
    private const int PoolSize = 30;   // 방법별 후보 수. 합치기 전에 각 방법에서 이만큼만 가져온다

    private readonly IReadOnlyList<Chunk> _chunks;
    private readonly float[][]? _vectors;   // 정규화된 벡터 (벡터 없이 BM25만 쓰는 색인이면 null)
    private readonly Bm25Index _bm25;
    private readonly IEmbedder? _embedder;

    public int ChunkCount => _chunks.Count;
    public IReadOnlyList<Chunk> Chunks => _chunks;

    private RagIndex(IReadOnlyList<Chunk> chunks, float[][]? vectors, IEmbedder? embedder)
    {
        _chunks = chunks;
        _vectors = vectors;
        _embedder = embedder;
        _bm25 = new Bm25Index(chunks.Select(c => c.Text));
    }

    /// <summary>임베딩 없이 BM25만 쓰는 색인 (Ollama가 꺼져 있어도 동작하는 대체 경로).</summary>
    public static RagIndex BuildKeywordOnly(IReadOnlyList<Chunk> chunks) => new(chunks, null, null);

    /// <summary>
    /// 청크마다 임베딩해 색인을 만든다. cache에 이미 있는 글은 다시 계산하지 않는다.
    /// </summary>
    public static async Task<RagIndex> BuildAsync(IReadOnlyList<Chunk> chunks, IEmbedder embedder, string modelName,
                                                  EmbeddingCache? cache = null, CancellationToken ct = default)
    {
        var vectors = new float[chunks.Count][];
        var missing = new List<int>();
        for (int i = 0; i < chunks.Count; i++)
        {
            if (cache is not null && cache.TryGet(EmbeddingCache.KeyOf(modelName, chunks[i].Text), out float[] v)) vectors[i] = v;
            else missing.Add(i);
        }

        if (missing.Count > 0)
        {
            float[][] fresh = await embedder.EmbedDocumentsAsync(missing.Select(i => chunks[i].Text).ToList(), ct);
            for (int n = 0; n < missing.Count; n++)
            {
                int i = missing[n];
                vectors[i] = Normalize(fresh[n]);
                cache?.Set(EmbeddingCache.KeyOf(modelName, chunks[i].Text), vectors[i]);
            }
            cache?.Save();
        }
        return new RagIndex(chunks, vectors, embedder);
    }

    public async Task<List<Hit>> SearchAsync(string query, IReadOnlySet<string> allowed, int k, SearchMode mode = SearchMode.Hybrid,
                                             CancellationToken ct = default)
    {
        if (mode != SearchMode.Bm25 && _vectors is null) mode = SearchMode.Bm25; // 벡터가 없으면 키워드 검색으로 대체

        // ① 권한 필터: 볼 수 있는 청크의 위치만 남긴다. 이후 모든 순위는 이 안에서만 계산한다.
        var visible = new List<int>();
        for (int i = 0; i < _chunks.Count; i++)
            if (allowed.Contains(_chunks[i].ConversationKey)) visible.Add(i);
        if (visible.Count == 0 || string.IsNullOrWhiteSpace(query)) return [];

        // ② 방법별 순위
        List<(int Index, double Score)> bm25Ranked = [], vectorRanked = [];
        float[]? q = null;
        if (mode != SearchMode.Vector)
        {
            double[] scores = _bm25.Score(query);
            bm25Ranked = visible.Where(i => scores[i] > 0).Select(i => (i, scores[i])).OrderByDescending(x => x.Item2).Take(PoolSize).ToList();
        }
        if (mode != SearchMode.Bm25)
        {
            q = Normalize(await _embedder!.EmbedQueryAsync(query, ct));
            vectorRanked = visible.Select(i => (i, (double)Dot(q, _vectors![i]))).OrderByDescending(x => x.Item2).Take(PoolSize).ToList();
        }

        // ③ 합치기
        IEnumerable<(int Index, double Score)> merged = mode switch
        {
            SearchMode.Bm25 => bm25Ranked,
            SearchMode.Vector => vectorRanked,
            _ => Rrf(bm25Ranked, vectorRanked),
        };

        var hits = new List<Hit>();
        int rank = 1;
        foreach (var (index, score) in merged.Take(k))
            hits.Add(new Hit(_chunks[index], score, rank++, q is null ? 0 : Dot(q, _vectors![index])));
        return hits;
    }

    /// <summary>Reciprocal Rank Fusion: 점수 크기가 서로 다른 두 순위표를 "순위"만으로 합친다(BM25 점수와 코사인은 단위가 달라 더할 수 없다).</summary>
    private static IEnumerable<(int Index, double Score)> Rrf(List<(int Index, double Score)> a, List<(int Index, double Score)> b)
    {
        var fused = new Dictionary<int, double>();
        foreach (var list in new[] { a, b })
            for (int r = 0; r < list.Count; r++)
                fused[list[r].Index] = fused.GetValueOrDefault(list[r].Index) + 1.0 / (RrfK + r + 1);
        // 점수가 같으면 위치 순으로 정해 매번 같은 결과가 나오게 한다
        return fused.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => (x.Key, x.Value));
    }

    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (float f in v) sum += (double)f * f;
        double norm = Math.Sqrt(sum);
        if (norm == 0) return v;
        var result = new float[v.Length];
        for (int i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
        return result;
    }

    private static float Dot(float[] a, float[] b)
    {
        float s = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }
}
