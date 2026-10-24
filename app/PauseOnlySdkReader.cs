using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LeiGodAutoStop;

// Read-only SDK client. The only command sent is query_nn_acc (method 100009).
public sealed class PauseOnlySdkReader(string installRoot, int clientPid, DateTime clientStart, Func<bool> sameClientWithoutActiveSession)
{
    public enum IdleObservation { NotAccelerating, Accelerating, Unknown }
    private static readonly int[] CandidatePorts = [10908, 5000];
    private readonly string _root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    private static int? _lastLoggedState;

    public async Task<IdleObservation> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!sameClientWithoutActiveSession()) return IdleObservation.Unknown;
        var endpoint = FindOwnedEndpoint();
        if (endpoint is null) return IdleObservation.Unknown;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(endpoint, timeout.Token);
            if (!OwnsListener(endpoint.Port) || !sameClientWithoutActiveSession()) return IdleObservation.Unknown;
            var command = JsonSerializer.Serialize(new { method = 100009, param = new { cmd = "query_nn_acc" }, id = 1 });
            await socket.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Text, true, timeout.Token);
            for (var ignored = 0; ignored < 32; ignored++)
            {
                using var response = JsonDocument.Parse(await ReceiveTextAsync(socket, timeout.Token));
                var root = response.RootElement;
                if (!MatchesReply(root))
                {
                    SafeLog.Write($"sdk query_nn_acc ignored unmatched websocket message port={endpoint.Port} count={ignored + 1}");
                    continue;
                }
                if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var codeNumber) || codeNumber != 200 ||
                    !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("is_onacc", out var onAcc) || onAcc.ValueKind != JsonValueKind.Number || !onAcc.TryGetInt32(out var state) || state is not (0 or 1))
                {
                    SafeLog.Write($"sdk query_nn_acc rejected correlated response port={endpoint.Port}");
                    return IdleObservation.Unknown;
                }
                if (!OwnsListener(endpoint.Port) || !sameClientWithoutActiveSession()) return IdleObservation.Unknown;
                if (_lastLoggedState != state)
                {
                    _lastLoggedState = state;
                    SafeLog.Write($"sdk query_nn_acc confirmed state={state} port={endpoint.Port}");
                }
                return state == 0 ? IdleObservation.NotAccelerating : IdleObservation.Accelerating;
            }
            SafeLog.Write("sdk query_nn_acc received too many unrelated messages");
            return IdleObservation.Unknown;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SafeLog.Write($"sdk query_nn_acc timed out port={endpoint.Port} timeout_seconds=5");
            return IdleObservation.Unknown;
        }
        catch (Exception ex)
        {
            SafeLog.Write("sdk query_nn_acc failed: " + SafeLog.Redact(ex.GetType().Name + ": " + ex.Message));
            return IdleObservation.Unknown;
        }
    }

    internal static bool MatchesReply(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.Number && method.TryGetInt32(out var m) && m == 100009 &&
        root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var i) && i == 1;

    private Uri? FindOwnedEndpoint()
    {
        foreach (var port in CandidatePorts)
            if (OwnsListener(port)) return new Uri($"ws://127.0.0.1:{port}");
        SafeLog.Write("sdk query_nn_acc: no verified local SDK listener");
        return null;
    }

    private bool OwnsListener(int port)
    {
        try
        {
            var sdkPid = PauseOnlyProcessOwnership.ListenerPid(port);
            if (sdkPid <= 0) return false;
            var sdk = PauseOnlyProcessOwnership.Read(sdkPid);
            var client = PauseOnlyProcessOwnership.Read(clientPid);
            if (!sdk.Name.Equals("leishenSdk", StringComparison.OrdinalIgnoreCase) ||
                !client.Name.Equals("leigod", StringComparison.OrdinalIgnoreCase) ||
                client.StartTime != clientStart ||
                !sdk.Path.StartsWith(_root, StringComparison.OrdinalIgnoreCase) ||
                !client.Path.StartsWith(_root, StringComparison.OrdinalIgnoreCase) ||
                sdk.UserSid != PauseOnlyProcessOwnership.CurrentUserSid ||
                client.UserSid != sdk.UserSid ||
                sdk.SessionId != PauseOnlyProcessOwnership.CurrentSessionId ||
                client.SessionId != sdk.SessionId) return false;
            return PauseOnlyProcessOwnership.SdkParentTopologyMatches(sdkPid, PauseOnlyProcessOwnership.ParentPid(sdkPid), sdk.StartTime,
                clientPid, PauseOnlyProcessOwnership.ParentPid(clientPid), clientStart);
        }
        catch { return false; }
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var part = await socket.ReceiveAsync(chunk, ct);
            if (part.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("雷神 SDK 查询连接提前关闭");
            buffer.Write(chunk, 0, part.Count);
            if (buffer.Length > 1_000_000) throw new InvalidOperationException("雷神 SDK 查询响应过大");
            if (part.EndOfMessage) return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}
