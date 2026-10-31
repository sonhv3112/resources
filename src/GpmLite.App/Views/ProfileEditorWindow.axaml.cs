using Avalonia.Controls;
using Avalonia.Interactivity;
using GpmLite.App.ViewModels;

namespace GpmLite.App.Views;

public partial class ProfileEditorWindow : Window
{
    public ProfileEditorWindow() => InitializeComponent();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProfileEditorViewModel vm && vm.Validate()) Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
