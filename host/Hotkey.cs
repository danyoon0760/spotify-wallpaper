using static SpotifyWallpaper.Native;

namespace SpotifyWallpaper;

// 전역 단축키 조합. 설정 파일에는 "Ctrl+Alt+L" 같은 글자로 저장한다.
internal readonly record struct Hotkey(uint Modifiers, Keys Key)
{
    // 글자를 조합으로. 빈 문자열(사용 안 함)이거나 잘못된 값이면 false.
    // 다른 앱의 일반 입력을 가로채지 않도록 Ctrl·Alt·Win 중 하나는 꼭 있어야 한다.
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint mods = 0;
        Keys key = Keys.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "windows": mods |= MOD_WIN; break;
                default:
                    if (key != Keys.None) return false; // 일반 키는 하나만
                    if (part.Length == 1 && char.IsAsciiDigit(part[0])) key = Keys.D0 + (part[0] - '0');
                    else if (part.All(char.IsAsciiDigit) || !Enum.TryParse(part, ignoreCase: true, out key) || !Enum.IsDefined(key)) return false;
                    break;
            }
        }
        if (key == Keys.None || IsModifierKey(key) || (key & Keys.Modifiers) != 0) return false;
        if ((mods & (MOD_CONTROL | MOD_ALT | MOD_WIN)) == 0) return false;
        hotkey = new Hotkey(mods, key);
        return true;
    }

    // WinForms 키 입력(Keys.Control | Keys.Alt | Keys.L 등)에서 조합을 만든다. 아직 일반 키가 안 눌렸으면 null
    public static Hotkey? FromKeyData(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        if (key == Keys.None || IsModifierKey(key)) return null;
        uint mods = 0;
        if ((keyData & Keys.Control) != 0) mods |= MOD_CONTROL;
        if ((keyData & Keys.Alt) != 0) mods |= MOD_ALT;
        if ((keyData & Keys.Shift) != 0) mods |= MOD_SHIFT;
        return new Hotkey(mods, key);
    }

    public bool HasRequiredModifier => (Modifiers & (MOD_CONTROL | MOD_ALT | MOD_WIN)) != 0;

    static bool IsModifierKey(Keys key) => key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
        or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
        or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(Key is >= Keys.D0 and <= Keys.D9 ? ((char)('0' + (Key - Keys.D0))).ToString() : Key.ToString());
        return string.Join("+", parts);
    }
}
