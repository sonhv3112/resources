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
        // Dev aid: GPMLITE_SNAPSHOT_DIR=/path renders every page to PNG (no screen-recording permission needed).
        if (Environment.GetEnvironmentVariable("GPMLITE_SNAPSHOT_DIR") is { Length: > 0 } dir)
            Opened += async (_, _) => await SnapshotPagesAsync(dir);
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
