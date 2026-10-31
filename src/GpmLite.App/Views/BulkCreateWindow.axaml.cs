using Avalonia.Controls;
using Avalonia.Interactivity;
using GpmLite.App.ViewModels;

namespace GpmLite.App.Views;

public partial class BulkCreateWindow : Window
{
    public BulkCreateWindow() => InitializeComponent();

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BulkCreateViewModel vm && vm.Validate()) Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
