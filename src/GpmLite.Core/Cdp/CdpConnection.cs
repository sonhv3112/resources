using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GpmLite.Core.Cdp;

public sealed record CdpEvent(string Method, JsonObject Params, string? SessionId);

public sealed class CdpException(string method, string message) : Exception($"{method}: {message}");

/// <summary>
/// Minimal Chrome DevTools Protocol client over the browser-level WebSocket, using flattened sessions
/// (every command/event carries an optional sessionId).
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private int _nextId;

    private static readonly JsonSerializerOptions ParamOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Raised on the receive loop for every protocol event. Handlers must not block.</summary>
    public event Action<CdpEvent>? EventReceived;
    public event Action? Closed;

    public static async Task<CdpConnection> ConnectAsync(Uri browserWsUrl, CancellationToken ct = default)
    {
        var c = new CdpConnection();
        c._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await c._ws.ConnectAsync(browserWsUrl, ct);
        c._receiveLoop = c.ReceiveLoopAsync();
        return c;
    }

    public async Task<JsonObject> SendAsync(string method, object? parameters = null, string? sessionId = null,
        CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var msg = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters is null ? new JsonObject() : JsonSerializer.SerializeToNode(parameters, ParamOpts),
        };
        if (sessionId is not null) msg["sessionId"] = sessionId;

        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        finally { _sendLock.Release(); }

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        var resp = await tcs.Task;
        if (resp["error"] is JsonObject err)
            throw new CdpException(method, err["message"]?.GetValue<string>() ?? err.ToJsonString());
        return resp["result"] as JsonObject ?? new JsonObject();
    }

    /// <summary>Fire-and-forget send that swallows protocol errors (for best-effort overrides).</summary>
    public async Task<bool> TrySendAsync(string method, object? parameters = null, string? sessionId = null)
    {
        try
        {
            await SendAsync(method, parameters, sessionId);
            return true;
        }
        catch (Exception e) when (e is CdpException or WebSocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1 << 16];
        var ms = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                JsonObject? msg;
                try { msg = JsonNode.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length)) as JsonObject; }
                catch (JsonException) { continue; }
                if (msg is null) continue;

                if (msg["id"] is JsonValue idv && idv.TryGetValue<int>(out var id))
                {
                    if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(msg);
                }
                else if (msg["method"]?.GetValue<string>() is { } method)
                {
                    var ev = new CdpEvent(method, msg["params"] as JsonObject ?? new JsonObject(),
                        msg["sessionId"]?.GetValue<string>());
                    try { EventReceived?.Invoke(ev); }
                    catch { /* a faulty handler must not kill the loop */ }
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            foreach (var kv in _pending)
                kv.Value.TrySetException(new WebSocketException("CDP connection closed"));
            _pending.Clear();
            Closed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { /* best effort */ }
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        _ws.Dispose();
        _cts.Dispose();
    }
}
