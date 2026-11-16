namespace NETDock.Core;

/// <summary>Dock items that control NETDock itself (RocketDock: Add item → Dock Settings / Quit).</summary>
public static class SpecialItems
{
    public const string Settings = "netdock:settings";
    public const string Quit = "netdock:quit";
    public const string Prefix = "netdock:";

    public const string SettingsIcon = @"%SystemRoot%\System32\shell32.dll,-16826";
    public const string QuitIcon = @"%SystemRoot%\System32\shell32.dll,-28";

    public static bool Is(DockItem item) => item.Target.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static DockItem Create(string target) => target switch
    {
        Settings => new DockItem { Kind = ItemKind.App, Name = "Dock Settings", Target = Settings, Icon = SettingsIcon },
        Quit => new DockItem { Kind = ItemKind.App, Name = "Quit NETDock", Target = Quit, Icon = QuitIcon },
        _ => throw new ArgumentException("Not a special item: " + target, nameof(target)),
    };
}
