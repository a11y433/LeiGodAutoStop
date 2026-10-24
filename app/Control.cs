using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeiGodAutoStop;

public sealed class PauseRequestOutcomeUnknownException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Uses only a pause/status request observed from this exact running LeiGod client.
/// A successful HTTP request is not pause proof; callers must apply PauseProofRules.
/// This adapter is not wired to automatic execution until live idle acceptance.
/// </summary>
/// <summary>
/// Token-free, read-only summary of what the running client's logs say about pause capability.
/// Fingerprint never contains tokens or request bodies; it only describes field names and separators.
/// </summary>
public sealed record PauseLogEvidence(int FilesScanned, int ClientLines, int PausePathLines, int StatusRequestLines, int UsableBodyLines, string Fingerprint, string RequestSource, string BodySource)
{
    public static PauseLogEvidence Empty { get; } = new(0, 0, 0, 0, 0, "", "none", "none");
    public bool HasUsableRequest => RequestSource != "none" && BodySource != "none";
}

public sealed class ClientLogPauseAdapter : IPauseAdapter
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Regex Prefix = new(@"^\[(?<pid>\d+)\]\[(?<time>[^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex StatusUrl = new(@"(?<url>https://api[123]\.leigod\.com/client/pause/status\?[^\""'\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareStatusUrl = new(@"(?<url>https://api[123]\.leigod\.com/client/pause(?:/status)?)(?![A-Za-z0-9_\-/])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JsonParamsField = new(@"""params""\s*:\s*\{(?<body>[^{}]{0,4000})\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JsonParamsPair = new(@"""(?<key>[^""\\]{1,64})""\s*:\s*(?:""(?<text>(?:\\.|[^""\\])*)""|(?<raw>[^,{}\s]{1,200}))", RegexOptions.Compiled);
    private static readonly Regex Token = new(@"(?:account_token|accountToken)[=\""' :]+(?<value>[^&\s,;\""']{12,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnyLeigodRequestUrl = new(@"(?<url>https://[A-Za-z0-9\.\-]+\.leigod\.com/[^\""'\s]*\?[^\""'\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JsonBodyField = new(@"""(?<field>body|data|payload|postData|post_data)""\s*:\s*""(?<value>(?:\\.|[^""\\])*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UserIdHeader = new(@"[""']X-User-Id[""']\s*:\s*[""'](?<value>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeviceIdHeader = new(@"[""']X-Device-Id[""']\s*:\s*[""'](?<value>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BracketMarker = new(@"\[(?<marker>[A-Za-z][A-Za-z0-9_]{0,15})\]", RegexOptions.Compiled);
    private static readonly Regex JsonEscape = new(@"\\[""\\/bfnrt]", RegexOptions.Compiled);
    private static readonly Regex SecretishField = new(@"(token|secret|password|passwd|hardware|device|mac|imei|uid|user|account|phone|mobile)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UrlHost = new(@"https://(?<host>[A-Za-z0-9\.\-]+)", RegexOptions.Compiled);
    private static readonly Regex ClientPath = new(@"/(?<path>client/[A-Za-z0-9_/\-\.]{1,60})", RegexOptions.Compiled);
    /// <summary>
    /// Every pause request line in this machine's logs (2026-09-25/26/27, api2 and api3, several client
    /// sessions, 177 lines) carries the same client level body, so it is a protocol constant rather than a
    /// per install secret. A body observed on the current machine is always preferred; this constant only
    /// fills the gap on a machine whose own logs never recorded such a line. A wrong value cannot produce a
    /// false success because the caller must still confirm the pause through two account readbacks.
    /// </summary>
    private const string KnownClientSignature = "tQFyh13f7AYsHg==";
    private static readonly string[] PreferredPauseHosts = ["api2.leigod.com", "api1.leigod.com", "api3.leigod.com"];
    private readonly string _logDirectory;
    private readonly int _clientPid;
    private readonly DateTime _clientStart;
    private DateTime _accountIdentityCacheUntil;
    private string? _cachedAccountIdentity;

    public ClientLogPauseAdapter(string logDirectory, int clientPid, DateTime clientStart)
    {
        _logDirectory = logDirectory;
        _clientPid = clientPid;
        _clientStart = clientStart;
    }

    public string Id => "leigod-current-client-log-pause";
    public int Version => 1;
    public string LastRequestDisposition { get; private set; } = "尚未发送暂停请求";
    public bool CanControl(string clientVersion) => Inspect().HasUsableRequest;
    public async Task<PauseProof> ReadPauseStateAsync(CancellationToken ct)
    {
        string? tokenValue = null;
        if (TryReadRequest(out var statusUri, out _))
        {
            tokenValue = statusUri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Equals("account_token", StringComparison.OrdinalIgnoreCase))?[1];
        }
        if (string.IsNullOrWhiteSpace(tokenValue)) TryReadAccountToken(out tokenValue);
        if (string.IsNullOrWhiteSpace(tokenValue)) return new(false, null, DateTime.UtcNow, "当前客户端日志未找到账号令牌；请确认雷神已登录并重新打开客户端");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://webapi.leigod.com/api/user/info")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["account_token"] = Uri.UnescapeDataString(tokenValue), ["lang"] = "zh_CN", ["os_type"] = "4" })
        };
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode) return new(false, null, DateTime.UtcNow, "账号状态接口 HTTP " + (int)response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (!TryReadNumber(root, "code", out var code)) return new(false, null, DateTime.UtcNow, "账号接口缺少有效结果码");
        if (code != 0) return new(false, null, DateTime.UtcNow, "账号接口拒绝读取，结果码=" + code.ToString(CultureInfo.InvariantCulture) + "；请核对雷神登录状态");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !TryReadNumber(data, "pause_status_id", out var state) || !TryReadNumber(data, "user_pause_time", out var seconds)) return new(false, null, DateTime.UtcNow, "账号状态响应缺少必要字段或字段类型不兼容");
        if (state is not (0 or 1)) return new(false, null, DateTime.UtcNow, "账号暂停状态值未知：" + state.ToString(CultureInfo.InvariantCulture));
        if (seconds < 0) return new(false, null, DateTime.UtcNow, "剩余时长不是有效数字");
        return new(state == 1, seconds, DateTime.UtcNow, "当前客户端账号状态; pause_status_id=" + state.ToString(CultureInfo.InvariantCulture));
    }

    public async Task RequestPauseAsync(CancellationToken ct)
    {
        var inspection = InspectLogs();
        if (inspection.Uri is null || string.IsNullOrWhiteSpace(inspection.Body)) throw new InvalidOperationException("当前未从雷神客户端日志取得账号参数；本轮不发送暂停请求");
        var failures = new List<string>();
        foreach (var pauseUri in PauseCandidates(inspection.Uri).Take(3))
        {
            if (pauseUri.Scheme != Uri.UriSchemeHttps || !IsAllowedApiHost(pauseUri.Host) || pauseUri.AbsolutePath != "/client/pause") throw new InvalidOperationException("暂停请求地址未通过白名单检查");
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, pauseUri) { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(inspection.Body)) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "UTF-8" };
                request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
                request.Headers.UserAgent.ParseAdd("axios/1.12.0");
                if (!string.IsNullOrWhiteSpace(inspection.UserId)) request.Headers.TryAddWithoutValidation("X-User-Id", inspection.UserId);
                var deviceId = inspection.DeviceId ?? QueryValue(pauseUri, "hardware_id");
                if (!string.IsNullOrWhiteSpace(deviceId)) request.Headers.TryAddWithoutValidation("X-Device-Id", Uri.UnescapeDataString(deviceId));
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode >= 500)
                    {
                        LastRequestDisposition = pauseUri.Host + " HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + "；服务端处理结果未知，禁止换主机重复发送，先查账户状态";
                        throw new PauseRequestOutcomeUnknownException(LastRequestDisposition);
                    }
                    failures.Add($"{pauseUri.Host} HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}");
                    continue;
                }
                var responseText = await response.Content.ReadAsStringAsync(attempt.Token);
                if (TryReadBusinessResult(responseText, out var accepted, out var detail))
                {
                    if (!accepted) { failures.Add(pauseUri.Host + " 业务拒绝：" + detail); continue; }
                    LastRequestDisposition = pauseUri.Host + " HTTP 与业务结果均接受；等待账户状态读回";
                    return;
                }
                LastRequestDisposition = pauseUri.Host + " HTTP 已接受但响应没有可识别的业务结果码；等待账户状态读回";
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LastRequestDisposition = pauseUri.Host + " 请求超时；服务端是否执行未知，禁止换主机重复发送，先查账户状态";
                throw new PauseRequestOutcomeUnknownException(LastRequestDisposition);
            }
            catch (HttpRequestException ex)
            {
                LastRequestDisposition = pauseUri.Host + " 网络中断；服务端是否执行未知，禁止换主机重复发送，先查账户状态";
                throw new PauseRequestOutcomeUnknownException(LastRequestDisposition, ex);
            }
        }
        throw new InvalidOperationException("暂停请求未获接受：" + string.Join("；", failures));
    }

    private static bool TryReadBusinessResult(string text, out bool accepted, out string detail)
    {
        accepted = false;
        detail = "响应格式未知";
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (TryReadNumber(root, "code", out var code))
            {
                accepted = code is 0 or 200;
                detail = "code=" + code.ToString(CultureInfo.InvariantCulture);
                if (!accepted && root.TryGetProperty("message", out var message)) detail += " " + SafeLog.Redact(message.ToString());
                return true;
            }
            if (root.TryGetProperty("success", out var success) && success.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                accepted = success.GetBoolean();
                detail = "success=" + accepted.ToString();
                return true;
            }
            if (root.TryGetProperty("status", out var status) && status.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                accepted = status.GetBoolean();
                detail = "status=" + accepted.ToString();
                return true;
            }
            return false;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Pause targets built from the account parameters found in the current client logs. The client serves the
    /// same pause call from api1, api2 and api3 depending on the session, so the hosts are tried in order and a
    /// wrong host simply fails the HTTP call. The caller still has to confirm the pause through account readbacks.
    /// </summary>
    internal static IEnumerable<Uri> PauseCandidates(Uri observed)
    {
        var hosts = new List<string>();
        if (IsAllowedApiHost(observed.Host)) hosts.Add(observed.Host);
        hosts.AddRange(PreferredPauseHosts);
        foreach (var host in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
            yield return new UriBuilder("https", host) { Path = "/client/pause", Query = observed.Query.TrimStart('?') }.Uri;
    }

    /// <summary>Public read-only probe used by the first-use check to explain why pause capability is or is not available.</summary>
    public PauseLogEvidence Inspect() => InspectLogs().Evidence;

    /// <summary>Stable, non-reversible account-session key used only to reset monitoring on login changes.</summary>
    public string? ReadAccountIdentity(bool refresh = false)
    {
        if (!refresh && DateTime.UtcNow < _accountIdentityCacheUntil) return _cachedAccountIdentity;
        string? token = null;
        var inspection = InspectLogs();
        if (inspection.Uri is not null) token = QueryValue(inspection.Uri, "account_token");
        if (string.IsNullOrWhiteSpace(token)) TryReadAccountToken(out token);
        _cachedAccountIdentity = string.IsNullOrWhiteSpace(token)
            ? null
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(token))));
        _accountIdentityCacheUntil = DateTime.UtcNow.AddSeconds(10);
        return _cachedAccountIdentity;
    }

    private static string? QueryValue(Uri uri, string name) => uri.Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2))
        .FirstOrDefault(parts => parts.Length == 2 && Uri.UnescapeDataString(parts[0]).Equals(name, StringComparison.OrdinalIgnoreCase)) is { Length: 2 } found
            ? found[1]
            : null;

    private static DateTime ReadLineTime(string line)
    {
        var prefix = Prefix.Match(line);
        if (prefix.Success && DateTime.TryParse(prefix.Groups["time"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when)) return when;
        try
        {
            using var document = JsonDocument.Parse(line);
            foreach (var key in new[] { "time", "timestamp", "created_at", "createdAt", "ts" })
            {
                if (!document.RootElement.TryGetProperty(key, out var value)) continue;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
                    return epoch > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).LocalDateTime : DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
                if (DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out when)) return when;
            }
        }
        catch { }
        return DateTime.MinValue;
    }

    private bool TryReadRequest(out Uri uri, out string body)
    {
        var inspection = InspectLogs();
        uri = inspection.Uri!;
        body = inspection.Body;
        return inspection.Uri is not null;
    }

    private sealed record Inspection(PauseLogEvidence Evidence, Uri? Uri, string Body, string? UserId, string? DeviceId);

    internal static (string? UserId, string? DeviceId) ExtractRequestHeaders(string line)
    {
        var user = UserIdHeader.Match(line);
        var device = DeviceIdHeader.Match(line);
        return (user.Success ? user.Groups["value"].Value : null,
            device.Success ? device.Groups["value"].Value : null);
    }

    private Inspection InspectLogs()
    {
        if (!Directory.Exists(_logDirectory)) return new(PauseLogEvidence.Empty, null, "", null, null);
        Uri? pauseUri = null;
        Uri? genericUri = null;
        var observedBody = "";
        var pauseTime = DateTime.MinValue;
        var genericTime = DateTime.MinValue;
        var files = 0; var clientLines = 0; var pauseLines = 0; var statusLines = 0; var usable = 0;
        var pauseShape = "";
        var genericShape = "";
        var unmatchedHint = "";
        string? userId = null;
        string? deviceId = null;
        var headerTime = DateTime.MinValue;
        var accepted = AcceptedClientPids();
        foreach (var file in FindHttpLogFiles().OrderByDescending(File.GetLastWriteTimeUtc).Take(8))
        {
            files++;
            var lineNo = 0;
            var fileUnmatched = "";
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 8_000_000) stream.Seek(-8_000_000, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    lineNo++;
                    if (!IsClientLine(line, accepted)) continue;
                    clientLines++;
                    var lineTime = ReadLineTime(line);
                    if (lineTime >= headerTime)
                    {
                        var (foundUser, foundDevice) = ExtractRequestHeaders(line);
                        if (foundUser is not null || foundDevice is not null)
                        {
                            if (foundUser is not null) userId = foundUser;
                            if (foundDevice is not null) deviceId = foundDevice;
                            headerTime = lineTime;
                        }
                    }
                    if (line.IndexOf("/client/pause", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        pauseLines++;
                        if (!TryExtractRequest(line, out var found, out var candidateBody, out var shape))
                        {
                            if (fileUnmatched.Length == 0) fileUnmatched = $"{Path.GetFileName(file)} line {lineNo}: {DescribePauseLikeLine(line)}";
                            continue;
                        }
                        statusLines++;
                        if (pauseShape.Length == 0) pauseShape = $"{Path.GetFileName(file)} line {lineNo}: {shape}";
                        var when = ReadLineTime(line);
                        if (pauseUri is null || when >= pauseTime)
                        {
                            pauseUri = found;
                            pauseTime = when;
                            observedBody = candidateBody;
                        }
                        if (!string.IsNullOrWhiteSpace(candidateBody)) usable++;
                        continue;
                    }
                    if (TryExtractGenericRequest(line, out var other, out var otherShape))
                    {
                        var when = ReadLineTime(line);
                        if (genericUri is null || when >= genericTime)
                        {
                            genericUri = other;
                            genericTime = when;
                            genericShape = $"{Path.GetFileName(file)} line {lineNo}: {otherShape}";
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (unmatchedHint.Length == 0 && fileUnmatched.Length > 0) unmatchedHint = fileUnmatched;
        }
        var usePauseLine = pauseUri is not null && (genericUri is null || pauseTime >= genericTime);
        var queryUri = usePauseLine ? pauseUri : genericUri;
        var requestSource = queryUri is null ? "none" : usePauseLine ? "pause-line" : "client-http-line";
        var body = usePauseLine && !string.IsNullOrWhiteSpace(observedBody) ? observedBody : queryUri is not null ? KnownClientSignature : "";
        var bodySource = usePauseLine && !string.IsNullOrWhiteSpace(observedBody) ? "observed" : queryUri is not null ? "client-signature" : "none";
        var fingerprint = usePauseLine ? pauseShape : genericShape.Length > 0 ? genericShape : pauseShape.Length > 0 ? pauseShape : unmatchedHint;
        return new(new PauseLogEvidence(files, clientLines, pauseLines, statusLines, usable, fingerprint, requestSource, bodySource), queryUri, body, userId, deviceId);
    }

    /// <summary>
    /// Any LeiGod request line carries the same client wide account parameters, verified field by field against
    /// a real pause line (account_token and hardware_id byte identical). This lets a machine that never logged
    /// the pause endpoint itself still build the pause call.
    /// </summary>
    private bool TryExtractGenericRequest(string line, out Uri uri, out string shape)
    {
        uri = null!;
        shape = "";
        foreach (Match match in AnyLeigodRequestUrl.Matches(line))
        {
            if (!Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var found)) continue;
            if (found.Scheme != Uri.UriSchemeHttps || !found.Host.EndsWith(".leigod.com", StringComparison.OrdinalIgnoreCase)) continue;
            if (!found.Query.Contains("account_token=", StringComparison.OrdinalIgnoreCase)) continue;
            uri = found;
            shape = DescribeQuery(found);
            return true;
        }
        var query = QueryFromParams(line);
        if (string.IsNullOrWhiteSpace(query) || !query.Contains("account_token=", StringComparison.OrdinalIgnoreCase)) return false;
        uri = new UriBuilder("https", PreferredPauseHosts[0]) { Path = "/client/pause", Query = query }.Uri;
        shape = DescribeQuery(uri);
        return true;
    }

    private bool TryExtractRequest(string line, out Uri uri, out string body, out string shape)
    {
        uri = null!;
        body = "";
        shape = "";
        foreach (Match match in StatusUrl.Matches(line))
        {
            if (!Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var found)) continue;
            if (found.Scheme != Uri.UriSchemeHttps || !IsAllowedApiHost(found.Host)) continue;
            if (found.AbsolutePath != "/client/pause/status") continue;
            if (!found.Query.Contains("account_token=", StringComparison.OrdinalIgnoreCase)) continue;
            var tail = line[(match.Index + match.Length)..];
            var candidate = ExtractBody(tail, line);
            uri = found;
            body = candidate;
            shape = DescribeShape(line, found, tail, candidate);
            return true;
        }
        // Client-side axios error records keep the endpoint in "url" and the query values in a "params" object,
        // and they may cover either /client/pause or /client/pause/status without a query on the url itself.
        foreach (Match match in BareStatusUrl.Matches(line))
        {
            if (!Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var bare)) continue;
            if (bare.Scheme != Uri.UriSchemeHttps || !IsAllowedApiHost(bare.Host)) continue;
            if (bare.AbsolutePath is not ("/client/pause" or "/client/pause/status")) continue;
            var tail = line[(match.Index + match.Length)..];
            var query = QueryFromTail(tail);
            if (string.IsNullOrWhiteSpace(query) || !query.Contains("account_token=", StringComparison.OrdinalIgnoreCase)) query = QueryFromParams(line);
            if (string.IsNullOrWhiteSpace(query) || !query.Contains("account_token=", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = ExtractBody(tail, line);
            var built = new UriBuilder(bare) { Query = query }.Uri;
            uri = built;
            body = candidate;
            shape = DescribeShape(line, built, tail, candidate);
            return true;
        }
        return false;
    }

    private static string? QueryFromTail(string tail)
    {
        var text = TrimDecorations(tail);
        if (text.Length == 0 || text[0] != '?') return null;
        var end = text.IndexOfAny(['"', '\'', ' ', '\t', '~']);
        return end >= 0 ? text[1..end] : text[1..];
    }

    private static string? QueryFromParams(string line)
    {
        var block = JsonParamsField.Match(line);
        if (!block.Success) return null;
        var pairs = new List<string>();
        foreach (Match pair in JsonParamsPair.Matches(block.Groups["body"].Value))
        {
            var key = pair.Groups["key"].Value;
            if (key.Length == 0) continue;
            var value = pair.Groups["text"].Success ? UnescapeJson(pair.Groups["text"].Value) : pair.Groups["raw"].Value;
            pairs.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
        }
        return pairs.Count == 0 ? null : string.Join("&", pairs);
    }

    /// <summary>
    /// Reads the request body that follows an observed pause/status URL. Only bytes that really appear in the
    /// log are returned; nothing is synthesized, and an unclear structure stays empty so callers keep blocking.
    /// </summary>
    private static string ExtractBody(string tail, string line)
    {
        var head = TrimDecorations(tail);
        var field = JsonBodyField.Match(head.Length > 0 ? head : line);
        if (field.Success)
        {
            var value = UnescapeJson(field.Groups["value"].Value);
            if (IsPlausibleBody(value, strict: false)) return value;
        }
        if (head.Length > 0 && "~|,;".IndexOf(head[0]) >= 0)
        {
            var delimiter = head[0];
            var inner = head[1..];
            var end = inner.IndexOf(delimiter);
            if (end >= 0)
            {
                var paired = inner[..end].Trim();
                if (IsPlausibleBody(paired, strict: false)) return paired;
            }
            else
            {
                var loose = inner.Trim().Trim('"', '\'');
                if (IsPlausibleBody(loose, strict: true)) return loose;
            }
        }
        return "";
    }

    private static string TrimDecorations(string value)
    {
        var text = value.TrimStart();
        while (text.Length > 0 && (text[0] == '"' || text[0] == '\'')) text = text[1..].TrimStart();
        return text;
    }

    private static bool IsPlausibleBody(string value, bool strict)
    {
        if (value.Length is < 4 or > 512) return false;
        if (value.StartsWith('[')) return false;
        if (value.Any(char.IsWhiteSpace)) return false;
        return strict
            ? value.All(c => char.IsAsciiLetterOrDigit(c) || c is '=' or '+' or '/' or '-' or '_' or '.')
            : !value.Any(char.IsControl);
    }

    private static string UnescapeJson(string value) => JsonEscape.Replace(value, match => match.Value switch
    {
        "\\\"" => "\"",
        "\\\\" => "\\",
        "\\/" => "/",
        "\\b" => "\b",
        "\\f" => "\f",
        "\\n" => "\n",
        "\\r" => "\r",
        "\\t" => "\t",
        _ => match.Value
    });

    /// <summary>Describes a parsed request without any token, query value or body content.</summary>
    private static string DescribeShape(string line, Uri uri, string tail, string body)
    {
        var markers = BracketMarker.Matches(line).Select(m => m.Groups["marker"].Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(4);
        var separators = new string(tail.Trim().Where(c => !char.IsLetterOrDigit(c)).Distinct().Take(4).ToArray());
        var bodyShape = string.IsNullOrWhiteSpace(body) ? "missing" : $"len={body.Length} class={CharsetClass(body)}";
        return $"{DescribeQuery(uri)} markers=[{string.Join(",", markers)}] separators=[{separators}] body={bodyShape}";
    }

    /// <summary>Describes the request target and query field names only; values are never included.</summary>
    private static string DescribeQuery(Uri uri)
    {
        var fields = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)[0])
            .Where(name => name.Length > 0)
            .Select(name => SecretishField.IsMatch(name) ? "<secret>" : name)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return $"host={uri.Host} path={uri.AbsolutePath} query-fields=[{string.Join(",", fields)}]";
    }

    /// <summary>Describes a pause-like line that did not match the known request shape, again without values.</summary>
    private static string DescribePauseLikeLine(string line)
    {
        var host = UrlHost.Match(line).Groups["host"].Value;
        var path = ClientPath.Match(line).Groups["path"].Value;
        var markers = BracketMarker.Matches(line).Select(m => m.Groups["marker"].Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(4);
        return $"host={(host.Length > 0 ? host : "none")} path={(path.Length > 0 ? "/" + path : "none")} hasQuery={line.Contains('?')} hasToken={line.Contains("account_token", StringComparison.OrdinalIgnoreCase)} markers=[{string.Join(",", markers)}]";
    }

    private static string CharsetClass(string value) =>
        value.All(char.IsAsciiLetterOrDigit) ? "alnum"
        : value.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=') ? "base64"
        : value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '=') ? "base64url"
        : "mixed";

    private bool TryReadAccountToken(out string? token)
    {
        token = null;
        var latest = DateTime.MinValue;
        var accepted = AcceptedClientPids();
        foreach (var file in FindHttpLogFiles().OrderByDescending(File.GetLastWriteTimeUtc).Take(8))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 8_000_000) stream.Seek(-8_000_000, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (!IsClientLine(line, accepted)) continue;
                    var match = Token.Matches(line).Cast<Match>().LastOrDefault();
                    if (match is null) continue;
                    var value = match.Groups["value"].Value.Trim('"', '\'');
                    var when = ReadLineTime(line);
                    if (value.Length >= 12 && when >= latest) { token = value; latest = when; }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return !string.IsNullOrWhiteSpace(token);
    }

    private IEnumerable<string> FindHttpLogFiles()
    {
        if (!Directory.Exists(_logDirectory)) return [];
        return new[] { "*.log", "*.txt" }.SelectMany(pattern => Directory.EnumerateFiles(_logDirectory, pattern, SearchOption.TopDirectoryOnly)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The root client plus the other live LeiGod processes installed beside it.</summary>
    private IReadOnlySet<int> AcceptedClientPids()
    {
        var set = new HashSet<int> { _clientPid };
        try
        {
            using var client = Process.GetProcessById(_clientPid);
            foreach (var pid in PauseOnlyClientDiscovery.SiblingPids(LeiGodDiscovery.SafePath(client))) set.Add(pid);
        }
        catch { }
        return set;
    }

    private bool IsClientLine(string line, IReadOnlySet<int> accepted)
    {
        var prefix = Prefix.Match(line);
        if (prefix.Success)
        {
            if (!int.TryParse(prefix.Groups["pid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefixPid) || !accepted.Contains(prefixPid)) return false;
            return DateTime.TryParse(prefix.Groups["time"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var stamped) && stamped >= _clientStart.AddSeconds(-2);
        }
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var root = doc.RootElement;
            var pid = root.TryGetProperty("pid", out var pidNode) ? pidNode.ToString() : root.TryGetProperty("process_id", out var processNode) ? processNode.ToString() : "";
            if (!int.TryParse(pid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPid) || !accepted.Contains(parsedPid)) return false;
            var timeNode = root.TryGetProperty("time", out var time) ? time : root.TryGetProperty("timestamp", out var timestamp) ? timestamp : default;
            return timeNode.ValueKind != JsonValueKind.Undefined && TryParseTime(timeNode, out var when) && when >= _clientStart.AddSeconds(-2);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryParseTime(JsonElement node, out DateTime value)
    {
        value = default;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var number))
        {
            try { value = number > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number).LocalDateTime : DateTimeOffset.FromUnixTimeSeconds(number).LocalDateTime; return true; }
            catch { return false; }
        }
        return DateTime.TryParse(node.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out value);
    }

    private static bool IsAllowedApiHost(string host) =>
        host.Equals("api1.leigod.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("api2.leigod.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("api3.leigod.com", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadNumber(JsonElement parent, string name, out decimal value)
    {
        value = 0;
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var node)) return false;
        return node.ValueKind == JsonValueKind.Number
            ? node.TryGetDecimal(out value)
            : decimal.TryParse(node.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}

#if LEGACY_STOP
/// <summary>
/// Uses the current client's own local SDK WebSocket command observed in its socket log:
/// method 100007 with game_id=-1. The command is accepted only when the SDK listener is
/// local and the caller still proves the same client/session and no target game process.
/// The command response is never treated as stop proof.  Builds that do not write a
/// stop-acc log line are verified with the same client's independent query_nn_acc
/// readback (method 100009) plus the session/process gates supplied by the caller.
/// </summary>
public sealed class ClientSdkSocketStopAdapter : IStopAdapter
{
    private static readonly int[] CandidatePorts = [10908, 5000];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static int? _lastLoggedAccelerationState;
    private readonly string _installRoot;
    private readonly int _clientPid;
    private readonly DateTime _clientStart;
    private readonly Func<bool> _sameClientAndNoGame;
    private readonly Func<bool> _hasStopProof;
    private readonly Func<bool> _stoppedSessionAndNoGame;
    private readonly Func<Task<bool>> _pauseStillConfirmed;
    private readonly ILeiGodVerifiedExitController _exit;

    public ClientSdkSocketStopAdapter(string installRoot, int clientPid, DateTime clientStart,
        Func<bool> sameClientAndNoGame, Func<bool> hasStopProof, Func<bool> stoppedSessionAndNoGame,
        Func<Task<bool>> pauseStillConfirmed, ILeiGodVerifiedExitController exit)
    {
        _installRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _clientPid = clientPid; _clientStart = clientStart; _sameClientAndNoGame = sameClientAndNoGame;
        _hasStopProof = hasStopProof; _stoppedSessionAndNoGame = stoppedSessionAndNoGame;
        _pauseStillConfirmed = pauseStillConfirmed; _exit = exit;
    }

    public string Id => "leigod-current-client-sdk-websocket-stop";
    public int Version => 1;
    public bool CanStopAcceleration => FindOwnedEndpoint() is not null;
    public bool StopReadbackConfirmed { get; private set; }

    public enum IdleObservation { NotAccelerating, Accelerating, Unknown }

    internal static bool HasExpectedSdkCorrelation(JsonElement root, int expectedMethod, int expectedId)
    {
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.Number && method.TryGetInt32(out var actualMethod) && actualMethod == expectedMethod &&
            root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var actualId) && actualId == expectedId;
    }

    // Fresh read-only proof for a client that has never produced an
    // acceleration event in the current log epoch. Do not reuse the cached
    // stop readback from an earlier game session.
    public async Task<bool> ProbeNoAccelerationAsync(CancellationToken ct)
    {
        return await ProbeIdleObservationAsync(ct) == IdleObservation.NotAccelerating;
    }

    public async Task<IdleObservation> ProbeIdleObservationAsync(CancellationToken ct)
    {
        if (!CanStopAcceleration || !_sameClientAndNoGame()) return IdleObservation.Unknown;
        var stopped = await QueryAccelerationStoppedStateAsync(ct);
        if (!_sameClientAndNoGame()) return IdleObservation.Unknown;
        return stopped switch
        {
            true => IdleObservation.NotAccelerating,
            false => IdleObservation.Accelerating,
            null => IdleObservation.Unknown
        };
    }

    public async Task RequestStopAccelerationAsync(CancellationToken ct)
    {
        var endpoint = FindOwnedEndpoint() ?? throw new NotSupportedException("当前雷神 SDK 本机停止协议不可验证");
        if (!_sameClientAndNoGame()) throw new InvalidOperationException("发送停止动作前客户端/游戏状态未通过复核");
        using var socket = new ClientWebSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(endpoint, timeout.Token);
        if (!IsCurrentSdkListenerOwned(endpoint.Port) || !_sameClientAndNoGame()) throw new InvalidOperationException("停止请求发送前 SDK 监听归属或游戏状态变化");
        var command = JsonSerializer.Serialize(new
        {
            method = 100007,
            param = new { game_id = -1, _reason = "other", reason = new { code = 555, message = "other" } },
            id = 1
        }, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(command);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
        var response = await ReceiveTextAsync(socket, timeout.Token);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.GetInt32() != 200 ||
            !root.TryGetProperty("method", out var method) || method.GetInt32() != 100007)
            throw new InvalidOperationException("雷神本机停止协议未返回成功响应");
        // The current 11.3 client can acknowledge pause first and emit its
        // interrupt/stop-acc + normal readback on the next periodic SDK cycle;
        // the observed live delay is about 147 seconds. Keep the app waiting
        // inside the verified workflow instead of declaring failure at 15s.
        // Keep the protocol attempt short enough to leave time for the
        // fail-closed process-tree exit path inside the workflow's 180s cap.
        var until = DateTime.UtcNow.AddSeconds(15);
        var nextQuery = DateTime.UtcNow;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (_hasStopProof()) return;
            if (DateTime.UtcNow >= nextQuery)
            {
                if (await QueryIsAccelerationStoppedAsync(ct))
                {
                    StopReadbackConfirmed = true;
                    return;
                }
                nextQuery = DateTime.UtcNow.AddSeconds(2);
            }
            await Task.Delay(250, ct);
        }
        throw new InvalidOperationException("客户端日志或同一 SDK 的 query_nn_acc 未确认加速已停止");
    }

    public async Task<bool> IsAccelerationStoppedAsync(CancellationToken ct)
    {
        if (_hasStopProof() || StopReadbackConfirmed) return true;
        if (!CanStopAcceleration || !_sameClientAndNoGame()) return false;
        StopReadbackConfirmed = await QueryIsAccelerationStoppedAsync(ct);
        return StopReadbackConfirmed;
    }

    public async Task RequestNormalExitAsync(CancellationToken ct)
    {
        var logProof = _hasStopProof();
        if (!logProof)
        {
            if (!StopReadbackConfirmed || !CanStopAcceleration || !await QueryIsAccelerationStoppedAsync(ct)) throw new InvalidOperationException("退出前缺少当前 SDK 监听归属下重新读取的 query_nn_acc 独立证据");
            StopReadbackConfirmed = true;
        }
        if (!_stoppedSessionAndNoGame() || !await _pauseStillConfirmed()) throw new InvalidOperationException("退出前 stop proof、游戏或暂停状态复核失败");
        await _exit.RequestVerifiedExitAsync(ct);
    }

    public async Task RequestPausedSessionExitFallbackAsync(CancellationToken ct)
    {
        if (!(_sameClientAndNoGame() || _stoppedSessionAndNoGame()) || !await _pauseStillConfirmed())
            throw new InvalidOperationException("回退进程树退出前原加速会话、游戏状态或暂停余额复核失败");
        await _exit.RequestPausedSessionTreeExitAsync(ct);
    }

    public Task<bool> AreOwnedClientProcessesGoneAsync(CancellationToken ct) => _exit.AreOwnedProcessesGoneAsync(ct);

    private async Task<bool> QueryIsAccelerationStoppedAsync(CancellationToken ct)
        => await QueryAccelerationStoppedStateAsync(ct) == true;

    private async Task<bool?> QueryAccelerationStoppedStateAsync(CancellationToken ct)
    {
        var endpoint = FindOwnedEndpoint() ?? throw new InvalidOperationException("当前雷神 SDK 本机查询协议不可验证");
        try
        {
            using var socket = new ClientWebSocket();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(endpoint, timeout.Token);
            if (!IsCurrentSdkListenerOwned(endpoint.Port) || !_sameClientAndNoGame()) throw new InvalidOperationException("query_nn_acc 发送前 SDK 监听归属或游戏状态变化");
            var command = JsonSerializer.Serialize(new { method = 100009, param = new { cmd = "query_nn_acc" }, id = 1 }, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(command);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
            var response = await ReceiveMatchingSdkResponseAsync(token => ReceiveTextAsync(socket, token), 100009, 1, endpoint.Port, timeout.Token);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            var code = default(JsonElement);
            var method = default(JsonElement);
            var data = default(JsonElement);
            var onAcc = default(JsonElement);
            var state = -1;
            var hasCode = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out code);
            var hasMethod = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("method", out method);
            var hasData = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;
            var hasState = hasData && data.TryGetProperty("is_onacc", out onAcc) && onAcc.ValueKind == JsonValueKind.Number && onAcc.TryGetInt32(out state);
            if (!hasCode || code.ValueKind != JsonValueKind.Number || code.GetInt32() != 200 ||
                !hasMethod || method.ValueKind != JsonValueKind.Number || method.GetInt32() != 100009 || !hasState)
            {
                SafeLog.Write($"sdk query_nn_acc rejected response port={endpoint.Port} root={root.ValueKind} code={(hasCode ? code.ToString() : "missing")} method={(hasMethod ? method.ToString() : "missing")} data={(hasData ? "object" : "missing-or-nonobject")} is_onacc={(hasState ? onAcc.ToString() : "missing-or-invalid")}");
                return null;
            }
            if (state is not (0 or 1))
            {
                SafeLog.Write($"sdk query_nn_acc returned unsupported state port={endpoint.Port} state={state}");
                return null;
            }
            if (_lastLoggedAccelerationState != state)
            {
                _lastLoggedAccelerationState = state;
                SafeLog.Write($"sdk query_nn_acc confirmed state={state} port={endpoint.Port}");
            }
            return state == 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            SafeLog.Write($"sdk query_nn_acc timed out port={endpoint.Port} timeout_seconds=5");
            return null;
        }
        catch (Exception ex)
        {
            SafeLog.Write($"sdk query_nn_acc failed port={endpoint.Port} error={SafeLog.Redact(ex.GetType().Name + ": " + ex.Message)}");
            return null;
        }
    }

    internal static async Task<string> ReceiveMatchingSdkResponseAsync(Func<CancellationToken, Task<string>> receive, int expectedMethod, int expectedId, int port, CancellationToken ct)
    {
        var ignored = 0;
        while (true)
        {
            var message = await receive(ct);
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (HasExpectedSdkCorrelation(root, expectedMethod, expectedId)) return message;

            var method = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("method", out var methodValue) && methodValue.ValueKind == JsonValueKind.Number
                ? methodValue.ToString() : "missing";
            var id = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.Number
                ? idValue.ToString() : "missing";
            ignored++;
            SafeLog.Write($"sdk query_nn_acc ignored unmatched websocket message port={port} method={method} id={id} count={ignored}");
            if (ignored >= 32) throw new InvalidOperationException("雷神 SDK 在查询响应前发送了过多无关消息");
        }
    }

    private Uri? FindOwnedEndpoint()
    {
        foreach (var port in CandidatePorts)
            if (ProcessOwnership.ListenerPid(port) > 0 && IsCurrentSdkListenerOwned(port))
                return new Uri($"ws://127.0.0.1:{port}");
        SafeLog.Write("sdk stop ownership: no verified local SDK listener");
        return null;
    }

    private bool IsCurrentSdkListenerOwned(int port)
    {
        try
        {
            using var client = new TcpClient();
            var pending = client.ConnectAsync("127.0.0.1", port);
            if (!pending.Wait(TimeSpan.FromSeconds(1)) || !client.Connected)
            {
                SafeLog.Write("sdk stop ownership: local listener unavailable");
                return false;
            }
            var sdks = Process.GetProcessesByName("leishenSdk");
            try
            {
                var listenerPid = ProcessOwnership.ListenerPid(port);
                var matched = sdks.Any(p =>
                {
                    try
                    {
                        var owned = ProcessOwnership.Read(p.Id);
                        var pathOk = owned.Name.Equals("leishenSdk", StringComparison.OrdinalIgnoreCase) && owned.Path.StartsWith(_installRoot, StringComparison.OrdinalIgnoreCase);
                        var userOk = owned.UserSid == ProcessOwnership.CurrentUserSid;
                        var sessionOk = owned.SessionId == ProcessOwnership.CurrentSessionId;
                        var client = ProcessOwnership.Read(_clientPid);
                        var clientOk = client.Name.Equals("leigod", StringComparison.OrdinalIgnoreCase) && client.StartTime == _clientStart && client.Path.StartsWith(_installRoot, StringComparison.OrdinalIgnoreCase) && client.UserSid == owned.UserSid && client.SessionId == owned.SessionId;
                        var parentOk = ProcessOwnership.SdkParentTopologyMatches(p.Id, ProcessOwnership.ParentPid(p.Id), owned.StartTime, _clientPid, ProcessOwnership.ParentPid(_clientPid), _clientStart);
                        var listenerOk = listenerPid == p.Id;
                        SafeLog.Write($"sdk stop ownership candidate pid={p.Id} listener={listenerOk} parent={parentOk} client={clientOk} path={pathOk} user={userOk} session={sessionOk}");
                        return listenerOk && parentOk && clientOk && pathOk && userOk && sessionOk;
                    }
                    catch (Exception ex) { SafeLog.Write("sdk stop ownership candidate unreadable=" + ex.GetType().Name); return false; }
                });
                SafeLog.Write("sdk stop ownership matched=" + matched);
                return matched;
            }
            finally { foreach (var p in sdks) p.Dispose(); }
        }
        catch { return false; }
    }


    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(chunk, ct);
            if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("雷神本机停止协议提前关闭连接");
            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(buffer.ToArray());
            if (buffer.Length > 1_000_000) throw new InvalidOperationException("雷神本机停止响应过大");
        }
    }
}
public sealed record AutoStopResult(bool Completed, string Stage, string Reason, bool AutomatedPauseRequest = false);
public static class StopSessionIdentity
{
    public static bool MatchesOriginal(IReadOnlyList<AccelerationSession> expected, SessionSnapshot current)
    {
        var expectedKeys = expected.Select(s => s.GameId + ":" + s.EventTime.Ticks).OrderBy(x => x, StringComparer.Ordinal);
        var currentKeys = current.ActiveSessions.Select(s => s.GameId + ":" + s.EventTime.Ticks).OrderBy(x => x, StringComparer.Ordinal);
        return current.Knowledge == SessionKnowledge.Accelerating && currentKeys.SequenceEqual(expectedKeys, StringComparer.Ordinal);
    }
}

/// <summary>Runs only after IdleMonitor reaches its configured idle threshold.</summary>
public sealed class AutoStopWorkflow
{
    public static readonly TimeSpan ActionBudget = TimeSpan.FromSeconds(54);
    private readonly TimeSpan _verificationDelay;
    private readonly TimeSpan _retryDelay;
    public AutoStopWorkflow(TimeSpan? verificationDelay = null, TimeSpan? retryDelay = null)
    {
        _verificationDelay = verificationDelay ?? TimeSpan.FromSeconds(12);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    }
    public async Task<AutoStopResult> RunAsync(
        AccelerationSession expected,
        Func<(SessionSnapshot Session, GameSnapshot Game)> observe,
        IPauseAdapter pause,
        IStopAdapter stop,
        CancellationToken ct,
        IReadOnlyList<AccelerationSession>? expectedSessions = null)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(ActionBudget);
        ct = bounded.Token;
        expectedSessions ??= [expected];
        var expectedIds = expectedSessions.Select(s => s.GameId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedKeys = expectedSessions.Select(s => s.GameId + ":" + s.EventTime.Ticks).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        bool SameClient(SessionSnapshot snapshot) => snapshot.Session?.ClientPid == expected.ClientPid && snapshot.Session.ClientStart == expected.ClientStart;
        bool OriginalAccelerating(SessionSnapshot snapshot)
        {
            if (snapshot.Knowledge != SessionKnowledge.Accelerating || !SameClient(snapshot)) return false;
            var sessions = snapshot.ActiveSessions.Count > 0 ? snapshot.ActiveSessions : snapshot.Session is { Active: true } single ? [single] : [];
            var keys = sessions.Select(s => s.GameId + ":" + s.EventTime.Ticks).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            return keys.SequenceEqual(expectedKeys, StringComparer.Ordinal) && sessions.All(s => s.ClientPid == expected.ClientPid && s.ClientStart == expected.ClientStart);
        }
        bool OriginalStopped(SessionSnapshot snapshot) => snapshot.Knowledge == SessionKnowledge.NotAccelerating && SameClient(snapshot) && snapshot.StopStateConfirmed && snapshot.StoppedGameIds.SetEquals(expectedIds);
        // 1 = original sessions still accelerating; 2 = all original IDs have stop-acc/status proof.
        int IdleRoute()
        {
            var (snapshot, game) = observe();
            if (!game.Known || game.Processes.Count != 0) return 0;
            if (OriginalStopped(snapshot)) return 2;
            if (OriginalAccelerating(snapshot)) return 1;
            return 0;
        }

        try
        {
            var pauseRequested = false;
            if (!stop.CanStopAcceleration) return new(false, "stop", "停止加速协议未独立验证；为避免仅暂停时长后遗留自动化状态，本次不发送暂停请求");
            if (IdleRoute() != 1) return new(false, "precheck", "会话或游戏状态已变化");
            var first = await pause.ReadPauseStateAsync(ct);
            if (!first.IsPaused)
            {
                if (!pause.CanControl("")) return new(false, "pause", "当前客户端没有可用的暂停控制参数");
                if (IdleRoute() != 1) return new(false, "pause-precheck", "发送暂停请求前游戏或会话发生变化");
                await pause.RequestPauseAsync(ct);
                pauseRequested = true;
            }
            var stoppedByPause = false;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var route = IdleRoute();
                if (route == 0 || stoppedByPause && route != 2) return new(false, "pause-proof", "暂停请求后会话集合、停止证明或游戏状态发生变化；保留雷神并提示人工检查时长");
                if (route == 2) stoppedByPause = true;
                first = await pause.ReadPauseStateAsync(ct);
                if (first.IsPaused && first.RemainingSeconds.HasValue) break;
                await Task.Delay(_retryDelay, ct);
            }
            if (!first.IsPaused || !first.RemainingSeconds.HasValue) return new(false, "pause-proof", "雷神账号未确认暂停；保留客户端");
            await Task.Delay(_verificationDelay, ct);
            var stableRoute = IdleRoute();
            if (stableRoute == 0 || stoppedByPause && stableRoute != 2) return new(false, "pause-proof", "确认暂停期间会话集合、停止证明或游戏状态发生变化；保留雷神");
            if (stableRoute == 2) stoppedByPause = true;
            var second = await pause.ReadPauseStateAsync(ct);
            if (second.ObservedAt - first.ObservedAt < TimeSpan.FromSeconds(12) || !PauseProofRules.Confirmed(first, second)) return new(false, "pause-proof", "暂停状态或剩余时长未稳定至少 12 秒；保留客户端");
            var stopRoute = IdleRoute();
            if (stopRoute == 0 || stoppedByPause && stopRoute != 2) return new(false, "stop-precheck", "停止加速前会话集合、停止证明或游戏状态发生变化；保留雷神");
            if (stopRoute == 2) stoppedByPause = true;
            var preStopPause = await pause.ReadPauseStateAsync(ct);
            if (!preStopPause.IsPaused || preStopPause.RemainingSeconds is null) return new(false, "stop-precheck", "停止加速前暂停状态已失效；保留雷神");
            var pausedTreeFallback = false;
            if (!stoppedByPause)
            {
                try
                {
                    await stop.RequestStopAccelerationAsync(ct);
                    if (!await stop.IsAccelerationStoppedAsync(ct)) pausedTreeFallback = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    try { pausedTreeFallback = !await stop.IsAccelerationStoppedAsync(ct); }
                    catch { pausedTreeFallback = true; }
                    SafeLog.Write("sdk stop route timed out or failed; considering paused-session tree exit: " + ex.GetType().Name);
                }
            }
            if (pausedTreeFallback) await stop.RequestPausedSessionExitFallbackAsync(ct);
            else await stop.RequestNormalExitAsync(ct);
            if (!await stop.AreOwnedClientProcessesGoneAsync(ct)) return new(false, "exit", "客户端或引擎仍在运行");
            var final = await pause.ReadPauseStateAsync(ct);
            if (!final.IsPaused || !final.RemainingSeconds.HasValue || final.RemainingSeconds < second.RemainingSeconds) return new(false, "final-proof", "退出后无法确认时长仍暂停");
            return new(true, "complete", "时长暂停、加速停止且客户端与引擎均已退出", pauseRequested);
        }
        catch (OperationCanceledException) { return new(false, "cancelled", "操作已取消；检查雷神当前状态"); }
        catch (Exception ex) { return new(false, "error", SafeLog.Redact(ex.GetType().Name + ": " + ex.Message)); }
    }
}

#endif
