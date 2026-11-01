using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpmLite.App.Views;
using GpmLite.Core.Fingerprint;
using GpmLite.Core.Models;
using GpmLite.Core.Proxy;
using GpmLite.Core.Services;

namespace GpmLite.App.ViewModels;

public sealed record GroupOption(string? Id, string Name)
{
    public override string ToString() => Name;
}

public partial class ProfilesViewModel : ObservableObject
{
    private readonly ProfileService _service;

    public ObservableCollection<ProfileRow> Rows { get; } = [];
    public ObservableCollection<GroupOption> GroupFilter { get; } = [];

    [ObservableProperty] private GroupOption? _selectedGroup;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _allSelected;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _summary = "";

    public bool HasSelection => SelectedCount > 0;

    public ProfilesViewModel(ProfileService service)
    {
        _service = service;
        service.StatusChanged += s => Dispatcher.UIThread.Post(() => OnStatus(s));
        service.Log += (id, msg) => Dispatcher.UIThread.Post(() =>
        {
            var row = Rows.FirstOrDefault(r => r.Id == id);
            if (row is not null) row.LastMessage = msg;
        });
        ReloadGroups();
        Reload();
    }

    private bool _suppressReload;

    partial void OnSelectedGroupChanged(GroupOption? value)
    {
        if (!_suppressReload) Reload();
    }
    partial void OnSearchTextChanged(string value) => Reload();
    partial void OnAllSelectedChanged(bool value)
    {
        foreach (var r in Rows) r.IsSelected = value;
    }

    public void ReloadGroups()
    {
        var keep = SelectedGroup?.Id;
        GroupFilter.Clear();
        GroupFilter.Add(new GroupOption(null, "Tất cả nhóm"));
        foreach (var g in _service.Repo.ListGroups()) GroupFilter.Add(new GroupOption(g.Id, g.Name));
        _suppressReload = true;
        try { SelectedGroup = GroupFilter.FirstOrDefault(g => g.Id == keep) ?? GroupFilter[0]; }
        finally { _suppressReload = false; }
    }

    public void Reload()
    {
        var groups = _service.Repo.ListGroups().ToDictionary(g => g.Id, g => g.Name);
        var tags = _service.Repo.TagsByProfile();
        var profiles = _service.Repo.ListProfiles(SelectedGroup?.Id, SearchText);
        var selected = Rows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();

        foreach (var r in Rows) r.PropertyChanged -= RowChanged;
        Rows.Clear();
        var i = 1;
        foreach (var p in profiles)
        {
            var row = new ProfileRow(this, p, i++)
            {
                GroupName = p.GroupId is not null && groups.TryGetValue(p.GroupId, out var gn) ? gn : "",
                TagsText = tags.TryGetValue(p.Id, out var t) ? string.Join(", ", t.Select(x => x.Name)) : "",
                IsSelected = selected.Contains(p.Id),
            };
            var rb = _service.GetRunning(p.Id);
            row.SetState(rb is not null ? ProfileRunState.Running : ProfileRunState.Stopped, rb?.DebuggerAddress);
            row.PropertyChanged += RowChanged;
            Rows.Add(row);
        }
        UpdateSelection();
        Summary = $"{Rows.Count} hồ sơ" + (SelectedGroup?.Id is null ? "" : $" trong \"{SelectedGroup.Name}\"");
    }

    private void RowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileRow.IsSelected)) UpdateSelection();
    }

    private void UpdateSelection()
    {
        SelectedCount = Rows.Count(r => r.IsSelected);
        OnPropertyChanged(nameof(HasSelection));
    }

    private void OnStatus(ProfileStatus s)
    {
        var row = Rows.FirstOrDefault(r => r.Id == s.ProfileId);
        if (row is null) return;
        row.SetState(s.State, s.DebuggerAddress);
        if (s.Message is not null && s.State == ProfileRunState.Stopped)
        {
            row.LastMessage = s.Message;
            Dialogs.Notify("Không mở được hồ sơ", $"{row.Name}: {s.Message}", error: true);
        }
        if (s.State == ProfileRunState.Running || s.State == ProfileRunState.Stopped)
        {
            // last-run time and proxy status are updated by the service; refresh just this row
            var p = _service.Repo.GetProfile(s.ProfileId);
            if (p is not null) row.Refresh(p);
        }
    }

    private List<ProfileRow> Selected => Rows.Where(r => r.IsSelected).ToList();

    // ---------- toolbar ----------
    [RelayCommand]
    private async Task CreateAsync()
    {
        try
        {
            var core = _service.RequireCore();
            var editor = ProfileEditorViewModel.ForNew(_service, core.Version, SelectedGroup?.Id);
            if (await Dialogs.EditProfileAsync(editor) is not true) return;
            var p = _service.Create(editor.Name, editor.GroupId, editor.Proxy, editor.Os, editor.BuildFingerprint(),
                editor.Note, editor.StartupUrls);
            Dialogs.Notify("Đã tạo hồ sơ", p.Name);
            Reload();
        }
        catch (Exception e) { Dialogs.Notify("Lỗi", e.Message, error: true); }
    }

    [RelayCommand]
    private async Task BulkCreateAsync()
    {
        var vm = new BulkCreateViewModel(_service.Repo.ListGroups(), SelectedGroup?.Id);
        if (await Dialogs.BulkCreateAsync(vm) is not true) return;
        try
        {
            var proxies = vm.ProxyLines();
            foreach (var px in proxies)
                if (!ProxyConfig.TryParse(px, out _, out var err)) throw new ArgumentException($"Proxy \"{px}\": {err}");
            var created = await Task.Run(() => _service.CreateBulk(vm.Prefix, vm.Count, vm.SelectedGroup?.Id, proxies, vm.Os));
            Dialogs.Notify("Tạo hàng loạt", $"Đã tạo {created.Count} hồ sơ");
            Reload();
        }
        catch (Exception e) { Dialogs.Notify("Lỗi", e.Message, error: true); }
    }

    [RelayCommand]
    private async Task ImportExcelAsync()
    {
        var path = await PickFileAsync("Nhập hồ sơ từ Excel/CSV",
            new FilePickerFileType("Excel / CSV") { Patterns = ["*.xlsx", "*.csv"] });
        if (path is null) return;
        try
        {
            var result = await Task.Run(() => ProfileImportExport.Import(_service, path));
            var msg = $"Đã nhập {result.Imported} hồ sơ"
                      + (result.Skipped > 0 ? $", bỏ qua {result.Skipped} dòng" : "");
            Dialogs.Notify("Nhập hồ sơ", msg, error: result.Imported == 0);
            if (result.Errors.Count > 0)
                Dialogs.Notify("Dòng bị bỏ qua", string.Join("\n", result.Errors.Take(8)), error: true);
            ReloadGroups();
            Reload();
        }
        catch (Exception e) { Dialogs.Notify("Lỗi", e.Message, error: true); }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        var path = await PickFileAsync("Khôi phục hồ sơ",
            new FilePickerFileType("Bản sao lưu (.zip)") { Patterns = ["*.zip"] });
        if (path is null) return;
        try
        {
            var p = await Task.Run(() => BackupService.Restore(_service, path));
            Dialogs.Notify("Khôi phục", $"Đã khôi phục \"{p.Name}\".");
            ReloadGroups();
            Reload();
        }
        catch (Exception e) { Dialogs.Notify("Lỗi", e.Message, error: true); }
    }

    [RelayCommand] private void Refresh() => Reload();

    [RelayCommand]
    private void StartSelected()
    {
        foreach (var r in Selected.Where(r => r.State == ProfileRunState.Stopped)) _ = StartRowAsync(r);
    }

    [RelayCommand]
    private void StopSelected()
    {
        foreach (var r in Selected.Where(r => r.State == ProfileRunState.Running)) _ = _service.StopAsync(r.Id);
    }

    [RelayCommand]
    private async Task RandomizeSelectedAsync()
    {
        var rows = Selected;
        if (rows.Count == 0) return;
        if (!await Dialogs.ConfirmAsync("Đổi fingerprint",
                $"Tạo fingerprint ngẫu nhiên mới cho {rows.Count} hồ sơ? Các website đã đăng nhập có thể coi đây là thiết bị mới."))
            return;
        try
        {
            _service.RandomizeFingerprints(rows.Select(r => r.Id));
            Dialogs.Notify("Đã đổi fingerprint", $"{rows.Count} hồ sơ");
        }
        catch (Exception e) { Dialogs.Notify("Lỗi", e.Message, error: true); }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var rows = Selected;
        if (rows.Count == 0) return;
        if (rows.Any(r => r.State != ProfileRunState.Stopped))
        {
            Dialogs.Notify("Không thể xoá", "Hãy đóng các hồ sơ đang chạy trước.", error: true);
            return;
        }
        if (!await Dialogs.ConfirmAsync("Xoá hồ sơ",
                $"Xoá vĩnh viễn {rows.Count} hồ sơ cùng toàn bộ dữ liệu trình duyệt (cookie, lịch sử...)?", danger: true))
            return;
        foreach (var r in rows) _service.Delete(r.Id);
        AllSelected = false;
        Reload();
    }

    [RelayCommand]
    private async Task MoveSelectedAsync()
    {
        var rows = Selected;
        if (rows.Count == 0) return;
        var groups = _service.Repo.ListGroups();
        var choice = await Dialogs.PickGroupAsync(groups);
        if (choice is null) return;
        foreach (var r in rows)
        {
            var p = _service.Repo.GetProfile(r.Id);
            if (p is null) continue;
            p.GroupId = choice.Id;
            _service.Update(p);
        }
        Reload();
    }

    // ---------- row actions ----------
    internal async Task StartRowAsync(ProfileRow row)
    {
        try { await _service.StartAsync(row.Id); }
        catch (Exception) { /* surfaced through StatusChanged */ }
    }

    internal Task StopRowAsync(ProfileRow row) => _service.StopAsync(row.Id);

    internal async Task EditRowAsync(ProfileRow row)
    {
        var p = _service.Repo.GetProfile(row.Id);
        if (p is null) return;
        try
        {
            var core = _service.RequireCore();
            var editor = ProfileEditorViewModel.ForExisting(_service, core.Version, p);
            if (await Dialogs.EditProfileAsync(editor) is not true) return;
            p.Name = editor.Name;
            p.GroupId = editor.GroupId;
            p.Proxy = editor.Proxy;
            p.Note = editor.Note;
            p.StartupUrls = editor.StartupUrls;
            p.FingerprintJson = editor.BuildFingerprint().ToJson();
            if (editor.ProxyStatus is not null) p.ProxyStatus = editor.ProxyStatus;
            _service.Update(p);
            if (row.State == ProfileRunState.Running)
                Dialogs.Notify("Đã lưu", "Thay đổi có hiệu lực từ lần mở hồ sơ tiếp theo.");
            Reload();
        }
        catch (Exception e) { Dialogs.Notify("Lỗi", e.Message, error: true); }
    }

    internal void CloneRow(ProfileRow row)
    {
        try
        {
            var c = _service.Clone(row.Id);
            Dialogs.Notify("Đã nhân bản", c.Name);
            Reload();
        }
        catch (Exception e) { Dialogs.Notify("Lỗi", e.Message, error: true); }
    }

    internal async Task DeleteRowAsync(ProfileRow row)
    {
        if (row.State != ProfileRunState.Stopped)
        {
            Dialogs.Notify("Không thể xoá", "Hãy đóng hồ sơ trước.", error: true);
            return;
        }
        if (!await Dialogs.ConfirmAsync("Xoá hồ sơ", $"Xoá vĩnh viễn \"{row.Name}\" cùng dữ liệu trình duyệt?", danger: true))
            return;
        _service.Delete(row.Id);
        Reload();
    }

    internal async Task CheckProxyRowAsync(ProfileRow row)
    {
        var p = _service.Repo.GetProfile(row.Id);
        if (p is null) return;
        if (string.IsNullOrWhiteSpace(p.Proxy))
        {
            Dialogs.Notify("Proxy", "Hồ sơ này không dùng proxy.");
            return;
        }
        row.ProxyStatus = "Đang kiểm tra...";
        try
        {
            var info = await _service.CheckProxyAsync(p.Proxy);
            p.ProxyStatus = info.Summary;
            _service.Update(p);
            row.ProxyStatus = $"{info.Summary} · {info.LatencyMs}ms";
        }
        catch (Exception e)
        {
            row.ProxyStatus = "Lỗi";
            Dialogs.Notify("Proxy lỗi", $"{row.Name}: {e.Message}", error: true);
        }
    }

    /// <summary>Dev snapshot helper: editor for an existing profile without opening it modally.</summary>
    internal ProfileEditorViewModel? CreateEditorForSnapshot(string id)
    {
        var p = _service.Repo.GetProfile(id);
        var core = Core.Browser.ChromeLocator.Find(_service.Settings.BrowserPath);
        return p is null || core is null ? null : ProfileEditorViewModel.ForExisting(_service, core.Version, p);
    }

    internal void CopyDebugger(ProfileRow row)
    {
        if (row.DebuggerAddress is { } addr) _ = Dialogs.CopyAsync(addr);
    }

    // ---------- cookie / backup row actions ----------
    internal async Task ExportCookieRowAsync(ProfileRow row)
    {
        // Engine-aware: Camoufox reads cookies.sqlite (works any time); Chromium uses CDP (needs the profile open).
        var path = await SaveFileAsync("Xuất cookie", $"{Sanitize(row.Name)}-cookies.json", "json",
            new FilePickerFileType("JSON") { Patterns = ["*.json"] });
        if (path is null) return;
        try
        {
            var n = await _service.ExportCookiesAsync(row.Id, path);
            Dialogs.Notify("Xuất cookie", $"Đã xuất {n} cookie.");
        }
        catch (Exception e) { Dialogs.Notify("Lỗi", e.Message, error: true); }
    }

    internal async Task ImportCookieRowAsync(ProfileRow row)
    {
        // Camoufox writes cookies.sqlite (profile must be CLOSED); Chromium uses CDP (needs the profile open).
        var path = await PickFileAsync("Nhập cookie", new FilePickerFileType("JSON") { Patterns = ["*.json"] });
        if (path is null) return;
        try
        {
            var n = await _service.ImportCookiesAsync(row.Id, path);
            Dialogs.Notify("Nhập cookie", $"Đã nhập {n} cookie.");
        }
        catch (Exception e) { Dialogs.Notify("Lỗi", e.Message, error: true); }
    }

    internal async Task BackupRowAsync(ProfileRow row)
    {
        var path = await SaveFileAsync("Sao lưu hồ sơ", $"{Sanitize(row.Name)}.zip", "zip",
            new FilePickerFileType("Zip") { Patterns = ["*.zip"] });
        if (path is null) return;
        try
        {
            await Task.Run(() => BackupService.Backup(_service, row.Id, path));
            Dialogs.Notify("Sao lưu", $"Đã sao lưu \"{row.Name}\".");
        }
        catch (Exception e) { Dialogs.Notify("Lỗi", e.Message, error: true); }
    }

    // ---------- file pickers ----------
    private static async Task<string?> PickFileAsync(string title, FilePickerFileType filter)
    {
        if (Dialogs.Owner is not { } owner) return null;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [filter],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private static async Task<string?> SaveFileAsync(string title, string suggestedName, string ext, FilePickerFileType filter)
    {
        if (Dialogs.Owner is not { } owner) return null;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = ext,
            FileTypeChoices = [filter],
        });
        return file?.TryGetLocalPath();
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "profile" : cleaned;
    }
}

public partial class ProfileRow : ObservableObject
{
    private readonly ProfilesViewModel _owner;

    public string Id { get; }
    public int Index { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _groupName = "";
    [ObservableProperty] private string _tagsText = "";
    [ObservableProperty] private string _proxyDisplay;
    [ObservableProperty] private string _proxyStatus;
    [ObservableProperty] private string _note;
    [ObservableProperty] private string _osText;
    [ObservableProperty] private string _lastRun;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ProfileRunState _state;
    [ObservableProperty] private string? _debuggerAddress;
    [ObservableProperty] private string _lastMessage = "";

    public bool IsRunning => State == ProfileRunState.Running;
    public bool IsBusy => State is ProfileRunState.Starting or ProfileRunState.Stopping;
    public string RunText => State switch
    {
        ProfileRunState.Starting => "Đang mở",
        ProfileRunState.Running => "Đóng",
        ProfileRunState.Stopping => "Đang đóng",
        _ => "Mở",
    };
    public string StatusText => State switch
    {
        ProfileRunState.Running => "● Đang chạy",
        ProfileRunState.Starting => "◌ Đang mở",
        ProfileRunState.Stopping => "◌ Đang đóng",
        _ => "",
    };

    public ProfileRow(ProfilesViewModel owner, Profile p, int index)
    {
        _owner = owner;
        Id = p.Id;
        Index = index;
        _name = p.Name;
        _note = p.Note;
        _proxyDisplay = "";
        _proxyStatus = "";
        _osText = "";
        _lastRun = "";
        Refresh(p);
    }

    public void Refresh(Profile p)
    {
        Name = p.Name;
        Note = p.Note;
        ProxyDisplay = MaskProxy(p.Proxy);
        ProxyStatus = p.ProxyStatus;
        var fp = FingerprintConfig.FromJson(p.FingerprintJson);
        OsText = fp.Os switch { TargetOs.MacOs => "macOS", TargetOs.Linux => "Linux", _ => "Windows" };
        LastRun = p.LastRunAt is { } t ? t.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "—";
    }

    public void SetState(ProfileRunState state, string? debugger)
    {
        State = state;
        DebuggerAddress = state == ProfileRunState.Running ? debugger : null;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(RunText));
        OnPropertyChanged(nameof(StatusText));
    }

    private static string MaskProxy(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Không dùng proxy";
        if (!ProxyConfig.TryParse(raw, out var cfg, out _) || cfg is null) return "Proxy không hợp lệ";
        var scheme = cfg.Type switch { ProxyType.Socks5 => "SOCKS5", ProxyType.Socks4 => "SOCKS4", _ => "HTTP" };
        return $"{scheme} {cfg.Host}:{cfg.Port}";
    }

    [RelayCommand]
    private Task ToggleRun() => State switch
    {
        ProfileRunState.Stopped => _owner.StartRowAsync(this),
        ProfileRunState.Running => _owner.StopRowAsync(this),
        _ => Task.CompletedTask,
    };

    [RelayCommand] private Task Edit() => _owner.EditRowAsync(this);
    [RelayCommand] private void Clone() => _owner.CloneRow(this);
    [RelayCommand] private Task Delete() => _owner.DeleteRowAsync(this);
    [RelayCommand] private Task CheckProxy() => _owner.CheckProxyRowAsync(this);
    [RelayCommand] private void CopyDebugger() => _owner.CopyDebugger(this);
    [RelayCommand] private Task ExportCookie() => _owner.ExportCookieRowAsync(this);
    [RelayCommand] private Task ImportCookie() => _owner.ImportCookieRowAsync(this);
    [RelayCommand] private Task Backup() => _owner.BackupRowAsync(this);
}
