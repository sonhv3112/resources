using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpmLite.Core.Fingerprint;
using GpmLite.Core.Models;
using GpmLite.Core.Proxy;
using GpmLite.Core.Services;

namespace GpmLite.App.ViewModels;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Proxy choices shown in the editor (adds a "None" to <see cref="GpmLite.Core.Proxy.ProxyType"/>).</summary>
public enum ProxyKind { None, Http, Https, Socks4, Socks5 }

public partial class ProfileEditorViewModel : ObservableObject
{
    private readonly ProfileService _service;
    private readonly string _coreVersion;
    private FingerprintConfig _fp;

    public string Title { get; }
    public List<GroupOption> Groups { get; }

    public static List<Option<TargetOs>> OsOptions { get; } =
    [
        new(TargetOs.Windows, "Windows"), new(TargetOs.MacOs, "macOS"), new(TargetOs.Linux, "Linux"),
    ];
    public static List<Option<NoiseMode>> NoiseOptions { get; } =
    [
        new(NoiseMode.Noise, "Nhiễu (khuyên dùng)"), new(NoiseMode.Real, "Thật"),
    ];
    public static List<Option<AutoMode>> AutoOptions { get; } =
    [
        new(AutoMode.Auto, "Dựa theo IP proxy"), new(AutoMode.Custom, "Tuỳ chỉnh"), new(AutoMode.Real, "Theo máy thật"),
    ];
    public static List<Option<WebRtcMode>> WebRtcOptions { get; } =
    [
        new(WebRtcMode.Altered, "Thay bằng IP proxy"), new(WebRtcMode.Disabled, "Tắt WebRTC"), new(WebRtcMode.Real, "Theo máy thật"),
    ];

    // ---- General ----
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private GroupOption? _selectedGroup;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _startupUrls = "";
    [ObservableProperty] private Option<TargetOs> _selectedOs;

    // ---- Proxy (GPM-style structured fields) ----
    public static List<Option<ProxyKind>> ProxyKindOptions { get; } =
    [
        new(ProxyKind.None, "Không dùng proxy (kết nối trực tiếp)"),
        new(ProxyKind.Http, "HTTP"),
        new(ProxyKind.Https, "HTTPS"),
        new(ProxyKind.Socks4, "SOCKS4"),
        new(ProxyKind.Socks5, "SOCKS5"),
    ];
    [ObservableProperty] private Option<ProxyKind> _selectedProxyKind = ProxyKindOptions[0];
    [ObservableProperty] private string _proxyHost = "";
    [ObservableProperty] private string _proxyPort = "";
    [ObservableProperty] private string _proxyUsername = "";
    [ObservableProperty] private string _proxyPassword = "";
    [ObservableProperty] private string _proxyCheckResult = "";
    [ObservableProperty] private bool _isCheckingProxy;
    public string? ProxyStatus { get; private set; }

    public bool IsProxyEnabled => SelectedProxyKind.Value != ProxyKind.None;
    partial void OnSelectedProxyKindChanged(Option<ProxyKind> value)
    {
        OnPropertyChanged(nameof(IsProxyEnabled));
        ProxyCheckResult = "";
    }

    /// <summary>The combined proxy string the backend stores/parses (empty = direct).</summary>
    public string Proxy => BuildProxyString();

    private string BuildProxyString()
    {
        if (SelectedProxyKind.Value == ProxyKind.None) return "";
        var scheme = SelectedProxyKind.Value switch
        {
            ProxyKind.Https => "https",
            ProxyKind.Socks4 => "socks4",
            ProxyKind.Socks5 => "socks5",
            _ => "http",
        };
        var host = ProxyHost.Trim();
        var port = ProxyPort.Trim();
        if (host.Length == 0 || port.Length == 0) return $"{scheme}://{host}:{port}"; // let Validate flag it
        var cred = !string.IsNullOrEmpty(ProxyUsername)
            ? $"{Uri.EscapeDataString(ProxyUsername)}:{Uri.EscapeDataString(ProxyPassword ?? "")}@"
            : "";
        return $"{scheme}://{cred}{host}:{port}";
    }

    private void LoadProxy(string? raw)
    {
        if (!ProxyConfig.TryParse(raw, out var cfg, out _) || cfg is null)
        {
            SelectedProxyKind = ProxyKindOptions[0];
            ProxyHost = ProxyPort = ProxyUsername = ProxyPassword = "";
            return;
        }
        var kind = cfg.Type switch
        {
            ProxyType.Socks4 => ProxyKind.Socks4,
            ProxyType.Socks5 => ProxyKind.Socks5,
            _ => (raw ?? "").TrimStart().StartsWith("https", StringComparison.OrdinalIgnoreCase) ? ProxyKind.Https : ProxyKind.Http,
        };
        SelectedProxyKind = ProxyKindOptions.First(o => o.Value == kind);
        ProxyHost = cfg.Host;
        ProxyPort = cfg.Port.ToString();
        ProxyUsername = cfg.Username ?? "";
        ProxyPassword = cfg.Password ?? "";
    }

    // ---- Fingerprint ----
    [ObservableProperty] private string _userAgent = "";
    [ObservableProperty] private string _webGlVendor = "";
    [ObservableProperty] private string _webGlRenderer = "";
    [ObservableProperty] private decimal? _hardwareConcurrency;
    [ObservableProperty] private decimal? _deviceMemory;
    [ObservableProperty] private string _screenText = "";
    [ObservableProperty] private decimal? _devicePixelRatio;
    [ObservableProperty] private bool _uaMobile;
    [ObservableProperty] private Option<NoiseMode> _canvas;
    [ObservableProperty] private Option<NoiseMode> _webGlImage;
    [ObservableProperty] private Option<NoiseMode> _audio;
    [ObservableProperty] private Option<NoiseMode> _clientRects;
    [ObservableProperty] private Option<AutoMode> _timezoneMode;
    [ObservableProperty] private string _timezone = "";
    [ObservableProperty] private Option<AutoMode> _languageMode;
    [ObservableProperty] private string _languages = "";
    [ObservableProperty] private Option<AutoMode> _geoMode;
    [ObservableProperty] private string _latitude = "";
    [ObservableProperty] private string _longitude = "";
    [ObservableProperty] private Option<WebRtcMode> _webRtc;
    [ObservableProperty] private string _webRtcPublicIp = "";
    [ObservableProperty] private string _vendor = "";
    [ObservableProperty] private string _fonts = "";
    [ObservableProperty] private decimal? _audioInputs;
    [ObservableProperty] private decimal? _audioOutputs;
    [ObservableProperty] private decimal? _videoInputs;
    [ObservableProperty] private bool _osChangedWarning;

    [ObservableProperty] private string _error = "";

    public string? GroupId => SelectedGroup?.Id;
    public TargetOs Os => SelectedOs.Value;
    public bool IsTimezoneCustom => TimezoneMode.Value == AutoMode.Custom;
    public bool IsLanguageCustom => LanguageMode.Value == AutoMode.Custom;
    public bool IsGeoCustom => GeoMode.Value == AutoMode.Custom;
    public bool IsWebRtcAltered => WebRtc.Value == WebRtcMode.Altered;

    private ProfileEditorViewModel(ProfileService service, string coreVersion, string title, FingerprintConfig fp,
        IEnumerable<Group> groups)
    {
        _service = service;
        _coreVersion = coreVersion;
        Title = title;
        _fp = fp;
        Groups = [new GroupOption(null, "Không có nhóm"), .. groups.Select(g => new GroupOption(g.Id, g.Name))];
        _selectedGroup = Groups[0];
        _selectedOs = OsOptions[0];
        _canvas = NoiseOptions[0];
        _webGlImage = NoiseOptions[0];
        _audio = NoiseOptions[0];
        _clientRects = NoiseOptions[0];
        _timezoneMode = AutoOptions[0];
        _languageMode = AutoOptions[0];
        _geoMode = AutoOptions[0];
        _webRtc = WebRtcOptions[0];
        LoadFingerprint(fp);
    }

    public static ProfileEditorViewModel ForNew(ProfileService service, string coreVersion, string? groupId)
    {
        var vm = new ProfileEditorViewModel(service, coreVersion, "Tạo hồ sơ mới",
            FingerprintGenerator.Generate(coreVersion), service.Repo.ListGroups());
        vm.Name = $"Profile {service.Repo.CountProfiles() + 1}";
        vm.SelectedGroup = vm.Groups.FirstOrDefault(g => g.Id == groupId) ?? vm.Groups[0];
        return vm;
    }

    public static ProfileEditorViewModel ForExisting(ProfileService service, string coreVersion, Profile p)
    {
        var vm = new ProfileEditorViewModel(service, coreVersion, $"Sửa hồ sơ — {p.Name}",
            FingerprintConfig.FromJson(p.FingerprintJson), service.Repo.ListGroups())
        {
            Name = p.Name,
            Note = p.Note,
            StartupUrls = p.StartupUrls,
            ProxyCheckResult = p.ProxyStatus,
        };
        vm.LoadProxy(p.Proxy);
        vm.SelectedGroup = vm.Groups.FirstOrDefault(g => g.Id == p.GroupId) ?? vm.Groups[0];
        return vm;
    }

    private void LoadFingerprint(FingerprintConfig fp)
    {
        _fp = fp;
        SelectedOs = OsOptions.First(o => o.Value == fp.Os);
        UserAgent = fp.UserAgent;
        WebGlVendor = fp.WebGlVendor;
        WebGlRenderer = fp.WebGlRenderer;
        HardwareConcurrency = fp.HardwareConcurrency;
        DeviceMemory = fp.DeviceMemory;
        ScreenText = $"{fp.ScreenWidth}x{fp.ScreenHeight}";
        DevicePixelRatio = (decimal)fp.DevicePixelRatio;
        UaMobile = fp.UaMobile;
        Canvas = NoiseOptions.First(o => o.Value == fp.Canvas);
        WebGlImage = NoiseOptions.First(o => o.Value == fp.WebGlImage);
        Audio = NoiseOptions.First(o => o.Value == fp.Audio);
        ClientRects = NoiseOptions.First(o => o.Value == fp.ClientRects);
        TimezoneMode = AutoOptions.First(o => o.Value == fp.TimezoneMode);
        Timezone = fp.Timezone;
        LanguageMode = AutoOptions.First(o => o.Value == fp.LanguageMode);
        Languages = fp.Languages;
        GeoMode = AutoOptions.First(o => o.Value == fp.GeoMode);
        Latitude = fp.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Longitude = fp.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        WebRtc = WebRtcOptions.First(o => o.Value == fp.WebRtc);
        WebRtcPublicIp = fp.WebRtcPublicIp;
        Vendor = fp.Vendor;
        // Show one family per line for easier editing; stored back as comma-separated.
        Fonts = string.Join("\n", SplitFonts(fp.Fonts));
        AudioInputs = fp.AudioInputs;
        AudioOutputs = fp.AudioOutputs;
        VideoInputs = fp.VideoInputs;
    }

    private static IEnumerable<string> SplitFonts(string s) =>
        s.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    partial void OnSelectedOsChanged(Option<TargetOs> value)
    {
        if (value.Value == _fp.Os) { OsChangedWarning = value.Value != FingerprintGenerator.HostOs; return; }
        // OS drives UA, platform, GPU and screen: regenerate them together to stay consistent.
        var next = FingerprintGenerator.Generate(_coreVersion, value.Value);
        CopyModes(BuildFingerprint(), next);
        LoadFingerprint(next);
        OsChangedWarning = value.Value != FingerprintGenerator.HostOs;
    }

    partial void OnTimezoneModeChanged(Option<AutoMode> value) => OnPropertyChanged(nameof(IsTimezoneCustom));
    partial void OnLanguageModeChanged(Option<AutoMode> value) => OnPropertyChanged(nameof(IsLanguageCustom));
    partial void OnGeoModeChanged(Option<AutoMode> value) => OnPropertyChanged(nameof(IsGeoCustom));
    partial void OnWebRtcChanged(Option<WebRtcMode> value) => OnPropertyChanged(nameof(IsWebRtcAltered));

    private static void CopyModes(FingerprintConfig from, FingerprintConfig to)
    {
        to.Canvas = from.Canvas; to.WebGlImage = from.WebGlImage; to.Audio = from.Audio; to.ClientRects = from.ClientRects;
        to.TimezoneMode = from.TimezoneMode; to.Timezone = from.Timezone;
        to.LanguageMode = from.LanguageMode; to.Languages = from.Languages;
        to.GeoMode = from.GeoMode; to.Latitude = from.Latitude; to.Longitude = from.Longitude;
        to.WebRtc = from.WebRtc; to.WebRtcPublicIp = from.WebRtcPublicIp;
    }

    [RelayCommand]
    private void RandomFingerprint()
    {
        var next = FingerprintGenerator.Regenerate(BuildFingerprint(), _coreVersion);
        LoadFingerprint(next);
    }

    [RelayCommand]
    private async Task CheckProxyAsync()
    {
        if (!IsProxyEnabled)
        {
            ProxyCheckResult = "Chọn loại proxy và nhập thông tin trước khi kiểm tra.";
            return;
        }
        if (!ProxyConfig.TryParse(Proxy, out _, out var err))
        {
            ProxyCheckResult = $"Sai định dạng: {err}";
            return;
        }
        IsCheckingProxy = true;
        ProxyCheckResult = "Đang kiểm tra...";
        try
        {
            var info = await _service.CheckProxyAsync(Proxy);
            ProxyStatus = info.Summary;
            ProxyCheckResult = $"✓ {info.Ip} · {info.Country} ({info.City}) · {info.Timezone} · {info.LatencyMs}ms";
        }
        catch (Exception e) { ProxyCheckResult = $"✗ {e.Message}"; }
        finally { IsCheckingProxy = false; }
    }

    /// <summary>Validates input; returns false and sets <see cref="Error"/> when something is wrong.</summary>
    public bool Validate()
    {
        Error = "";
        if (string.IsNullOrWhiteSpace(Name)) Error = "Tên hồ sơ không được để trống.";
        else if (IsProxyEnabled && string.IsNullOrWhiteSpace(ProxyHost)) Error = "Proxy: chưa nhập Host/IP.";
        else if (IsProxyEnabled && string.IsNullOrWhiteSpace(ProxyPort)) Error = "Proxy: chưa nhập Port.";
        else if (!ProxyConfig.TryParse(Proxy, out _, out var perr)) Error = $"Proxy: {perr}";
        else if (HardwareConcurrency is null or < 1 or > 64) Error = "Số nhân CPU phải trong khoảng 1–64.";
        else if (DeviceMemory is null or < 1 or > 8) Error = "RAM (GB) phải trong khoảng 1–8 (giới hạn của Chrome).";
        else if (IsTimezoneCustom && !IsValidTimezone(Timezone)) Error = "Múi giờ không hợp lệ (ví dụ: Asia/Ho_Chi_Minh).";
        else if (IsGeoCustom && (!TryCoord(Latitude, 90, out _) || !TryCoord(Longitude, 180, out _)))
            Error = "Toạ độ không hợp lệ.";
        else if (!TryParseScreen(ScreenText, out _, out _)) Error = "Độ phân giải phải có dạng 1920x1080.";
        else if (DevicePixelRatio is null or < 0.5m or > 4m) Error = "Tỉ lệ điểm ảnh (DPR) phải trong khoảng 0.5–4.";
        return Error.Length == 0;
    }

    private static bool IsValidTimezone(string tz)
    {
        if (string.IsNullOrWhiteSpace(tz)) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(tz); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static bool TryCoord(string s, double max, out double v) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)
        && Math.Abs(v) <= max;

    private static bool TryParseScreen(string s, out int w, out int h)
    {
        w = h = 0;
        var parts = s.ToLowerInvariant().Split('x', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h)
               && w is >= 640 and <= 7680 && h is >= 480 and <= 4320;
    }

    public FingerprintConfig BuildFingerprint()
    {
        var fp = _fp.Clone();
        fp.Os = SelectedOs.Value;
        fp.UserAgent = UserAgent;
        fp.WebGlVendor = WebGlVendor;
        fp.WebGlRenderer = WebGlRenderer;
        fp.HardwareConcurrency = (int)(HardwareConcurrency ?? fp.HardwareConcurrency);
        fp.DeviceMemory = (int)(DeviceMemory ?? fp.DeviceMemory);
        if (TryParseScreen(ScreenText, out var w, out var h) && (w != fp.ScreenWidth || h != fp.ScreenHeight))
        {
            // keep the taskbar/menu-bar gap the preset had
            fp.AvailHeight = Math.Max(1, h - (fp.ScreenHeight - fp.AvailHeight));
            fp.AvailWidth = w;
            fp.ScreenWidth = w;
            fp.ScreenHeight = h;
            fp.WindowWidth = Math.Min(fp.WindowWidth, fp.AvailWidth);
            fp.WindowHeight = Math.Min(fp.WindowHeight, fp.AvailHeight);
        }
        if (DevicePixelRatio is { } dpr) fp.DevicePixelRatio = (double)dpr;
        fp.UaMobile = UaMobile;
        fp.Canvas = Canvas.Value;
        fp.WebGlImage = WebGlImage.Value;
        fp.Audio = Audio.Value;
        fp.ClientRects = ClientRects.Value;
        fp.TimezoneMode = TimezoneMode.Value;
        fp.Timezone = Timezone.Trim();
        fp.LanguageMode = LanguageMode.Value;
        fp.Languages = Languages.Trim();
        fp.GeoMode = GeoMode.Value;
        if (TryCoord(Latitude, 90, out var lat)) fp.Latitude = lat;
        if (TryCoord(Longitude, 180, out var lon)) fp.Longitude = lon;
        fp.WebRtc = WebRtc.Value;
        fp.WebRtcPublicIp = WebRtcPublicIp.Trim();
        fp.Vendor = Vendor.Trim();
        fp.Fonts = string.Join(",", SplitFonts(Fonts));
        fp.AudioInputs = (int)(AudioInputs ?? fp.AudioInputs);
        fp.AudioOutputs = (int)(AudioOutputs ?? fp.AudioOutputs);
        fp.VideoInputs = (int)(VideoInputs ?? fp.VideoInputs);
        return fp;
    }
}

public partial class BulkCreateViewModel(IEnumerable<Group> groups, string? groupId) : ObservableObject
{
    public List<GroupOption> Groups { get; } =
        [new GroupOption(null, "Không có nhóm"), .. groups.Select(g => new GroupOption(g.Id, g.Name))];

    [ObservableProperty] private string _prefix = "Profile";
    [ObservableProperty] private decimal? _countValue = 10;
    [ObservableProperty] private string _proxies = "";
    [ObservableProperty] private Option<TargetOs> _selectedOs = ProfileEditorViewModel.OsOptions[0];
    [ObservableProperty] private GroupOption? _selectedGroup;
    [ObservableProperty] private string _error = "";

    public int Count => (int)(CountValue ?? 0);
    public TargetOs Os => SelectedOs.Value;

    public List<string> ProxyLines() =>
        Proxies.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public bool Validate()
    {
        SelectedGroup ??= Groups.FirstOrDefault(g => g.Id == groupId) ?? Groups[0];
        Error = Count is < 1 or > 1000 ? "Số lượng phải trong khoảng 1–1000." :
            string.IsNullOrWhiteSpace(Prefix) ? "Tiền tố tên không được để trống." : "";
        foreach (var line in ProxyLines())
            if (Error.Length == 0 && !ProxyConfig.TryParse(line, out _, out var err)) Error = $"Proxy \"{line}\": {err}";
        return Error.Length == 0;
    }
}
