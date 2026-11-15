namespace NETDock.Core;

/// <summary>A global hotkey written as text, e.g. "Ctrl+Alt+R", parsed for RegisterHotKey.</summary>
public readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    /// <summary>Null for blank or invalid text. Needs at least one modifier, so a bare key can't be stolen.</summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0, vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": mods |= Ctrl; continue;
                case "ALT": mods |= Alt; continue;
                case "SHIFT": mods |= Shift; continue;
                case "WIN" or "WINDOWS": mods |= Win; continue;
            }
            if (vk != 0) return null; // two non-modifier keys
            vk = KeyCode(raw.ToUpperInvariant());
            if (vk == 0) return null;
        }
        return mods != 0 && vk != 0 ? new Hotkey(mods, vk) : null;
    }

    private static uint KeyCode(string k)
    {
        if (k.Length == 1 && (char.IsAsciiLetterUpper(k[0]) || char.IsAsciiDigit(k[0]))) return k[0];
        if (k.Length >= 2 && k[0] == 'F' && int.TryParse(k.AsSpan(1), out var f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        return k switch
        {
            "SPACE" => 0x20, "TAB" => 0x09, "HOME" => 0x24, "END" => 0x23, "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E, "PAGEUP" or "PGUP" => 0x21, "PAGEDOWN" or "PGDN" => 0x22,
            "UP" => 0x26, "DOWN" => 0x28, "LEFT" => 0x25, "RIGHT" => 0x27, "`" or "TILDE" => 0xC0,
            _ => 0,
        };
    }
}
