namespace Shiyin.Model;

/// <summary>是非題的回答（M§27、R32、R33）：存成會議語言的文字，就是草稿和送給校稿的回答（同 Mac 的 YesNoAnswer）。
/// 「是」→ 是／Yes／はい；「不是」→ 不是／No／いいえ；不是而且填了答案 →「不是，答案」「No, 答案」「いいえ、答案」</summary>
public sealed record YesNoAnswer(bool? IsYes, string Correction = "")
{
    /// <summary>從存起來的文字認回來；空字串是還沒選，認不出來的（例如舊的草稿）回傳 null</summary>
    public static YesNoAnswer? Parse(string text, MeetingLanguage language)
    {
        var (yes, no, separator) = (Yes(language), No(language), Separator(language));
        if (text.Length == 0) return new YesNoAnswer((bool?)null);
        if (text == yes) return new YesNoAnswer(true);
        if (text == no) return new YesNoAnswer(false);
        if (text.StartsWith(no + separator, StringComparison.Ordinal)) return new YesNoAnswer(false, text[(no.Length + separator.Length)..]);
        return null;
    }

    public string Text(MeetingLanguage language) => IsYes switch
    {
        null => "",
        true => Yes(language),
        false => Correction.Length == 0 ? No(language) : No(language) + Separator(language) + Correction,
    };

    static string Yes(MeetingLanguage l) => l.Text("是", "Yes", "はい");  // l10n-skip：會議語言的回答
    static string No(MeetingLanguage l) => l.Text("不是", "No", "いいえ");  // l10n-skip：會議語言的回答
    static string Separator(MeetingLanguage l) => l.Text("，", ", ", "、");  // l10n-skip：會議語言的標點
}
