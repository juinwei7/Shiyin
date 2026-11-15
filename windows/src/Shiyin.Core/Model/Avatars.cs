using System.Text;
using Shiyin.Notes;
using Shiyin.Text;

namespace Shiyin.Model;

/// <summary>
/// 頭像（M§58、R83，同 Mac 的 Views/Avatar.swift）：寫在頭像上的字、人物的釉色。畫面在 App 的 <c>Ui.Avatar</c>；
/// 這裡不碰畫面，標準答案 util/avatars.json 比對（Mac 的 PersonAvatar.initials、Color.person）
/// </summary>
public static class Avatars
{
    /// <summary>
    /// 釉色（M§58.1）＝說話者的 8 種顏色（淺色、深色）：天青、茄皮紫、茶葉末、鈞紫、霽紅、松石、醬釉、青瓷。
    /// 青瓷綠是時間點按鈕的顏色、琥珀色代表低信心，所以這兩種排在最後，人少的會議不會用到
    /// </summary>
    public static readonly (uint Light, uint Dark)[] Palette =
    [
        (0x3F5BA9, 0x8FA8EE), (0x8E3B6B, 0xE08BBE), (0x4F7A28, 0x9BCB6E), (0x7A4FB5, 0xBFA0EE),
        (0xA23B3B, 0xEE8E8E), (0x2F7486, 0x7CC4D6), (0x9A5B13, 0xE2A65E), (0x2D6A5D, 0x6DB2A1),
    ];

    /// <summary>說話者編號（從 1 開始）的顏色：8 色循環（同 Mac 的 Color.speaker）</summary>
    public static (uint Light, uint Dark) Speaker(int idx) => Palette[(Math.Max(idx, 1) - 1) % Palette.Length];

    /// <summary>
    /// 人物頁、側欄的顏色（M§58.1）：照名字算，同一個名字每次都一樣。名字去掉頭尾空白、轉小寫的 UTF-8 做 FNV-1a，
    /// 取 8 的餘數；回傳說話者編號（1～8），顏色用 <see cref="Speaker"/>
    /// </summary>
    public static int PersonIndex(string name)
    {
        var hash = 2_166_136_261u;
        foreach (var b in Encoding.UTF8.GetBytes(SwiftText.Lower(SwiftText.Trim(name))))
            hash = unchecked((hash ^ b) * 16_777_619u);
        return (int)(hash % 8) + 1;
    }

    /// <summary>
    /// 寫在頭像上的字（M§58.2）：預設名稱（「說話者 3」）寫裡面的數字；第一個字是中日韓文字時取第一個字；
    /// 其他照空白（和 - . _）切開，取前兩段的第一個字母（大寫）；都沒有時取第一個字；空的是「?」
    /// </summary>
    public static string Initials(string name)
    {
        var trimmed = SwiftText.Trim(name);
        var characters = SwiftText.Characters(trimmed);
        if (characters.Count == 0) return "?";
        var first = characters[0];
        if (SpeakerNames.IsDefault(trimmed))
        {
            var digits = characters.Where(SwiftText.IsNumber).ToList();
            if (digits.Count > 0) return string.Concat(digits.Take(2));
        }
        if (IsCjk(Rune.GetRuneAt(first, 0).Value)) return first;
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in characters)
        {
            if (IsSeparator(c))
            {
                if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) words.Add(current.ToString());
        var letters = words.Take(2).Select(w => SwiftText.Characters(w)[0].ToUpperInvariant()).ToList();
        return letters.Count == 0 ? first : string.Concat(letters);
    }

    /// <summary>Swift 的 Character.isWhitespace，加上 - . _</summary>
    static bool IsSeparator(string character) =>
        character is "-" or "." or "_" || (character.Length > 0 && char.IsWhiteSpace(character[0]));

    /// <summary>平假名、片假名、中日韓統一表意文字（含擴充、相容）、韓文音節</summary>
    static bool IsCjk(int scalar) => scalar is (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF)
        or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x2FA1F);
}
