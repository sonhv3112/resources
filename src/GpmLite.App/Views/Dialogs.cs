using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GpmLite.App.ViewModels;
using GpmLite.Core.Models;

namespace GpmLite.App.Views;

/// <summary>Modal dialogs and toasts, owned by the main window.</summary>
public static class Dialogs
{
    private static WindowNotificationManager? _toasts;
    private static Window? _owner;

    public static Window? Owner
    {
        get => _owner;
        set
        {
            _owner = value;
            if (value is not null)
                _toasts = new WindowNotificationManager(value) { Position = NotificationPosition.BottomRight, MaxItems = 4 };
        }
    }

    public static void Notify(string title, string message, bool error = false) =>
        _toasts?.Show(new Notification(title, message, error ? NotificationType.Error : NotificationType.Success,
            TimeSpan.FromSeconds(error ? 8 : 4)));

    public static async Task<bool?> EditProfileAsync(ProfileEditorViewModel vm) =>
        Owner is null ? null : await new ProfileEditorWindow { DataContext = vm }.ShowDialog<bool?>(Owner);

    public static async Task<bool?> BulkCreateAsync(BulkCreateViewModel vm) =>
        Owner is null ? null : await new BulkCreateWindow { DataContext = vm }.ShowDialog<bool?>(Owner);

    public static async Task CopyAsync(string text)
    {
        if (Owner?.Clipboard is { } cb)
        {
            await cb.SetTextAsync(text);
            Notify("Đã sao chép", text);
        }
    }

    public static async Task<string?> PickFileAsync(string title)
    {
        if (Owner is null) return null;
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<bool> ConfirmAsync(string title, string message, bool danger = false)
    {
        if (Owner is null) return false;
        var w = SmallWindow(title);
        var ok = new Button { Content = danger ? "Xoá" : "Đồng ý", Classes = { "primary" }, IsDefault = true };
        if (danger) ok.Background = (IBrush)Application.Current!.FindResource("Danger")!;
        var cancel = new Button { Content = "Huỷ", Classes = { "ghost" }, IsCancel = true };
        ok.Click += (_, _) => w.Close(true);
        cancel.Click += (_, _) => w.Close(false);
        w.Content = Layout(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, cancel, ok);
        return await w.ShowDialog<bool?>(Owner) == true;
    }

    public static async Task<string?> PromptAsync(string title, string label, string initial)
    {
        if (Owner is null) return null;
        var w = SmallWindow(title);
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = "Lưu", Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = "Huỷ", Classes = { "ghost" }, IsCancel = true };
        ok.Click += (_, _) => w.Close(box.Text);
        cancel.Click += (_, _) => w.Close(null);
        var body = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = label }, box } };
        w.Content = Layout(body, cancel, ok);
        w.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await w.ShowDialog<string?>(Owner);
    }

    public static async Task<GroupOption?> PickGroupAsync(IReadOnlyList<Group> groups)
    {
        if (Owner is null) return null;
        var w = SmallWindow("Chuyển nhóm");
        var options = new List<GroupOption> { new(null, "Không có nhóm") };
        options.AddRange(groups.Select(g => new GroupOption(g.Id, g.Name)));
        var combo = new ComboBox { ItemsSource = options, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var ok = new Button { Content = "Chuyển", Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = "Huỷ", Classes = { "ghost" }, IsCancel = true };
        ok.Click += (_, _) => w.Close(combo.SelectedItem as GroupOption);
        cancel.Click += (_, _) => w.Close(null);
        w.Content = Layout(new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "Nhóm đích" }, combo } }, cancel, ok);
        return await w.ShowDialog<GroupOption?>(Owner);
    }

    private static Window SmallWindow(string title) => new()
    {
        Title = title,
        Width = 420,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
    };

    private static Control Layout(Control body, Button cancel, Button ok) => new StackPanel
    {
        Margin = new Thickness(20),
        Spacing = 18,
        Children =
        {
            body,
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { cancel, ok },
            },
        },
    };
}
