using System.Security.Cryptography;
using System.Text;

namespace ChatApp.Rag;

/// <summary>
/// 문장 → 벡터 결과를 글 내용의 해시로 보관한다. 서버를 다시 켜도, 대화가 늘어도 "새 청크만" 임베딩하면 된다.
/// [왜] CPU만으로 임베딩하면 청크 1개에 수십~수백 ms가 든다. 안 바뀐 청크를 다시 계산하지 않는 것이 CPU 환경의 핵심 최적화다.
/// 파일 형식(이진): 개수(int) → [해시(문자열), 차원(int), float × 차원] × 개수
/// </summary>
public sealed class EmbeddingCache
{
    private readonly Dictionary<string, float[]> _items = [];
    private readonly string? _path;
    private readonly object _lock = new();

    public EmbeddingCache(string? path = null)
    {
        _path = path;
        if (path is not null && File.Exists(path)) Load(path);
    }

    public int Count { get { lock (_lock) return _items.Count; } }

    public static string KeyOf(string model, string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model + "\n" + text)));

    public bool TryGet(string key, out float[] vector)
    {
        lock (_lock) return _items.TryGetValue(key, out vector!);
    }

    public void Set(string key, float[] vector)
    {
        lock (_lock) _items[key] = vector;
    }

    public void Save()
    {
        if (_path is null) return;
        string tmp = _path + ".tmp";
        lock (_lock)
        {
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(_items.Count);
                foreach (var (key, vec) in _items)
                {
                    w.Write(key);
                    w.Write(vec.Length);
                    foreach (float f in vec) w.Write(f);
                }
            }
        }
        // 쓰다가 죽어도 기존 캐시가 깨지지 않도록 임시 파일에 쓴 뒤 바꿔치기한다
        File.Move(tmp, _path, overwrite: true);
    }

    private void Load(string path)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string key = r.ReadString();
                var vec = new float[r.ReadInt32()];
                for (int k = 0; k < vec.Length; k++) vec[k] = r.ReadSingle();
                _items[key] = vec;
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
        {
            // 캐시는 "있으면 좋은 것"이다. 깨졌으면 비우고 다시 만들면 된다.
            _items.Clear();
        }
    }
}
