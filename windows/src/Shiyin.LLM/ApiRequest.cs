using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.Localization;

namespace Shiyin.LLM;

/// <summary>API 整理工具共用的 HTTP 送出與錯誤處理（M§8.4、§19.1、§21.14）</summary>
public static class ApiRequest
{
    public sealed record Response(int Status, JsonNode? Body, string Message)
    {
        public JsonObject Object => Body as JsonObject ?? [];
    }

    /// <summary>重試前等的時間：測試可以改短</summary>
    public static TimeSpan NetworkRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public static double MaxRetryAfterSeconds { get; set; } = 30;

    /// <summary>送出請求，HTTP 狀態不是 200 時丟出看得懂的錯誤</summary>
    public static async Task<JsonObject> Send(HttpClient http, Func<HttpRequestMessage> request, string provider, CancellationToken cancel)
    {
        var response = await Fetch(http, request, provider, cancel);
        if (response.Status != 200) throw Error(response, provider);
        return response.Object;
    }

    static readonly int[] RetryableStatuses = [429, 500, 502, 503, 504];

    /// <summary>不檢查 HTTP 狀態（OpenAI 相容 API 要看錯誤內容決定要不要退回 JSON 模式）。
    /// 網路暫時出錯、429、5xx 時自動重試一次（R20.5）；取消時丟出 OperationCanceledException</summary>
    public static async Task<Response> Fetch(HttpClient http, Func<HttpRequestMessage> makeRequest, string provider, CancellationToken cancel)
    {
        var retried = false;
        while (true)
        {
            HttpResponseMessage message;
            try
            {
                using var timeout = new CancellationTokenSource(LLMClient.Timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
                message = await http.SendAsync(makeRequest(), linked.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                if (!retried && IsRetryable(e))
                {
                    retried = true;
                    await Task.Delay(NetworkRetryDelay, cancel);
                    continue;
                }
                throw LLMException.Unreachable(L.F("連不到 %@：%@", provider, e.Message));
            }
            using (message)
            {
                var data = await message.Content.ReadAsByteArrayAsync(cancel);
                var result = Parse(data, (int)message.StatusCode);
                // 429 是帳戶沒有額度時，等多久都不會好，不重試
                if (!retried && RetryableStatuses.Contains(result.Status) && !IsNoCredit(result))
                {
                    retried = true;
                    var header = message.Headers.TryGetValues("retry-after", out var values) ? values.FirstOrDefault() : null;
                    var wait = Math.Min(MaxRetryAfterSeconds, Math.Max(Math.Min(1, MaxRetryAfterSeconds),
                        double.TryParse(header, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 5));
                    await Task.Delay(TimeSpan.FromSeconds(wait), cancel);
                    continue;
                }
                return result;
            }
        }
    }

    /// <summary>網路暫時出錯（逾時、斷線、連不到主機、DNS）才重試</summary>
    static bool IsRetryable(Exception e) => e switch
    {
        OperationCanceledException => true,
        HttpRequestException { InnerException: SocketException s } => s.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionRefused
            or SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.HostNotFound or SocketError.HostUnreachable
            or SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.TryAgain,
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError } => true,
        _ => false,
    };

    static Response Parse(byte[] data, int status)
    {
        JsonNode? parsed = null;
        try { parsed = JsonNode.Parse(data); } catch (JsonException) { }
        // Gemini 的 Interactions API 把錯誤包在陣列裡：[{"error": {...}}]（M§19.2）
        var error = (parsed as JsonObject)?["error"] as JsonObject ?? ((parsed as JsonArray)?.FirstOrDefault() as JsonObject)?["error"] as JsonObject;
        var message = error?["message"]?.GetValue<string>() ?? Encoding.UTF8.GetString(data, 0, Math.Min(300, data.Length));
        return new Response(status, parsed, message);
    }

    static bool IsNoCredit(Response r)
    {
        var message = r.Message.ToLowerInvariant();
        return r.Status == 429 && new[] { "credit", "quota", "billing" }.Any(message.Contains);
    }

    public static LLMException Error(Response r, string provider)
    {
        var status = r.Status;
        // Gemini 的 key 錯誤是 HTTP 400 加上「API key not valid」
        var keyProblem = status == 401 || (status == 400 && r.Message.Contains("API key", StringComparison.OrdinalIgnoreCase));
        var noCredit = IsNoCredit(r);
        var hint = keyProblem ? L.T("（API key 錯誤或已失效）")
            : noCredit ? L.T("（帳戶沒有額度，要到服務的網站加值）")
            : status == 429 ? L.T("（超過用量限制，請稍後再試）") : "";
        var text = L.F("%@ 回傳 HTTP %lld%@：%@", provider, (long)status, hint, r.Message);
        // 設定問題和用量上限分開（M§34.4）：key 錯誤或沒權限要改設定，429 等一下或加值
        if (keyProblem || status == 403) return LLMException.Rejected(text);
        if (status == 429) return LLMException.Limited(text);
        return LLMException.Failed(text);
    }

    public static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
