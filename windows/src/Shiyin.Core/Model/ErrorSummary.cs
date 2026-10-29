namespace Shiyin.Model;

/// <summary>錯誤自己的種類（design-windows §12.7、R54.4）：寫進 log 和 jobs.error_kind，只有種類、不含訊息（訊息可能有逐字稿，M§21.9）</summary>
public interface ILogSummary
{
    string LogSummary { get; }
}
