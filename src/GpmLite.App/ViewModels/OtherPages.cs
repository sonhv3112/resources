using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpmLite.App.Views;
using GpmLite.Core.Browser;
using GpmLite.Core.Models;
using GpmLite.Core.Proxy;
using GpmLite.Core.Services;

namespace GpmLite.App.ViewModels;

public sealed record GroupRow(Group Group, int Count)
{
    public string Name => Group.Name;
}

public partial class GroupsViewModel(ProfileService service, Action onChanged) : ObservableObject
{
    public ObservableCollection<GroupRow> Rows { get; } = [];
    [ObservableProperty] private string _newName = "";

    public void Reload()
    {
        Rows.Clear();
        foreach (var g in service.Repo.ListGroups()) Rows.Add(new GroupRow(g, service.Repo.CountProfiles(g.Id)));
    }

    [RelayCommand]
    private void Add()
    {
        var name = NewName.Trim();
        if (name.Length == 0) return;
        if (Rows.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Dialogs.Notify("Trùng tên", $"Nhóm \"{name}\" đã tồn tại.", error: true);
            return;
        }
        service.Repo.UpsertGroup(new Group { Name = name, SortOrder = Rows.Count });
        NewName = "";
        Reload();
        onChanged();
    }

    [RelayCommand]
    private async Task RenameAsync(GroupRow row)
    {
        var name = await Dialogs.PromptAsync("Đổi tên nhóm", "Tên mới", row.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        row.Group.Name = name.Trim();
        try { service.Repo.UpsertGroup(row.Group); }
        catch (Exception e) { Dialogs.Notify("Lỗi", e.Message, error: true); }
        Reload();
        onChanged();
    }

    [RelayCommand]
    private async Task DeleteAsync(GroupRow row)
    {
        if (!await Dialogs.ConfirmAsync("Xoá nhóm",
                $"Xoá nhóm \"{row.Name}\"? {row.Count} hồ sơ trong nhóm sẽ được giữ lại (chuyển về \"Không có nhóm\").", danger: true))
            return;
        service.Repo.DeleteGroup(row.Group.Id);
        Reload();
        onChanged();
    }
}

public partial class ProxyRow(SavedProxy proxy) : ObservableObject
{
    public SavedProxy Proxy { get; } = proxy;
    public string Raw => Proxy.Raw;
    public bool IsRoute => Proxy.IsRoute;
    [ObservableProperty] private string _status = proxy.LastCheck;
    [ObservableProperty] private bool _isSelected;
}

public partial class ProxiesViewModel : ObservableObject
{
    private readonly ProfileService _service;
    private readonly RouteProxyService _routes;

    public ObservableCollection<ProxyRow> Rows { get; } = [];
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isChecking;

    // ---- route proxy section ----
    public ObservableCollection<RouteProvider> RouteProviders { get; } = [];
    [ObservableProperty] private RouteProvider? _selectedProvider;
    [ObservableProperty] private string _routeApiKey = "";

    public ProxiesViewModel(ProfileService service)
    {
        _service = service;
        _routes = RouteProxyService.Create(
            service.Paths.RouteProvidersDir,
            publicIpProvider: async ct => (await new ProxyChecker(service.Settings.IpLookupUrl).CheckAsync(null, ct: ct)).Ip);
        foreach (var p in _routes.Providers) RouteProviders.Add(p);
        SelectedProvider = RouteProviders.FirstOrDefault();
    }

    public void Reload()
    {
        Rows.Clear();
        foreach (var p in _service.Repo.ListProxies()) Rows.Add(new ProxyRow(p));
    }

    [RelayCommand]
    private void Add()
    {
        var lines = Input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bad = lines.Where(l => !ProxyConfig.TryParse(l, out _, out _)).ToList();
        if (bad.Count > 0)
        {
            Dialogs.Notify("Proxy sai định dạng", string.Join("\n", bad.Take(5)), error: true);
            return;
        }
        var existing = Rows.Select(r => r.Raw).ToHashSet();
        foreach (var l in lines.Where(l => !existing.Contains(l)))
            _service.Repo.UpsertProxy(new SavedProxy { Raw = l });
        Input = "";
        Reload();
    }

    [RelayCommand]
    private async Task CheckAllAsync()
    {
        IsChecking = true;
        try
        {
            using var gate = new SemaphoreSlim(8);
            await Task.WhenAll(Rows.ToList().Select(async row =>
            {
                await gate.WaitAsync();
                try
                {
                    row.Status = "Đang kiểm tra...";
                    var info = row.IsRoute
                        ? await CheckRouteAsync(row)
                        : await _service.CheckProxyAsync(row.Raw);
                    row.Status = $"✓ {info.Summary} · {info.LatencyMs}ms";
                }
                catch (Exception e) { row.Status = $"✗ {e.Message}"; }
                finally { gate.Release(); }
                row.Proxy.LastCheck = row.Status;
                _service.Repo.UpsertProxy(row.Proxy);
            }));
        }
        finally { IsChecking = false; }
    }

    /// <summary>Resolves a route entry to a concrete proxy, remembers it, then checks that proxy's IP.</summary>
    private async Task<IpInfo> CheckRouteAsync(ProxyRow row)
    {
        var cfg = await _routes.ResolveRawAsync(row.Raw);
        row.Proxy.RouteResolved = cfg.ToString();
        return await _service.CheckProxyAsync($"{cfg.Host}:{cfg.Port}"
            + (cfg.HasAuth ? $":{cfg.Username}:{cfg.Password}" : ""));
    }

    [RelayCommand]
    private async Task DeleteAsync(ProxyRow row)
    {
        if (!await Dialogs.ConfirmAsync("Xoá proxy", $"Xoá {row.Raw}?")) return;
        _service.Repo.DeleteProxy(row.Proxy.Id);
        Reload();
    }

    [RelayCommand]
    private Task Copy(ProxyRow row) => Dialogs.CopyAsync(row.Raw);

    // ---- route proxy commands ----

    [RelayCommand]
    private void AddRoute()
    {
        if (SelectedProvider is null) return;
        var key = RouteApiKey.Trim();
        if (key.Length == 0)
        {
            Dialogs.Notify("Thiếu API key", "Nhập API key của provider.", error: true);
            return;
        }
        var raw = RouteProxy.Format(SelectedProvider.Prefix, key);
        if (Rows.Any(r => r.Raw.Equals(raw, StringComparison.OrdinalIgnoreCase)))
        {
            Dialogs.Notify("Trùng", "Route proxy này đã có.", error: true);
            return;
        }
        _service.Repo.UpsertProxy(new SavedProxy { Raw = raw, Note = SelectedProvider.DisplayName });
        RouteApiKey = "";
        Reload();
    }

    [RelayCommand]
    private async Task TestRouteAsync(ProxyRow row)
    {
        if (!row.IsRoute) return;
        row.Status = "Đang phân giải...";
        try
        {
            var cfg = await _routes.ResolveRawAsync(row.Raw);
            row.Proxy.RouteResolved = cfg.ToString();
            var info = await _service.CheckProxyAsync($"{cfg.Host}:{cfg.Port}"
                + (cfg.HasAuth ? $":{cfg.Username}:{cfg.Password}" : ""));
            row.Status = $"✓ {info.Summary} · {info.LatencyMs}ms";
        }
        catch (Exception e) { row.Status = $"✗ {e.Message}"; }
        row.Proxy.LastCheck = row.Status;
        _service.Repo.UpsertProxy(row.Proxy);
    }

    [RelayCommand]
    private async Task ChangeIpAsync(ProxyRow row)
    {
        if (!row.IsRoute || !RouteProxy.TryParse(row.Raw, out var prefix, out var key)) return;
        var provider = _routes.Find(prefix);
        if (provider is null)
        {
            Dialogs.Notify("Không rõ provider", $"Không tìm thấy provider '{prefix}'.", error: true);
            return;
        }
        row.Status = "Đang đổi IP...";
        try
        {
            var cfg = await _routes.ChangeIpAsync(provider, key);
            row.Proxy.RouteResolved = cfg.ToString();
            var info = await _service.CheckProxyAsync($"{cfg.Host}:{cfg.Port}"
                + (cfg.HasAuth ? $":{cfg.Username}:{cfg.Password}" : ""));
            row.Status = $"✓ IP mới: {info.Summary} · {info.LatencyMs}ms";
            Dialogs.Notify("Đã đổi IP", info.Summary);
        }
        catch (Exception e) { row.Status = $"✗ {e.Message}"; Dialogs.Notify("Đổi IP thất bại", e.Message, error: true); }
        row.Proxy.LastCheck = row.Status;
        _service.Repo.UpsertProxy(row.Proxy);
    }
}

public sealed record ExtensionRow(string Name, string Folder);

public partial class ExtensionsViewModel(ProfileService service) : ObservableObject
{
    public ObservableCollection<ExtensionRow> Rows { get; } = [];
    public string Folder => service.Paths.ExtensionsDir;

    public void Reload()
    {
        Rows.Clear();
        if (!Directory.Exists(Folder)) return;
        foreach (var dir in Directory.GetDirectories(Folder).Where(d => File.Exists(Path.Combine(d, "manifest.json"))))
            Rows.Add(new ExtensionRow(ReadName(dir), dir));
    }

    private static string ReadName(string dir)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
            var n = doc.RootElement.TryGetProperty("name", out var v) ? v.GetString() : null;
            return string.IsNullOrWhiteSpace(n) || n.StartsWith("__MSG_") ? Path.GetFileName(dir) : n;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return Path.GetFileName(dir); }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        Process.Start(new ProcessStartInfo { FileName = Folder, UseShellExecute = true });
    }

    [RelayCommand] private void Refresh() => Reload();
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly ProfileService _service;
    private readonly Action _onCoreChanged;

    [ObservableProperty] private string _browserPath;
    [ObservableProperty] private string _detected = "";
    [ObservableProperty] private bool _useCamoufox;
    [ObservableProperty] private string _camoufoxPath;
    [ObservableProperty] private string _pythonPath;
    [ObservableProperty] private string _sidecarPath;
    [ObservableProperty] private string _camoufoxStatus = "";
    [ObservableProperty] private string _ipLookupUrl;
    [ObservableProperty] private bool _localApiEnabled;
    [ObservableProperty] private decimal? _localApiPort;
    [ObservableProperty] private bool _checkProxyOnStart;

    /// <summary>Built-in IP-checker presets shown in the dropdown (last item = custom URL).</summary>
    public ObservableCollection<CheckerServer> CheckerServers { get; } = [];
    [ObservableProperty] private CheckerServer? _selectedChecker;

    public string DataFolder => _service.Paths.Root;

    public SettingsViewModel(ProfileService service, Action onCoreChanged)
    {
        _service = service;
        _onCoreChanged = onCoreChanged;
        var s = service.Settings;
        _browserPath = s.BrowserPath;
        _useCamoufox = s.Engine == BrowserEngine.Camoufox;
        _camoufoxPath = s.CamoufoxPath;
        _pythonPath = s.PythonPath;
        _sidecarPath = s.SidecarPath;
        _ipLookupUrl = s.IpLookupUrl;
        _localApiEnabled = s.LocalApiEnabled;
        _localApiPort = s.LocalApiPort;
        _checkProxyOnStart = s.CheckProxyOnStart;
        foreach (var srv in ProxyChecker.BuiltinServers) CheckerServers.Add(srv);
        _selectedChecker = ProxyChecker.ResolveServer(s.IpLookupUrl) is { Id: "custom" } custom
            ? AddCustom(custom)
            : CheckerServers.FirstOrDefault(c => c.Url.Equals(s.IpLookupUrl, StringComparison.OrdinalIgnoreCase));
        Detect();
    }

    private CheckerServer AddCustom(CheckerServer custom)
    {
        CheckerServers.Add(custom);
        return custom;
    }

    partial void OnSelectedCheckerChanged(CheckerServer? value)
    {
        // Picking a preset fills the URL box; the box stays editable for custom services.
        if (value is not null && value.Id != "custom") IpLookupUrl = value.Url;
    }

    private void Detect()
    {
        BrowserCore? core = ChromeLocator.Find(BrowserPath);
        Detected = core is null ? "Không tìm thấy trình duyệt hợp lệ" : $"{core.ExecutablePath}  —  phiên bản {core.Version}";

        var binary = string.IsNullOrWhiteSpace(CamoufoxPath) ? ChromeLocator.DefaultCamoufoxPath() : CamoufoxPath;
        var script = string.IsNullOrWhiteSpace(SidecarPath) ? ChromeLocator.DefaultSidecarPath() : SidecarPath;
        var binOk = binary is not null && File.Exists(binary);
        var scrOk = script is not null && File.Exists(script);
        CamoufoxStatus = (binOk, scrOk) switch
        {
            (true, true) => $"Camoufox: {binary}\nSidecar: {script}",
            (false, _) => "Chưa tìm thấy binary Camoufox — hãy chọn đường dẫn bên dưới.",
            (_, false) => "Chưa tìm thấy sidecar camoufox_sidecar.py — hãy chọn đường dẫn bên dưới.",
        };
    }

    partial void OnUseCamoufoxChanged(bool value) => Detect();
    partial void OnCamoufoxPathChanged(string value) => Detect();
    partial void OnSidecarPathChanged(string value) => Detect();

    [RelayCommand]
    private void Save()
    {
        var s = _service.Settings;
        var apiChanged = s.LocalApiEnabled != LocalApiEnabled || s.LocalApiPort != (int)(LocalApiPort ?? 19995);
        s.BrowserPath = BrowserPath.Trim();
        s.Engine = UseCamoufox ? BrowserEngine.Camoufox : BrowserEngine.Chromium;
        s.CamoufoxPath = CamoufoxPath.Trim();
        s.PythonPath = PythonPath.Trim();
        s.SidecarPath = SidecarPath.Trim();
        s.IpLookupUrl = string.IsNullOrWhiteSpace(IpLookupUrl) ? ProxyChecker.DefaultLookupUrl : IpLookupUrl.Trim();
        s.LocalApiEnabled = LocalApiEnabled;
        s.LocalApiPort = (int)(LocalApiPort ?? 19995);
        s.CheckProxyOnStart = CheckProxyOnStart;
        s.Save(_service.Paths.SettingsFile);
        Detect();
        _onCoreChanged();
        Dialogs.Notify("Đã lưu cài đặt", apiChanged ? "Khởi động lại ứng dụng để áp dụng thay đổi API." : "");
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await Dialogs.PickFileAsync("Chọn file chạy Chrome/Chromium");
        if (path is null) return;
        // On macOS the user usually picks the .app bundle; resolve to the executable inside it.
        if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path))
        {
            var macos = Path.Combine(path, "Contents", "MacOS");
            var exe = Directory.Exists(macos) ? Directory.GetFiles(macos).FirstOrDefault() : null;
            if (exe is not null) path = exe;
        }
        BrowserPath = path;
        Detect();
    }

    [RelayCommand]
    private async Task BrowseCamoufoxAsync()
    {
        var path = await Dialogs.PickFileAsync("Chọn Camoufox (Camoufox.app hoặc camoufox.exe)");
        if (path is null) return;
        if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path))
        {
            var macos = Path.Combine(path, "Contents", "MacOS");
            var exe = Directory.Exists(macos)
                ? Directory.GetFiles(macos).FirstOrDefault(f => Path.GetFileName(f).Contains("camoufox", StringComparison.OrdinalIgnoreCase))
                  ?? Directory.GetFiles(macos).FirstOrDefault()
                : null;
            if (exe is not null) path = exe;
        }
        CamoufoxPath = path;
    }

    [RelayCommand]
    private async Task BrowsePythonAsync()
    {
        var path = await Dialogs.PickFileAsync("Chọn Python có cài package camoufox");
        if (path is not null) PythonPath = path;
    }

    [RelayCommand]
    private async Task BrowseSidecarAsync()
    {
        var path = await Dialogs.PickFileAsync("Chọn camoufox_sidecar.py");
        if (path is not null) SidecarPath = path;
    }

    [RelayCommand]
    private void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo { FileName = DataFolder, UseShellExecute = true });
}
