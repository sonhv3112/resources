using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using GpmLite.App.ViewModels;

namespace GpmLite.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Dev aid: force a window size for snapshot testing at various widths.
        if (Environment.GetEnvironmentVariable("GPMLITE_WIN_W") is { Length: > 0 } ws && double.TryParse(ws, out var ww))
            Width = ww;
        if (Environment.GetEnvironmentVariable("GPMLITE_WIN_H") is { Length: > 0 } hs && double.TryParse(hs, out var hh))
            Height = hh;
        // Never let the window be larger than the screen it's on (otherwise the right/bottom — e.g. the
        // "Thao tác" column — falls off the screen edge and looks cut). Clamp size and nudge on-screen.
        Opened += (_, _) => ClampToScreen();

        // Dev aid: GPMLITE_SNAPSHOT_DIR=/path renders every page to PNG (no screen-recording permission needed).
        if (Environment.GetEnvironmentVariable("GPMLITE_SNAPSHOT_DIR") is { Length: > 0 } dir)
            Opened += async (_, _) => await SnapshotPagesAsync(dir);
    }

    private void ClampToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling;
        // WorkingArea excludes the menu bar / dock / taskbar; convert physical px -> logical.
        var availW = screen.WorkingArea.Width / scale;
        var availH = screen.WorkingArea.Height / scale;
        if (Width > availW) Width = availW;
        if (Height > availH) Height = availH;
        // Re-center if the window now hangs off the screen edge.
        var pos = Position;
        var wa = screen.WorkingArea;
        var w = (int)(Width * scale);
        var h = (int)(Height * scale);
        var x = Math.Max(wa.X, Math.Min(pos.X, wa.X + wa.Width - w));
        var y = Math.Max(wa.Y, Math.Min(pos.Y, wa.Y + wa.Height - h));
        if (x != pos.X || y != pos.Y) Position = new PixelPoint(x, y);
    }

    private async Task SnapshotPagesAsync(string dir)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        Directory.CreateDirectory(dir);
        foreach (var page in Enum.GetValues<Page>())
        {
            vm.NavigateCommand.Execute(page);
            await Task.Delay(700);
            await Dispatcher.UIThread.InvokeAsync(() => Save(this, Path.Combine(dir, $"{page}.png")));
        }
        vm.NavigateCommand.Execute(Page.Profiles);

        // Editor dialog, one image per tab.
        var svcProfile = vm.Profiles.Rows.FirstOrDefault();
        if (svcProfile is null || Avalonia.Application.Current is not App) return;
        var editor = vm.Profiles.CreateEditorForSnapshot(svcProfile.Id);
        if (editor is null) return;
        var w = new ProfileEditorWindow { DataContext = editor };
        w.Show(this);
        await Task.Delay(700);
        var tabs = w.GetLogicalDescendants().OfType<TabControl>().First();
        for (var i = 0; i < tabs.ItemCount; i++)
        {
            tabs.SelectedIndex = i;
            await Task.Delay(400);
            await Dispatcher.UIThread.InvokeAsync(() => Save(w, Path.Combine(dir, $"Editor{i}.png")));
        }
        w.Close();
        await Task.Delay(1500); // let a profile started via the API show up as running
        await Dispatcher.UIThread.InvokeAsync(() => Save(this, Path.Combine(dir, "ProfilesRunning.png")));
    }

    internal static void Save(Visual visual, string file)
    {
        var size = visual.Bounds.Size;
        var scale = (visual as TopLevel)?.RenderScaling ?? 1;
        using var bmp = new RenderTargetBitmap(new PixelSize((int)(size.Width * scale), (int)(size.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        bmp.Render(visual);
        bmp.Save(file);
    }
}
