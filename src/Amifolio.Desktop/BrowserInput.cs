namespace Amifolio.Desktop;

internal enum BrowserCommand { None, Back, Forward, Up, Refresh, SelectAll, CopyPaths, Open }
internal static class BrowserInput
{
    public static BrowserCommand Key(int key, bool alt, bool control, bool shift, bool editing)
    {
        if (editing || shift || (alt && control)) return BrowserCommand.None;
        if (alt) return key switch { 37 => BrowserCommand.Back, 39 => BrowserCommand.Forward, 38 => BrowserCommand.Up, _ => BrowserCommand.None };
        if (control) return key switch { 65 => BrowserCommand.SelectAll, 67 => BrowserCommand.CopyPaths, _ => BrowserCommand.None };
        return key switch { 8 => BrowserCommand.Back, 13 => BrowserCommand.Open, 116 => BrowserCommand.Refresh, _ => BrowserCommand.None };
    }
    public static BrowserCommand SideButton(bool backPressed, bool forwardPressed) =>
        backPressed == forwardPressed ? BrowserCommand.None : backPressed ? BrowserCommand.Back : BrowserCommand.Forward;
}
