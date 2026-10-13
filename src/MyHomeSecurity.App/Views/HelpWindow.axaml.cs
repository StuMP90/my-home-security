using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MyHomeSecurity.App.Views;

/// <summary>Keyboard shortcuts and how things work (Help button / Ctrl+Shift+H).</summary>
public partial class HelpWindow : Window
{
    // (keys, what it does); a null key is a plain note line.
    private static readonly (string Title, (string? Keys, string Text)[] Rows)[] Sections =
    {
        ("Getting around", new (string?, string)[]
        {
            ("Ctrl+1 … Ctrl+4", "Cameras / Home / Captures / Settings"),
            ("F11", "Full screen (or leave it)"),
            ("Esc", "Leave full screen, then go back from one camera to the grid"),
        }),
        ("Cameras", new (string?, string)[]
        {
            ("Find cameras…", "Search your local network for cameras (ONVIF), then add the ones you want"),
            ("Add camera…", "Add one by address: a Tapo or other RTSP camera, or any stream URL"),
            ("Double-click a tile", "View that camera full size, using its high-resolution stream"),
            ("Double-click the picture", "Full screen, while viewing one camera"),
            ("Left / Right arrows", "Previous / next camera, while viewing one camera"),
            ("Snapshot / Record", "Save a full-resolution picture, or record the camera's main stream to a file"),
            ("Arrange", "Reorder, edit or remove cameras"),
            (null, "Only what's on screen streams. The grid uses each camera's small stream at a low frame rate; switching tab, minimising or locking stops the streams. Settings → Video tunes the load."),
            (null, "Recordings copy the camera's video without re-encoding, so they cost almost no CPU, and they carry on while the app is locked. The red button at the top lists them."),
        }),
        ("Home Assistant", new (string?, string)[]
        {
            ("Add tile…", "Show a Home Assistant entity: a switch, light, sensor, climate and more"),
            ("Click a switch", "Turn it on or off"),
            ("Arrange", "Reorder, edit or remove tiles"),
            (null, "Home Assistant is only asked for its state while the Home Assistant tab is showing. Set the address and access token in Settings."),
        }),
        ("Captures", new (string?, string)[]
        {
            ("Double-click", "Open a snapshot or recording in your usual viewer"),
            (null, "Snapshots and recordings are ordinary files, not encrypted. Keep the folders somewhere only you can read."),
        }),
        ("Security", new (string?, string)[]
        {
            ("Ctrl+Shift+L", "Lock My Home Security. Live views stop; recordings keep running"),
            ("Ctrl+Shift+H", "This help"),
            (null, "Camera passwords and the Home Assistant token are kept only in an encrypted file, unlocked with your password. The app locks itself after the idle time set in Settings."),
            (null, "No telemetry, no accounts, no cloud: the app only talks to your cameras and your Home Assistant."),
            (null, "Forgot the password? Use the link on the unlock screen to start again. The old encrypted file is set aside, not deleted, in case the password turns up."),
        }),
    };

    public HelpWindow()
    {
        InitializeComponent();
        VersionText.Text = AppInfo.DisplayVersion;
        foreach (var (title, rows) in Sections)
        {
            SectionsPanel.Children.Add(new TextBlock { Text = title, Classes = { "section" } });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*") };
            foreach (var (keys, text) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var row = grid.RowDefinitions.Count - 1;
                if (keys == null)
                {
                    var note = new TextBlock { Text = text, Classes = { "note" } };
                    Grid.SetRow(note, row);
                    Grid.SetColumnSpan(note, 2);
                    grid.Children.Add(note);
                    continue;
                }
                var key = new TextBlock { Text = keys, Classes = { "key" } };
                var what = new TextBlock { Text = text, Classes = { "what" } };
                Grid.SetRow(key, row);
                Grid.SetRow(what, row);
                Grid.SetColumn(what, 1);
                grid.Children.Add(key);
                grid.Children.Add(what);
            }
            SectionsPanel.Children.Add(grid);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnLicences(object? sender, RoutedEventArgs e) => _ = new LicensesWindow().ShowDialog(this);
}
