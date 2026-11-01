using System.Diagnostics;
using System.Text.Json.Nodes;
using GpmLite.Core.Proxy;

namespace GpmLite.Core.Browser;

/// <summary>
/// A live Camoufox (Firefox) browser for one profile: process + proxy relay. The fingerprint is
/// already baked in via the <c>CAMOU_CONFIG</c> env at launch, so there is no CDP session to manage.
/// </summary>
public sealed class CamoufoxBrowser : IRunningBrowser
{
    private readonly Process _process;
    private readonly ProxyRelay? _relay;
    private int _exitedRaised;

    public string ProfileId { get; }
    public int ProcessId => _process.Id;
    public bool IsRunning => !_process.HasExited;
    /// <summary>Camoufox has no CDP endpoint.</summary>
    public string? DebuggerAddress => null;

    public event Action<IRunningBrowser>? Exited;
    public event Action<string>? Warning;

    internal CamoufoxBrowser(string profileId, Process process, ProxyRelay? relay)
    {
        ProfileId = profileId;
        _process = process;
        _relay = relay;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _ = OnExitedAsync();
        if (_relay is not null) _relay.Error += msg => Warning?.Invoke($"proxy: {msg}");
    }

    public Task<JsonObject> SendCdpAsync(string method, object? parameters = null, CancellationToken ct = default) =>
        throw new NotSupportedException("Camoufox (Firefox) has no CDP endpoint; cookie import/export via CDP is unavailable.");

    public async Task StopAsync(TimeSpan? grace = null)
    {
        if (_process.HasExited) return;
        // Firefox exits cleanly on SIGTERM / CloseMainWindow, flushing the profile.
        try { _process.CloseMainWindow(); } catch (InvalidOperationException) { }
        try { await _process.WaitForExitAsync().WaitAsync(grace ?? TimeSpan.FromSeconds(8)); }
        catch (TimeoutException)
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    private async Task OnExitedAsync()
    {
        if (Interlocked.Exchange(ref _exitedRaised, 1) == 1) return;
        if (_relay is not null) await _relay.DisposeAsync();
        Exited?.Invoke(this);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await OnExitedAsync();
        _process.Dispose();
    }
}
