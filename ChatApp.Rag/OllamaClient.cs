using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatApp.Rag;

/// <summary>
/// 로컬 Ollama(http://localhost:11434)를 부르는 클라이언트. 임베딩과 대화 생성을 모두 맡는다.
/// 외부로 나가는 통신이 없어 대화 내용이 PC 밖으로 나가지 않는다(온프레미스).
/// </summary>
public sealed class OllamaClient : IEmbedder, IChatModel
{
    private readonly HttpClient _http;

    public string EmbedModel { get; }
    public string ChatModel { get; }

    /// <summary>임베딩 모델마다 "질문용/문서용" 접두어가 다르다(예: embeddinggemma). 모델에 맞게 바꿔 쓴다.</summary>
    public string QueryPrefix { get; init; } = "task: search result | query: ";
    public string DocumentPrefix { get; init; } = "title: none | text: ";

    /// <summary>
    /// false(기본)면 GPU를 쓰지 않고 CPU로만 돌린다(num_gpu = 0).
    /// [왜] VRAM 2GB급 구형 GPU(GTX 1050 등)는 CUDA 오류로 Ollama가 죽는 일이 있고, 이득도 거의 없다.
    ///      GPU가 충분한 PC에서는 true로 바꾸면 된다.
    /// </summary>
    public bool UseGpu { get; init; }

    public OllamaClient(string embedModel, string chatModel, string baseUrl = "http://localhost:11434", HttpClient? http = null)
    {
        EmbedModel = embedModel;
        ChatModel = chatModel;
        // CPU 추론은 느릴 수 있어 제한 시간을 넉넉히 둔다(그래도 무한 대기는 하지 않는다)
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.BaseAddress ??= new Uri(baseUrl);
    }

    public async Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        => await EmbedAsync(texts.Select(t => DocumentPrefix + t).ToList(), ct);

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
        => (await EmbedAsync([QueryPrefix + text], ct))[0];

    private async Task<float[][]> EmbedAsync(List<string> inputs, CancellationToken ct)
    {
        var result = new List<float[]>(inputs.Count);
        // 한 번에 너무 많이 보내면 메모리가 튀므로 묶음 단위로 나눠 보낸다
        foreach (string[] batch in inputs.Chunk(16))
        {
            var request = new JsonObject
            {
                ["model"] = EmbedModel,
                ["input"] = new JsonArray(batch.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["options"] = GpuOptions(new JsonObject()),
            };
            using HttpResponseMessage res = await _http.PostAsJsonAsync("/api/embed", request, ct);
            await EnsureOk(res, ct);
            var body = await res.Content.ReadFromJsonAsync<EmbedResponse>(ct)
                       ?? throw new InvalidOperationException("Ollama 임베딩 응답이 비어 있습니다.");
            if (body.Embeddings is null || body.Embeddings.Length != batch.Length)
                throw new InvalidOperationException($"Ollama 임베딩 개수가 다릅니다(요청 {batch.Length}, 응답 {body.Embeddings?.Length ?? 0}).");
            result.AddRange(body.Embeddings);
        }
        return result.ToArray();
    }

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["model"] = ChatModel,
            ["stream"] = false,
            // 생각(thinking) 출력은 끈다: CPU에서는 시간이 몇 배 걸리고, 답변에 섞여 나오기도 한다
            ["think"] = false,
            // 같은 질문에는 같은 답이 나오도록(평가·시연 재현성) 온도를 0으로
            ["options"] = GpuOptions(new JsonObject { ["temperature"] = 0, ["num_ctx"] = 4096 }),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        using HttpResponseMessage res = await _http.PostAsJsonAsync("/api/chat", request, ct);
        await EnsureOk(res, ct);
        var body = await res.Content.ReadFromJsonAsync<ChatResponse>(ct);
        return body?.Message?.Content?.Trim() ?? "";
    }

    private JsonObject GpuOptions(JsonObject options)
    {
        if (!UseGpu) options["num_gpu"] = 0;
        return options;
    }

    private static async Task EnsureOk(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        string detail = await res.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Ollama 오류 {(int)res.StatusCode}: {detail}");
    }

    private sealed class EmbedResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("embeddings")] public float[][]? Embeddings { get; set; }
    }

    private sealed class ChatResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }

    private sealed class ChatMessage
    {
        [System.Text.Json.Serialization.JsonPropertyName("content")] public string? Content { get; set; }
    }
}
