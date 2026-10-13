using Avalonia.Controls;
using Avalonia.Input;
using MyHomeSecurity.App.ViewModels;

namespace MyHomeSecurity.App.Views;

public partial class CapturesView : UserControl
{
    public CapturesView() => InitializeComponent();

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CaptureItemViewModel item)
            item.OpenCommand.Execute(null);
    }
}
