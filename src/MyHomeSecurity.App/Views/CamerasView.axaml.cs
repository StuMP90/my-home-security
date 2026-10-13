using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using MyHomeSecurity.App.ViewModels;

namespace MyHomeSecurity.App.Views;

public partial class CamerasView : UserControl
{
    public CamerasView() => InitializeComponent();

    private void OnTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        // A double-click on one of the tile's buttons isn't a request to open the camera.
        if (IsInsideButton(e.Source as Avalonia.Visual))
            return;
        if ((sender as Control)?.DataContext is CameraTileViewModel tile)
            tile.OpenLiveCommand.Execute(null);
    }

    private void OnLiveDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main)
            main.ToggleFullScreenCommand.Execute(null);
    }

    private static bool IsInsideButton(Avalonia.Visual? visual)
    {
        for (var v = visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            if (v is Button)
                return true;
        return false;
    }
}
