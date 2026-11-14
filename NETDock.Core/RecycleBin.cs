namespace NETDock.Core;

/// <summary>The Recycle Bin item: which icon to show for its empty and full states.</summary>
public static class RecycleBin
{
    public const string Target = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    public const string EmptyIcon = @"%SystemRoot%\System32\imageres.dll,-55";
    public const string FullIcon = @"%SystemRoot%\System32\imageres.dll,-54";

    public static bool Is(DockItem item) => string.Equals(item.Target, Target, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Empty: the item's icon, or Windows' empty bin. Full: the item's "full" icon (RocketDock's
    /// FileName2); failing that Windows' full bin, unless the user chose their own single icon,
    /// which is then kept for both states.
    /// </summary>
    public static string IconFor(DockItem item, bool full)
    {
        if (!full) return item.Icon ?? EmptyIcon;
        if (item.IconFull is { } f) return f;
        return item.Icon is null || string.Equals(item.Icon, EmptyIcon, StringComparison.OrdinalIgnoreCase) ? FullIcon : item.Icon;
    }
}
