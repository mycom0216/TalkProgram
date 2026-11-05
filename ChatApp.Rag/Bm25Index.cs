namespace ChatApp.Rag;

/// <summary>
/// 한국어 채팅용 토큰 나누기. 형태소 분석기 없이 "단어 + 글자 2개씩(bigram)"으로 나눈다.
/// [왜] 한국어는 조사가 붙어("배포는", "배포를") 단어 단위로만 비교하면 못 찾는다.
///      글자 bigram("배포")은 조사와 상관없이 맞는다. 외부 라이브러리 없이 가벼워서 CPU만 있는 PC에 맞다.
/// </summary>
public static class Tokenizer
{
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
            int start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
            string word = text[start..i].ToLowerInvariant();

            tokens.Add(word);
            // 두 글자 이상이면 bigram도 넣는다. 영문·숫자 단어(예: v2.3, API)는 그대로 한 토큰이면 충분하다.
            if (word.Length >= 2 && word.Any(IsHangulOrCjk))
                for (int k = 0; k + 1 < word.Length; k++)
                    tokens.Add(word.Substring(k, 2));
        }
        return tokens;
    }

    private static bool IsHangulOrCjk(char c) => c is >= '가' and <= '힣' or >= '一' and <= '鿿';
}

/// <summary>BM25 (k1=1.2, b=0.75). 문서는 청크 목록의 위치(index)로 가리킨다.</summary>
public sealed class Bm25Index
{
    private const double K1 = 1.2, B = 0.75;

    private readonly List<Dictionary<string, int>> _termFreq = [];
    private readonly List<int> _length = [];
    private readonly Dictionary<string, int> _docFreq = [];
    private double _avgLength;

    public int Count => _length.Count;

    public Bm25Index(IEnumerable<string> documents)
    {
        foreach (string doc in documents)
        {
            var tf = new Dictionary<string, int>();
            List<string> tokens = Tokenizer.Tokenize(doc);
            foreach (string t in tokens) tf[t] = tf.GetValueOrDefault(t) + 1;
            foreach (string t in tf.Keys) _docFreq[t] = _docFreq.GetValueOrDefault(t) + 1;
            _termFreq.Add(tf);
            _length.Add(tokens.Count);
        }
        _avgLength = _length.Count == 0 ? 0 : _length.Average();
    }

    /// <summary>질문과 각 문서의 BM25 점수. 겹치는 토큰이 없으면 0.</summary>
    public double[] Score(string query)
    {
        var scores = new double[_length.Count];
        // 질문 안의 같은 토큰이 여러 번 나와도 한 번만 센다
        foreach (string term in Tokenizer.Tokenize(query).Distinct())
        {
            if (!_docFreq.TryGetValue(term, out int df)) continue;
            double idf = Math.Log(1 + (_length.Count - df + 0.5) / (df + 0.5));
            for (int d = 0; d < scores.Length; d++)
            {
                if (!_termFreq[d].TryGetValue(term, out int f)) continue;
                double norm = f * (K1 + 1) / (f + K1 * (1 - B + B * _length[d] / _avgLength));
                scores[d] += idf * norm;
            }
        }
        return scores;
    }
}
