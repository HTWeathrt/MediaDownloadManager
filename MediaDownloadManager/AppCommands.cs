using System.Windows.Input;

namespace MediaDownloadManager;

public static class AppCommands
{
    public static readonly RoutedUICommand AddUrl = Make("Add URL...", nameof(AddUrl), Key.N, ModifierKeys.Control);
    public static readonly RoutedUICommand FromClipboard = Make("Add from clipboard", nameof(FromClipboard), Key.V, ModifierKeys.Control | ModifierKeys.Shift);
    public static readonly RoutedUICommand ImportList = Make("Import list...", nameof(ImportList), Key.O, ModifierKeys.Control);
    public static readonly RoutedUICommand OpenOutputFolder = Make("Open output folder", nameof(OpenOutputFolder), Key.L, ModifierKeys.Control);
    public static readonly RoutedUICommand StartAll = Make("Start all", nameof(StartAll), Key.F5, ModifierKeys.None);
    public static readonly RoutedUICommand PauseAll = Make("Pause all", nameof(PauseAll), Key.F6, ModifierKeys.None);
    public static readonly RoutedUICommand StartSelected = Make("Start / retry selected", nameof(StartSelected), Key.R, ModifierKeys.Control);
    public static readonly RoutedUICommand OpenFile = Make("Open file", nameof(OpenFile), Key.None, ModifierKeys.None);
    public static readonly RoutedUICommand ShowInFolder = Make("Show in folder", nameof(ShowInFolder), Key.E, ModifierKeys.Control);
    public static readonly RoutedUICommand CopyUrl = Make("Copy URL", nameof(CopyUrl), Key.C, ModifierKeys.Control | ModifierKeys.Shift);
    public static readonly RoutedUICommand ConvertHevc = Make("Convert HEVC → H.264", nameof(ConvertHevc), Key.H, ModifierKeys.Control | ModifierKeys.Shift);
    public static readonly RoutedUICommand ConvertFile = Make("Convert video file...", nameof(ConvertFile), Key.None, ModifierKeys.None);
    public static readonly RoutedUICommand PriorityHigh = Make("⬆ High priority", nameof(PriorityHigh), Key.D1, ModifierKeys.Control);
    public static readonly RoutedUICommand PriorityNormal = Make("● Normal priority", nameof(PriorityNormal), Key.D2, ModifierKeys.Control);
    public static readonly RoutedUICommand PriorityLow = Make("⬇ Low priority", nameof(PriorityLow), Key.D3, ModifierKeys.Control);
    public static readonly RoutedUICommand ClearCompleted = Make("Clear completed", nameof(ClearCompleted), Key.Delete, ModifierKeys.Control | ModifierKeys.Shift);
    public static readonly RoutedUICommand Exit = Make("Exit", nameof(Exit), Key.None, ModifierKeys.None);

    private static RoutedUICommand Make(string text, string name, Key key, ModifierKeys modifiers)
    {
        var gestures = new InputGestureCollection();
        if (key != Key.None) gestures.Add(new KeyGesture(key, modifiers));
        return new RoutedUICommand(text, name, typeof(AppCommands), gestures);
    }
}
