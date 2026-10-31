using System.Net;
using System.Text;
using System.Text.Json;
using GpmLite.Core.Browser;
using GpmLite.Core.Fingerprint;

namespace GpmLite.Core.Services;

/// <summary>
/// Local automation API on 127.0.0.1 only. Typical use: start a profile, then attach Selenium
/// (debuggerAddress) or Puppeteer/Playwright (connectOverCDP) to the returned address.
///
/// GET  /api/v1/profiles?group_id=&amp;search=
/// GET  /api/v1/profiles/{id}
/// POST /api/v1/profiles            {"name","group_id","proxy","os":"Windows|MacOs|Linux","note","startup_urls"}
/// GET|POST /api/v1/profiles/{id}/start  -> {"debugger_address","ws_endpoint","browser_path"}
/// GET|POST /api/v1/profiles/{id}/stop
/// DELETE /api/v1/profiles/{id}
/// GET  /api/v1/groups
/// </summary>
public sealed class LocalApiServer(ProfileService service) : IAsyncDisposable
{
    private HttpListener? _listener;
    private Task? _loop;
    private readonly CancellationTokenSource _cts = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public string? Prefix { get; private set; }

    public void Start(int port)
    {
        Prefix = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(Prefix);
        _listener.Start();
        _loop = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { break; }
            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            // Reject cross-site browser requests: only non-browser clients or same-host pages may call us.
            if (!IsAllowedOrigin(ctx.Request.Headers["Origin"]))
            {
                await WriteAsync(ctx, 403, new { success = false, message = "Forbidden origin" });
                return;
            }

            var (status, body) = await RouteAsync(ctx.Request);
            await WriteAsync(ctx, status, body);
        }
        catch (KeyNotFoundException e) { await WriteAsync(ctx, 404, new { success = false, message = e.Message }); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or LaunchException or JsonException)
        {
            await WriteAsync(ctx, 400, new { success = false, message = e.Message });
        }
        catch (Exception e) { await WriteAsync(ctx, 500, new { success = false, message = e.Message }); }
    }

    /// <summary>No Origin (curl, Selenium scripts) or an exact loopback host. Prefix matching would let
    /// "http://127.0.0.1.evil.com" through.</summary>
    internal static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        return u.Host is "127.0.0.1" or "localhost" or "[::1]" or "::1";
    }

    private async Task<(int, object)> RouteAsync(HttpListenerRequest req)
    {
        var path = req.Url!.AbsolutePath.TrimEnd('/');
        var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // api v1 profiles {id} {action}
        var method = req.HttpMethod;
        if (seg.Length < 3 || seg[0] != "api" || seg[1] != "v1") return (404, new { success = false, message = "Not found" });

        switch (seg[2])
        {
            case "groups" when method == "GET" && seg.Length == 3:
                return (200, new { success = true, data = service.Repo.ListGroups() });

            case "profiles" when seg.Length == 3 && method == "GET":
                var list = service.Repo.ListProfiles(req.QueryString["group_id"], req.QueryString["search"])
                    .Select(ToDto).ToList();
                return (200, new { success = true, data = list });

            case "profiles" when seg.Length == 3 && method == "POST":
                var input = await ReadBodyAsync<CreateProfileInput>(req);
                TargetOs? os = Enum.TryParse<TargetOs>(input.Os, true, out var o) ? o : null;
                var created = service.Create(input.Name ?? "", input.GroupId, input.Proxy ?? "", os,
                    note: input.Note ?? "", startupUrls: input.StartupUrls ?? "");
                return (201, new { success = true, data = ToDto(created) });

            case "profiles" when seg.Length == 4 && method == "GET":
                var p = service.Repo.GetProfile(seg[3]) ?? throw new KeyNotFoundException("Profile not found");
                return (200, new { success = true, data = ToDto(p) });

            case "profiles" when seg.Length == 4 && method == "DELETE":
                if (service.Repo.GetProfile(seg[3]) is null) throw new KeyNotFoundException("Profile not found");
                service.Delete(seg[3]);
                return (200, new { success = true });

            case "profiles" when seg.Length == 5 && seg[4] == "start" && method is "POST" or "GET":
                var rb = await service.StartAsync(seg[3]);
                return (200, new
                {
                    success = true,
                    data = new
                    {
                        profile_id = rb.ProfileId,
                        debugger_address = rb.DebuggerAddress,
                        ws_endpoint = rb.BrowserWebSocketUrl.ToString(),
                        browser_path = service.RequireCore().ExecutablePath,
                    },
                });

            case "profiles" when seg.Length == 5 && seg[4] == "stop" && method is "POST" or "GET":
                await service.StopAsync(seg[3]);
                return (200, new { success = true });
        }
        return (404, new { success = false, message = "Not found" });
    }

    private object ToDto(Models.Profile p) => new
    {
        id = p.Id,
        name = p.Name,
        group_id = p.GroupId,
        proxy = p.Proxy,
        note = p.Note,
        running = service.IsRunning(p.Id),
        debugger_address = service.GetRunning(p.Id)?.DebuggerAddress,
        created_at = p.CreatedAt,
        last_run_at = p.LastRunAt,
    };

    private sealed class CreateProfileInput
    {
        public string? Name { get; set; }
        public string? GroupId { get; set; }
        public string? Proxy { get; set; }
        public string? Os { get; set; }
        public string? Note { get; set; }
        public string? StartupUrls { get; set; }
    }

    private static async Task<T> ReadBodyAsync<T>(HttpListenerRequest req) where T : new()
    {
        using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        var text = await r.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(text) ? new T() : JsonSerializer.Deserialize<T>(text, Json) ?? new T();
    }

    private static async Task WriteAsync(HttpListenerContext ctx, int status, object body)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        if (_loop is not null) { try { await _loop; } catch { } }
        _cts.Dispose();
    }
}
