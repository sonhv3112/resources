using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpmLite.Core.Browser;
using GpmLite.Core.Services;

namespace GpmLite.App.ViewModels;

public enum Page { Profiles, Groups, Proxies, Extensions, Settings }

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ProfileService _service;

    public ProfilesViewModel Profiles { get; }
    public GroupsViewModel Groups { get; }
    public ProxiesViewModel Proxies { get; }
    public ExtensionsViewModel Extensions { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private Page _currentPage = Page.Profiles;
    [ObservableProperty] private object _currentView;
    [ObservableProperty] private string _coreText = "";
    [ObservableProperty] private string _apiText;
    [ObservableProperty] private int _runningCount;

    public bool IsProfiles => CurrentPage == Page.Profiles;
    public bool IsGroups => CurrentPage == Page.Groups;
    public bool IsProxies => CurrentPage == Page.Proxies;
    public bool IsExtensions => CurrentPage == Page.Extensions;
    public bool IsSettings => CurrentPage == Page.Settings;

    public MainWindowViewModel(ProfileService service, string? apiStatus)
    {
        _service = service;
        Profiles = new ProfilesViewModel(service);
        Groups = new GroupsViewModel(service, () => Profiles.ReloadGroups());
        Proxies = new ProxiesViewModel(service);
        Extensions = new ExtensionsViewModel(service);
        Settings = new SettingsViewModel(service, RefreshCore);
        _currentView = Profiles;
        _apiText = apiStatus is null ? "API: tắt" : $"API: {apiStatus}";

        service.StatusChanged += _ => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => RunningCount = service.RunningBrowsers.Count);
        RefreshCore();
    }

    private void RefreshCore()
    {
        if (_service.Settings.Engine == GpmLite.Core.Services.BrowserEngine.Camoufox)
        {
            CoreText = "Engine: Camoufox (Firefox)";
            return;
        }
        BrowserCore? core = ChromeLocator.Find(_service.Settings.BrowserPath);
        CoreText = core is null ? "Chưa tìm thấy Chrome" : $"Core: Chrome {core.Version}";
    }

    [RelayCommand]
    private void Navigate(Page page)
    {
        CurrentPage = page;
        CurrentView = page switch
        {
            Page.Groups => Groups,
            Page.Proxies => Proxies,
            Page.Extensions => Extensions,
            Page.Settings => Settings,
            _ => Profiles,
        };
        switch (page)
        {
            case Page.Groups: Groups.Reload(); break;
            case Page.Proxies: Proxies.Reload(); break;
            case Page.Extensions: Extensions.Reload(); break;
            case Page.Profiles: Profiles.ReloadGroups(); Profiles.Reload(); break;
        }
        OnPropertyChanged(nameof(IsProfiles));
        OnPropertyChanged(nameof(IsGroups));
        OnPropertyChanged(nameof(IsProxies));
        OnPropertyChanged(nameof(IsExtensions));
        OnPropertyChanged(nameof(IsSettings));
    }
}
