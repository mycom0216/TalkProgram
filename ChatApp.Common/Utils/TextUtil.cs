using System.Globalization;

namespace ChatApp.Common.Utils;

/// <summary>글자(이모지 포함)가 깨지지 않게 문자열 길이를 다루는 도우미.</summary>
// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] 글자 수 자르기 같은 작은 문자열 도우미.
// [왜] C#의 string.Length는 "글자 수"가 아니라 UTF-16 단위 수다. 😀 같은 이모지는 2단위(서로게이트 쌍)라서
//      text[..500]처럼 자르면 이모지 반쪽만 남아 깨진 글자(�)가 저장될 수 있다.
//      StringInfo는 사람 눈에 보이는 "글자"(텍스트 요소) 단위로 다룬다.
// ─────────────────────────────────────────────────────────────────────────────
public static class TextUtil
{
    /// <summary>최대 max개의 UTF-16 단위를 넘지 않게, 글자(이모지 포함)가 깨지지 않는 위치에서 자른다.</summary>
    /// <remarks>DB 칸(VARCHAR(500))은 UTF-16 단위가 아니라 문자 수로 세지만, 둘 다 넘지 않게 짧은 쪽 기준으로 자른다.</remarks>
    public static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        // [개념] StringInfo.ParseCombiningCharacters: 각 "글자"가 시작하는 위치 목록. max를 넘지 않는 마지막 시작점에서 자른다.
        int cut = 0;
        foreach (int start in StringInfo.ParseCombiningCharacters(text))
        {
            if (start > max) break;
            cut = start;
        }
        // cut 위치의 글자가 max 안에 다 들어가면 그 글자까지 포함
        var next = StringInfo.GetNextTextElementLength(text, cut);
        return cut + next <= max ? text[..(cut + next)] : text[..cut];
    }
}
