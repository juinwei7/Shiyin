using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Notes;

/// <summary>送給整理工具的一份參考文件（M§39.3）：名稱去掉副檔名，照加入的順序</summary>
public sealed record DocumentText(string Name, DocumentKind Kind, IReadOnlyList<string> Pages)
{
    public static DocumentText From(MeetingDocument document)
    {
        var name = PathText.DeletingExtension(document.Name);
        return new DocumentText(name.Length == 0 ? document.Name : name, document.DocumentKind, document.PageTexts);
    }

    /// <summary>字數：各頁字形叢集數的和（Swift 的 Character）</summary>
    public int Length => Pages.Sum(SwiftText.Count);
}

/// <summary>會議參考資料（M§39.3、R61.5～R61.7、D133、D135）：長度分配、提示詞裡的區塊、整理和提問多的規則</summary>
public static class DocumentPrompt
{
    /// <summary>一場會議的文件合計最多幾個字（R61.7，同問所有會議）</summary>
    public const int Limit = 40_000;

    public sealed record Allocated(DocumentText Document, List<string> Pages, int IncludedPages, int IncludedChars, bool Truncated)
    {
        /// <summary>截掉的說明用「前 N 頁」：有頁碼、而且後面還有整頁沒送；截在最後一頁裡時用字數</summary>
        public bool CutByPages => Truncated && Document.Kind.IsPaged() && IncludedPages < Document.Pages.Count;
    }

    /// <summary>平均分配：由短到長，每份拿「剩下的額度 ÷ 還沒分到的份數」和自己的長度取小的；短的整份送、長的截掉後面</summary>
    public static List<Allocated> Allocate(IReadOnlyList<DocumentText> documents, int limit = Limit)
    {
        var lengths = documents.Select(d => d.Length).ToList();
        var order = Enumerable.Range(0, documents.Count).OrderBy(i => lengths[i]).ThenBy(i => i).ToList();
        var budgets = new int[documents.Count];
        var remaining = limit;
        for (var k = 0; k < order.Count; k++)
        {
            var i = order[k];
            budgets[i] = Math.Min(lengths[i], remaining / (documents.Count - k));
            remaining -= budgets[i];
        }
        return documents.Select((d, i) => Truncate(d, budgets[i])).ToList();
    }

    /// <summary>整頁放進去，放不下的那一頁取前面的字，後面的頁不送</summary>
    public static Allocated Truncate(DocumentText document, int budget)
    {
        var pages = new List<string>();
        var used = 0;
        foreach (var page in document.Pages)
        {
            var count = SwiftText.Count(page);
            if (used + count <= budget)
            {
                pages.Add(page);
                used += count;
                continue;
            }
            var rest = budget - used;
            if (rest > 0)
            {
                pages.Add(SwiftText.Prefix(page, rest));
                used += rest;
            }
            break;
        }
        return new Allocated(document, pages, pages.Count, used, used < document.Length);
    }

    // MARK: 提示詞裡的區塊（放在重點標記之後、逐字稿之前）

    public static string Block(IReadOnlyList<DocumentText> documents, MeetingLanguage language, int limit = Limit)
    {
        var lines = new List<string>
        {
            language.Text("會議參考資料（使用者加入的文件，[第 N 頁] 是頁碼）：",
                          "Reference documents (added by the user; [Page N] marks page numbers):",
                          "会議の参考資料（ユーザーが追加した文書。[N ページ] はページ番号）："),  // l10n-skip
        };
        var allocated = Allocate(documents, limit);
        for (var index = 0; index < allocated.Count; index++)
        {
            var item = allocated[index];
            if (index > 0) lines.Add("");
            lines.Add(Title(item.Document.Name, language) + (item.Truncated ? TruncationNote(item, language) : ""));
            for (var page = 0; page < item.Pages.Count; page++)
            {
                var text = item.Pages[page];
                if (text.Length == 0) continue;
                if (item.Document.Kind.IsPaged()) lines.Add(Marker(page + 1, item.Document.Kind, language));
                lines.Add(text);
            }
        }
        return string.Join("\n", lines);
    }

    public static string Title(string name, MeetingLanguage language) => language.Text($"《{name}》", $"Document: {name}", $"『{name}』");

    public static string Marker(int page, DocumentKind kind, MeetingLanguage language) => kind == DocumentKind.Pptx
        ? language.Text($"[第 {page} 張投影片]", $"[Slide {page}]", $"[スライド {page}]")
        : language.Text($"[第 {page} 頁]", $"[Page {page}]", $"[{page} ページ]");

    public static string TruncationNote(Allocated item, MeetingLanguage language)
    {
        int n = item.IncludedPages, m = item.Document.Pages.Count;
        switch (item.CutByPages ? item.Document.Kind : DocumentKind.Text)
        {
            case DocumentKind.Pdf:
                return language.Text($"（文件太長，只附上前 {n} 頁，共 {m} 頁）",
                                     $" (too long; only the first {n} of {m} pages are included)",
                                     $"（長すぎるため、全 {m} ページのうち最初の {n} ページのみ）");  // l10n-skip
            case DocumentKind.Pptx:
                return language.Text($"（文件太長，只附上前 {n} 張投影片，共 {m} 張）",
                                     $" (too long; only the first {n} of {m} slides are included)",
                                     $"（長すぎるため、全 {m} 枚のスライドのうち最初の {n} 枚のみ）");  // l10n-skip
            default:
                int x = item.IncludedChars, y = item.Document.Length;
                return language.Text($"（文件太長，只附上前 {x} 字，共 {y} 字）",
                                     $" (too long; only the first {x} of {y} characters are included)",
                                     $"（長すぎるため、全 {y} 文字のうち最初の {x} 文字のみ）");  // l10n-skip
        }
    }

    // MARK: 規則（有文件時才接在原本的規則後面）

    /// <summary>整理：中文、英文 12～16，日文 13～17</summary>
    public static string NotesRules(MeetingLanguage language) => language.Text("""
        12. 「會議參考資料」是使用者加入、會議中一起討論的文件（簡報、規格書、報價單等）。用它來看懂逐字稿：討論指的是哪一頁、哪個方案，以及數字、名稱、專有名詞的正確寫法。
        13. 紀錄以會議中實際討論的內容為準。文件裡有、但會議中沒有討論的內容，不要寫進紀錄。
        14. 紀錄用到文件裡的資訊（數字、規格、日期、條文）時，在該條的句尾註明出處，例如「（《報價單》第 3 頁）」；投影片寫「（《簡報》第 3 張投影片）」，沒有頁碼的文件只寫「（《議程》）」。time 仍填逐字稿裡討論到它的時間點。
        15. 會議中的說法跟文件不一致時（例如數字、日期、名稱不同），照會議中的說法寫，並列進 reviewItems 請使用者確認。
        16. 專有名詞、人名在文件裡找得到正確寫法時，照文件寫，不必列進 reviewItems（這是規則 2 的例外）。
        """, """
        12. "Reference documents" are documents the user added that were discussed in the meeting (slides, specifications, quotes, etc.). Use them to understand the transcript: which page or option the discussion refers to, and the correct numbers, names and spellings of terms.
        13. The notes follow what was actually discussed in the meeting. Do not include content that is only in the documents and was not discussed.
        14. When an item uses information from a document (numbers, specifications, dates, clauses), cite the source at the end of that item, for example "(Quote, p. 3)"; for slides write "(Deck, slide 3)", and for a document without page numbers just its name, "(Agenda)". time is still when it was discussed in the transcript.
        15. If what was said in the meeting differs from a document (for example a different number, date or name), write what was said in the meeting and add it to reviewItems for the user to confirm.
        16. If the correct spelling of a proper noun or name can be found in the documents, use it; it does not need to go in reviewItems (an exception to rule 2).
        """, """
        13. 「会議の参考資料」は、ユーザーが追加した、会議で一緒に検討された文書（スライド、仕様書、見積書など）である。文字起こしを理解するために使う：議論がどのページ・どの案を指しているか、数字・名前・専門用語の正しい表記。
        14. 議事録は会議で実際に議論された内容を基準とする。文書にあっても会議で議論されなかった内容は書かない。
        15. 文書の情報（数字、仕様、日付、条文）を使った項目には、文末に出典を書く。例：「（『見積書』3 ページ）」。スライドは「（『提案資料』スライド 3）」、ページ番号のない文書は「（『議題』）」とする。time は引き続き文字起こしで議論された時刻を入れる。
        16. 会議での発言と文書の内容が食い違う場合（数字、日付、名前など）は、会議での発言どおりに書き、reviewItems に入れてユーザーに確認してもらう。
        17. 固有名詞や人名の正しい表記が文書で確認できる場合はそれに従い、reviewItems に入れなくてよい（ルール 2 の例外）。
        """);

    /// <summary>對一場會議提問：第 5 條</summary>
    public static string AskRule(MeetingLanguage language) => language.Text("""
        5. 「會議參考資料」是會議中一起討論的文件，也可以用來回答，但要說清楚是會議中說的，還是只有文件裡寫的。根據文件的重點放進 citations 時，time 填空字串，quote 開頭寫出處，例如「《報價單》第 3 頁：…」。
        """, """
        5. The "Reference documents" were discussed in the meeting and can also be used to answer, but make clear whether something was said in the meeting or is only written in a document. For a point based on a document, add a citation with "time" as an empty string and "quote" starting with the source, for example "Quote, p. 3: …".
        """, """
        5. 「会議の参考資料」は会議で一緒に検討された文書で、回答に使ってもよいが、会議で話されたことか、文書に書いてあるだけのことかを区別すること。文書にもとづく要点を citations に入れるときは、time を空文字列にし、quote の先頭に出典を書く（例：「『見積書』3 ページ：…」）。
        """);
}
