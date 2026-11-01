using System.Text.Json.Nodes;

namespace GpmLite.Core.Browser;

/// <summary>
/// A live browser for one profile, independent of engine. <see cref="RunningBrowser"/> is the
/// Chromium/CDP implementation; <see cref="CamoufoxBrowser"/> is the Camoufox/Firefox one.
/// </summary>
public interface IRunningBrowser : IAsyncDisposable
{
    string ProfileId { get; }
    bool IsRunning { get; }
    int ProcessId { get; }

    /// <summary>"host:port" for CDP clients (Selenium/Puppeteer). Null for engines without CDP (Camoufox).</summary>
    string? DebuggerAddress { get; }

    event Action<IRunningBrowser>? Exited;
    /// <summary>Non-fatal problems (an override the browser refused, a relay error...).</summary>
    event Action<string>? Warning;

    Task StopAsync(TimeSpan? grace = null);

    /// <summary>Chromium/CDP only (cookie import/export). Throws <see cref="NotSupportedException"/>
    /// on engines without CDP.</summary>
    Task<JsonObject> SendCdpAsync(string method, object? parameters = null, CancellationToken ct = default);
}
