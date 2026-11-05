using ChatApp.Common.Models;
using ChatApp.Rag;
using ChatApp.Server;
using ChatApp.Server.Data;
using ChatApp.Server.Services;

namespace ChatApp.Tests;

// AI 비서(RAG) 테스트. Ollama·DB 없이 돌아가도록 가짜 임베딩·가짜 LLM을 쓴다.
// 가장 중요한 것은 "권한 필터": 못 보는 대화가 검색·프롬프트·출처 어디에도 섞이면 안 된다.
public class RagTests
{
    // ── 가짜 부품 ──────────────────────────────────────────

    /// <summary>글자 bigram을 64칸 벡터에 해시로 세어 담는 가짜 임베딩 (같은 글자가 많이 겹치면 가깝다). 호출 횟수를 센다.</summary>
    private sealed class FakeEmbedder : IEmbedder
    {
        public int DocumentCalls;

        public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            DocumentCalls += texts.Count;
            return Task.FromResult(texts.Select(Embed).ToArray());
        }

        public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default) => Task.FromResult(Embed(text));

        private static float[] Embed(string text)
        {
            var v = new float[64];
            foreach (string token in Tokenizer.Tokenize(text)) v[(token.GetHashCode() & 0x7fffffff) % 64] += 1;
            return v;
        }
    }

    /// <summary>검증 단계(예/아니오)와 답변 생성 호출을 구분해 세는 가짜 LLM. Calls는 "답변 생성" 호출 수다.</summary>
    private sealed class FakeChat(string reply, string verdict = "예") : IChatModel
    {
        public int Calls;
        public int VerifyCalls;
        public string? LastUser;

        public Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
        {
            if (system == RagAssistant.VerifierPrompt)
            {
                VerifyCalls++;
                return Task.FromResult(verdict);
            }
            Calls++;
            LastUser = user;
            return Task.FromResult(reply);
        }
    }

    private static readonly DateTime T0 = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static ChatLine Line(long id, string conv, string text, int minutes = 0, string sender = "홍길동")
        => new(id, conv, sender, text, T0.AddMinutes(minutes));

    private static async Task<RagIndex> BuildAsync(IEnumerable<ChatLine> lines, FakeEmbedder? embedder = null, EmbeddingCache? cache = null, int maxChars = 500)
        => await RagIndex.BuildAsync(Chunker.Build(lines, maxChars), embedder ?? new FakeEmbedder(), "fake", cache);

    // ── 청킹 ──────────────────────────────────────────────

    [Fact]
    public void Chunker_SplitsWhenTimeGapIsLarge_AndNeverMixesConversations()
    {
        var chunks = Chunker.Build(
        [
            Line(1, "room:1", "배포 일정 공유", 0), Line(2, "room:1", "확인했습니다", 5),
            Line(3, "room:1", "점심 뭐 먹지", 120),            // 2시간 뒤 → 새 청크
            Line(4, "room:2", "다른 방 이야기", 1),
        ]);

        Assert.Equal(3, chunks.Count);
        Assert.Contains(chunks, c => c.ConversationKey == "room:1" && c.MessageIds.SequenceEqual([1L, 2L]));
        Assert.Contains(chunks, c => c.ConversationKey == "room:1" && c.MessageIds.SequenceEqual([3L]));
        Assert.Contains(chunks, c => c.ConversationKey == "room:2" && c.MessageIds.SequenceEqual([4L]));
    }

    [Fact]
    public void Chunker_WhenSplitByLength_OverlapsOneLine_SoBoundaryAnswersAreNotLost()
    {
        var lines = Enumerable.Range(1, 6).Select(i => Line(i, "room:1", new string('가', 40), i)).ToList();
        var chunks = Chunker.Build(lines, maxChars: 100);   // 한 청크에 2줄(80자)까지

        Assert.True(chunks.Count >= 3);
        // 인접 청크는 한 줄을 겹친다
        for (int i = 1; i < chunks.Count; i++)
            Assert.Equal(chunks[i - 1].MessageIds[^1], chunks[i].MessageIds[0]);
        // 모든 메시지는 어느 청크에든 들어 있다
        Assert.Equal(lines.Select(l => l.MessageId).Order(), chunks.SelectMany(c => c.MessageIds).Distinct().Order());
    }

    // ── 토큰·BM25 ─────────────────────────────────────────

    [Fact]
    public void Tokenizer_KoreanParticleVariants_ShareBigrams()
    {
        // "배포는"/"배포를"은 단어로는 달라도 bigram "배포"가 같다
        Assert.Contains("배포", Tokenizer.Tokenize("배포는"));
        Assert.Contains("배포", Tokenizer.Tokenize("배포를"));
        Assert.Contains("v2", Tokenizer.Tokenize("릴리스 V2 배포"));   // 영문은 소문자 한 토큰
    }

    [Fact]
    public void Bm25_RanksDocumentWithQueryTermsFirst_AndZeroWhenNothingOverlaps()
    {
        var bm25 = new Bm25Index(["점심 메뉴는 칼국수", "서버 배포는 목요일 밤", "워크숍 장소는 대회의실"]);
        double[] s = bm25.Score("배포 언제");
        Assert.True(s[1] > s[0] && s[1] > s[2]);
        Assert.All(bm25.Score("zzz qqq"), v => Assert.Equal(0, v));
    }

    // ── 검색 + 권한 필터 (가장 중요) ──────────────────────

    [Theory]
    [InlineData(SearchMode.Bm25)]
    [InlineData(SearchMode.Vector)]
    [InlineData(SearchMode.Hybrid)]
    public async Task Search_NeverReturnsConversationsOutsideAllowedSet(SearchMode mode)
    {
        RagIndex index = await BuildAsync(
        [
            Line(1, "room:dev", "배포는 10월 29일 밤 10시입니다"),
            Line(2, "room:hr", "올해 연봉 인상률은 5.2퍼센트 확정입니다", 1),
            Line(3, "dm:E1|E2", "연봉 인상률 얘기는 비밀로 해주세요", 2),
        ]);

        // E3는 hr 방도 E1·E2의 1:1도 볼 수 없다. 질문이 비밀 내용과 글자까지 똑같아도 나오면 안 된다.
        var allowed = AiAssistantService.AllowedKeys("E3", [], index.Chunks.Select(c => c.ConversationKey));
        var hits = await index.SearchAsync("연봉 인상률", allowed, 10, mode);
        Assert.Empty(hits);

        // 대조군: 권한을 주면 같은 질문에 정답이 나온다 (필터가 아니라 검색이 비어서 통과한 것이 아님을 증명)
        var all = index.Chunks.Select(c => c.ConversationKey).ToHashSet();
        var visibleHits = await index.SearchAsync("연봉 인상률", all, 10, mode);
        Assert.NotEmpty(visibleHits);
        Assert.Contains(visibleHits, h => h.Chunk.ConversationKey == "room:hr");
    }

    [Fact]
    public async Task Search_FilterAppliesBeforeRanking_SoTopKIsFilledFromVisibleConversations()
    {
        // 못 보는 방에 질문과 더 잘 맞는 글이 있어도, 보이는 방의 글이 상위로 채워져야 한다(필터 후 순위)
        RagIndex index = await BuildAsync(
        [
            Line(1, "room:secret", "서버 점검 일정 서버 점검 일정 서버 점검 일정"),
            Line(2, "room:open", "서버 점검은 금요일 새벽입니다", 1),
        ]);
        var hits = await index.SearchAsync("서버 점검 일정", new HashSet<string> { "room:open" }, 3, SearchMode.Hybrid);
        Assert.Single(hits);
        Assert.Equal("room:open", hits[0].Chunk.ConversationKey);
    }

    [Fact]
    public async Task Search_HybridIsDeterministic_AndRanksAreSequential()
    {
        RagIndex index = await BuildAsync(Enumerable.Range(1, 12).Select(i => Line(i, "room:1", $"회의록 {i}번 안건 서버 배포 계획", i * 60)));
        var allowed = new HashSet<string> { "room:1" };
        var a = await index.SearchAsync("서버 배포", allowed, 5);
        var b = await index.SearchAsync("서버 배포", allowed, 5);

        Assert.Equal(a.Select(h => h.Chunk.Id), b.Select(h => h.Chunk.Id));
        Assert.Equal([1, 2, 3, 4, 5], a.Select(h => h.Rank));
    }

    [Fact]
    public async Task Search_KeywordOnlyIndex_WorksWithoutEmbeddings()
    {
        // Ollama가 없어도 BM25로는 동작해야 한다 (Hybrid·Vector 요청도 BM25로 대체)
        var index = RagIndex.BuildKeywordOnly(Chunker.Build([Line(1, "room:1", "스테이징 서버 주소는 stg2 입니다")]));
        var hits = await index.SearchAsync("스테이징 주소", new HashSet<string> { "room:1" }, 3, SearchMode.Hybrid);
        Assert.Single(hits);
        Assert.Equal(0, hits[0].Cosine);
    }

    [Fact]
    public async Task Search_EmptyAllowedOrEmptyQuery_ReturnsNothing()
    {
        RagIndex index = await BuildAsync([Line(1, "room:1", "아무 내용")]);
        Assert.Empty(await index.SearchAsync("아무", new HashSet<string>(), 3));
        Assert.Empty(await index.SearchAsync("   ", new HashSet<string> { "room:1" }, 3));
    }

    // ── 임베딩 캐시 ───────────────────────────────────────

    [Fact]
    public async Task Build_WithCache_DoesNotReEmbedUnchangedChunks()
    {
        var cache = new EmbeddingCache();
        var embedder = new FakeEmbedder();
        var lines = new List<ChatLine> { Line(1, "room:1", "첫 번째 대화"), Line(2, "room:2", "두 번째 대화") };

        await BuildAsync(lines, embedder, cache);
        Assert.Equal(2, embedder.DocumentCalls);

        lines.Add(Line(3, "room:3", "새로 생긴 대화"));
        await BuildAsync(lines, embedder, cache);
        Assert.Equal(3, embedder.DocumentCalls);   // 새 청크 1개만 추가로 계산
    }

    [Fact]
    public void EmbeddingCache_SaveAndLoad_RoundTrips_AndCorruptFileIsIgnored()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rag-cache-{Guid.NewGuid():N}.bin");
        try
        {
            var cache = new EmbeddingCache(path);
            cache.Set("k1", [0.5f, -1.25f, 3f]);
            cache.Save();

            Assert.True(new EmbeddingCache(path).TryGet("k1", out float[] v));
            Assert.Equal([0.5f, -1.25f, 3f], v);

            File.WriteAllBytes(path, [1, 2, 3, 4, 5]);          // 깨진 파일
            Assert.Equal(0, new EmbeddingCache(path).Count);     // 예외 없이 빈 캐시로 시작
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmbeddingCache_KeyDependsOnModelAndText()
    {
        Assert.NotEqual(EmbeddingCache.KeyOf("a", "글"), EmbeddingCache.KeyOf("b", "글"));
        Assert.NotEqual(EmbeddingCache.KeyOf("a", "글1"), EmbeddingCache.KeyOf("a", "글2"));
        Assert.Equal(EmbeddingCache.KeyOf("a", "글"), EmbeddingCache.KeyOf("a", "글"));
    }

    // ── 답변 생성 ─────────────────────────────────────────

    [Fact]
    public async Task Assistant_WhenNothingVisibleMatches_DoesNotCallLlm()
    {
        RagIndex index = await BuildAsync([Line(1, "room:hr", "비밀 연봉 정보")]);
        var chat = new FakeChat("지어낸 답");
        var assistant = new RagAssistant(index, chat);

        RagAnswer answer = await assistant.AskAsync("연봉", new HashSet<string> { "room:dev" });

        Assert.False(answer.Grounded);
        Assert.Equal(RagAssistant.NotFound, answer.Text);
        Assert.Equal(0, chat.Calls);   // 근거가 없으면 LLM을 부르지 않는다
    }

    [Fact]
    public async Task Assistant_KeepsOnlyValidCitations_AndMarksRefusalAsNotGrounded()
    {
        RagIndex index = await BuildAsync([Line(1, "room:1", "배포는 29일 밤 10시입니다")]);

        // 모델이 없는 출처 번호([9])를 지어내면 버린다
        var cited = await new RagAssistant(index, new FakeChat("29일 밤 10시입니다. [1] [9]")).AskAsync("배포 언제", new HashSet<string> { "room:1" });
        Assert.True(cited.Grounded);
        Assert.Single(cited.Sources);

        var refused = await new RagAssistant(index, new FakeChat("대화 기록에서 찾을 수 없습니다.")).AskAsync("배포 언제", new HashSet<string> { "room:1" });
        Assert.False(refused.Grounded);
    }

    [Fact]
    public async Task Assistant_MinCosineFiltersWeakEvidence()
    {
        RagIndex index = await BuildAsync([Line(1, "room:1", "배포는 29일 밤 10시입니다")]);
        var chat = new FakeChat("x");
        // 코사인이 1.01 이상일 수는 없으므로 모든 근거가 약하다고 본다
        var assistant = new RagAssistant(index, chat, new RagOptions { MinCosine = 1.01 });

        RagAnswer answer = await assistant.AskAsync("배포 언제", new HashSet<string> { "room:1" });
        Assert.False(answer.Grounded);
        Assert.Equal(0, chat.Calls);
    }

    // ── 근거 검증 단계 (환각 방지) ─────────────────────────

    [Fact]
    public async Task Assistant_WhenVerifierSaysNo_RefusesWithoutGeneratingAnAnswer()
    {
        RagIndex index = await BuildAsync([Line(1, "room:1", "점심은 칼국수를 먹었습니다")]);
        var chat = new FakeChat("지어낸 답입니다", verdict: "아니오");

        RagAnswer answer = await new RagAssistant(index, chat).AskAsync("점심 뭐 먹었어", new HashSet<string> { "room:1" });

        Assert.False(answer.Grounded);
        Assert.Equal(RagAssistant.NotFound, answer.Text);
        Assert.Equal(1, chat.VerifyCalls);
        Assert.Equal(0, chat.Calls);   // 근거에 답이 없다고 판정되면 답변을 만들지 않는다
    }

    [Fact]
    public async Task Assistant_VerificationCanBeTurnedOff()
    {
        RagIndex index = await BuildAsync([Line(1, "room:1", "점심은 칼국수를 먹었습니다")]);
        var chat = new FakeChat("칼국수입니다. [1]", verdict: "아니오");

        RagAnswer answer = await new RagAssistant(index, chat, new RagOptions { VerifyEvidence = false })
            .AskAsync("점심 뭐 먹었어", new HashSet<string> { "room:1" });

        Assert.True(answer.Grounded);
        Assert.Equal(0, chat.VerifyCalls);
    }

    [Theory]
    [InlineData("예", true)]
    [InlineData("예.", true)]
    [InlineData("  \"예\"", true)]
    [InlineData("네, 있습니다", true)]
    [InlineData("Yes", true)]
    [InlineData("아니오", false)]
    [InlineData("아니요, 없습니다", false)]
    [InlineData("예상 밖이라 알 수 없습니다", false)]   // "예"로 시작하지만 다른 단어
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("판정: 예", true)]
    [InlineData("판정: 아니오", false)]
    [InlineData("판정 : 예.", true)]
    [InlineData("232", false)]                       // 질문의 답을 그대로 말한 경우(실측된 실패 형태)
    [InlineData("판정: 예상 못함", false)]
    [InlineData("...", false)]
    public void IsYes_OnlyAcceptsAnExplicitYesAsTheFirstWord(string verdict, bool expected)
        => Assert.Equal(expected, RagAssistant.IsYes(verdict));

    // ── 서버 서비스: 대화 키와 종단 권한 ──────────────────

    [Fact]
    public void DmKey_IsSameInBothDirections()
    {
        Assert.Equal(AiAssistantService.DmKey("E1002", "E1001"), AiAssistantService.DmKey("E1001", "E1002"));
        Assert.Equal("dm:E1001|E1002", AiAssistantService.DmKey("E1002", "E1001"));
    }

    [Fact]
    public void AllowedKeys_IncludesMemberRoomsAndOwnDmsOnly_AndFailsClosedOnOddKeys()
    {
        string[] keys = ["dm:E1|E2", "dm:E2|E3", "dm:E1|E1", "dm:E1|E2|E9", "room:7", "dm:E10|E2"];
        var allowed = AiAssistantService.AllowedKeys("E1", [3, 4], keys);

        Assert.Contains("room:3", allowed);
        Assert.Contains("room:4", allowed);
        Assert.Contains("dm:E1|E2", allowed);
        Assert.Contains("dm:E1|E1", allowed);
        Assert.DoesNotContain("dm:E2|E3", allowed);        // 남의 1:1
        Assert.DoesNotContain("dm:E1|E2|E9", allowed);     // 모양이 이상한 키는 아무에게도 허용하지 않는다
        Assert.DoesNotContain("dm:E10|E2", allowed);       // "E1"이 "E10"의 앞부분이어도 다른 사람
        Assert.DoesNotContain("room:7", allowed);          // 멤버가 아닌 방
    }

    private static AiAssistantService NewService(FakeChat chat, params MessageRepository.IndexLine[] rows)
        => new(new AiConfig { Enabled = true, MinCosine = 0 }, _ => rows, id => id == 1 ? "개발팀" : null, _ => [1],
               new FakeEmbedder(), chat, "fake", new EmbeddingCache());

    private static MessageRepository.IndexLine Row(long id, int? room, string from, string? to, string name, string text)
        => new(id, room, from, to, name, text, T0.AddMinutes(id));

    [Fact]
    public async Task Service_AnswersOnlyFromRoomsAndDmsTheAskerCanSee()
    {
        var chat = new FakeChat("배포는 29일입니다. [1]");
        using AiAssistantService service = NewService(chat,
            Row(1, 1, "E1", null, "김민수", "배포 일정은 29일 밤 10시입니다"),
            Row(2, 2, "E9", null, "오세진", "연봉 인상률 비밀 5.2퍼센트"),               // 못 보는 방
            Row(3, null, "E8", "E9", "박지훈", "연봉 인상률 얘기 비밀 DM"));            // 남의 1:1

        // 질문자 E1: 방 1만 멤버
        AiAnswer answer = await service.AskAsync("E1", [1], "연봉 인상률이 몇 퍼센트야?");

        Assert.DoesNotContain("5.2", chat.LastUser ?? "");     // 비밀 내용이 프롬프트에 들어가지 않았다
        Assert.All(answer.Sources, s => Assert.DoesNotContain("5.2", s.Excerpt));
        Assert.All(answer.Sources, s => Assert.Equal("개발팀", s.Title));
    }

    [Fact]
    public async Task Service_CanSeeOwnDm_AndNamesThePeerInSourceTitle()
    {
        var chat = new FakeChat("21일 수요일입니다. [1]");
        using AiAssistantService service = NewService(chat,
            Row(1, null, "E2", "E1", "이서연", "연차는 21일 수요일로 신청하겠습니다"));

        AiAnswer answer = await service.AskAsync("E1", [], "연차 언제야");

        Assert.True(answer.Grounded);
        Assert.Contains("21일", answer.Text);
        Assert.Single(answer.Sources);
        Assert.Equal("1:1 대화 (이서연)", answer.Sources[0].Title);

        // 같은 대화를 제3자가 물으면 못 본다
        AiAnswer other = await service.AskAsync("E3", [], "연차 언제야");
        Assert.False(other.Grounded);
        Assert.Empty(other.Sources);
    }
}
