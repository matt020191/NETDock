namespace NETDock.Core;

/// <summary>Dock items that control NETDock itself (RocketDock: Add item → Dock Settings / Quit).</summary>
public static class SpecialItems
{
    public const string Settings = "netdock:settings";
    public const string Quit = "netdock:quit";
    public const string Search = "netdock:search";
    // System corner tiles (drawn live by the dock, no icon file).
    public const string Clock = "netdock:clock";
    public const string Battery = "netdock:battery";
    public const string Network = "netdock:network";
    public const string Volume = "netdock:volume";
    public static readonly string[] SystemCorner = [Clock, Battery, Network, Volume];

    /// <summary>A live system tile (clock, battery, network, volume) rather than an icon.</summary>
    public static bool IsTile(DockItem item) => Array.IndexOf(SystemCorner, item.Target.ToLowerInvariant()) >= 0;
    public const string Prefix = "netdock:";
    /// <summary>A minimised window shown on the dock (never saved).</summary>
    public const string Window = "netdock:window";

    public const string SettingsIcon = @"%SystemRoot%\System32\shell32.dll,-16826";
    public const string QuitIcon = @"%SystemRoot%\System32\shell32.dll,-28";
    public const string SearchIcon = @"%SystemRoot%\System32\shell32.dll,-23";

    public static bool Is(DockItem item) => item.Target.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static DockItem Create(string target) => target switch
    {
        Settings => new DockItem { Kind = ItemKind.App, Name = "Dock Settings", Target = Settings, Icon = SettingsIcon },
        Quit => new DockItem { Kind = ItemKind.App, Name = "Quit NETDock", Target = Quit, Icon = QuitIcon },
        Search => new DockItem { Kind = ItemKind.App, Name = "Search", Target = Search, Icon = SearchIcon },
        Clock => new DockItem { Kind = ItemKind.App, Name = "Clock", Target = Clock },
        Battery => new DockItem { Kind = ItemKind.App, Name = "Battery", Target = Battery },
        Network => new DockItem { Kind = ItemKind.App, Name = "Network", Target = Network },
        Volume => new DockItem { Kind = ItemKind.App, Name = "Volume", Target = Volume },
        _ => throw new ArgumentException("Not a special item: " + target, nameof(target)),
    };
}
