using System;
using System.Collections.Generic;
using System.Text;

namespace MouseFace
{
    // The five buttons Windows knows, named by what a mouse sends.
    internal enum Btn { Left, Right, Middle, Back, Forward }

    internal enum ActKind { Button, Double, ClickLock, Keys, None }

    // What one button does, as written in settings.ini.
    internal sealed class Act
    {
        internal readonly ActKind Kind;
        internal readonly Btn Button;   // Kind.Button: which button to send
        internal readonly ushort[] Keys; // Kind.Keys: virtual-key codes, pressed in this order
        internal readonly string Text;   // the canonical spelling, for the log and --watch
        // Kind.Keys: held while the button is: a single key (for push-to-talk and the like), or Ctrl,
        // Shift, Alt and Win on their own (for Ctrl+click). A shortcut of a modifier and another key is
        // pressed and let go at once.
        internal readonly bool Hold;

        Act(ActKind kind, Btn button, ushort[] keys, string text)
        {
            Kind = kind; Button = button; Keys = keys; Text = text;
            Hold = keys != null && (keys.Length == 1 || Array.TrueForAll(keys, KeyNames.IsModifier));
        }

        internal static Act Same(Btn b) { return new Act(ActKind.Button, b, null, b.ToString()); }
        internal static readonly Act Double = new Act(ActKind.Double, Btn.Left, null, "Double");
        internal static readonly Act ClickLock = new Act(ActKind.ClickLock, Btn.Left, null, "ClickLock");
        internal static readonly Act None = new Act(ActKind.None, Btn.Left, null, "None");

        // True when this action is the button itself, so the click can pass through untouched.
        internal bool IsPlain(Btn b) { return Kind == ActKind.Button && Button == b; }

        public override string ToString() { return Text; }

        internal static bool TryParse(string text, out Act act, out string error)
        {
            act = null; error = null;
            string t = (text ?? "").Trim();
            Btn b;
            if (Buttons.TryParse(t, out b)) { act = Same(b); return true; }
            if (t.Equals("Double", StringComparison.OrdinalIgnoreCase)) { act = Double; return true; }
            if (t.Equals("ClickLock", StringComparison.OrdinalIgnoreCase)) { act = ClickLock; return true; }
            if (t.Equals("None", StringComparison.OrdinalIgnoreCase)) { act = None; return true; }
            if (t.StartsWith("Keys ", StringComparison.OrdinalIgnoreCase) || t.Equals("Keys", StringComparison.OrdinalIgnoreCase))
            {
                ushort[] keys;
                string canonical;
                if (!KeyNames.TryParseChord(t.Substring(4).Trim(), out keys, out canonical, out error)) return false;
                act = new Act(ActKind.Keys, Btn.Left, keys, "Keys " + canonical);
                return true;
            }
            error = "'" + t + "' is not an action. Use Left, Right, Middle, Back, Forward, Double, ClickLock, None or Keys followed by a shortcut (like Keys Ctrl+C)";
            return false;
        }
    }

    // One piece of input to send: a mouse button or a key, going down or up.
    internal struct Out
    {
        internal bool IsKey;
        internal Btn B;
        internal ushort Vk;
        internal bool Down;

        internal static Out Mouse(Btn b, bool down) { Out o = new Out(); o.B = b; o.Down = down; return o; }
        internal static Out Key(ushort vk, bool down) { Out o = new Out(); o.IsKey = true; o.Vk = vk; o.Down = down; return o; }

        public override string ToString() { return (IsKey ? KeyNames.NameOf(Vk) : B.ToString()) + (Down ? "+" : "-"); }

        internal static string Describe(List<Out> outs)
        {
            StringBuilder s = new StringBuilder();
            foreach (Out o in outs) s.Append(s.Length > 0 ? " " : "").Append(o.ToString());
            return s.ToString();
        }
    }

    internal static class Buttons
    {
        internal static bool TryParse(string text, out Btn b)
        {
            foreach (Btn x in (Btn[])Enum.GetValues(typeof(Btn)))
                if (x.ToString().Equals(text.Trim(), StringComparison.OrdinalIgnoreCase)) { b = x; return true; }
            b = Btn.Left;
            return false;
        }

        // A low-level hook message as a button going down or up. Wheels and movement are not buttons.
        internal static bool Classify(int msg, uint mouseData, out Btn b, out bool down)
        {
            b = Btn.Left; down = false;
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN: down = true; return true;
                case Native.WM_LBUTTONUP: return true;
                case Native.WM_RBUTTONDOWN: b = Btn.Right; down = true; return true;
                case Native.WM_RBUTTONUP: b = Btn.Right; return true;
                case Native.WM_MBUTTONDOWN: b = Btn.Middle; down = true; return true;
                case Native.WM_MBUTTONUP: b = Btn.Middle; return true;
                case Native.WM_XBUTTONDOWN:
                case Native.WM_XBUTTONUP:
                    down = msg == Native.WM_XBUTTONDOWN;
                    uint x = mouseData >> 16;
                    if (x == 1) { b = Btn.Back; return true; }
                    if (x == 2) { b = Btn.Forward; return true; }
                    return false;
            }
            return false;
        }

        // With the buttons swapped in Windows (Settings > Mouse, often by left-handed people), the hook
        // sees the swapped, logical button, while Raw Input reports and SendInput use the physical one.
        // Measured on Windows 11: an injected physical left press, swapped, reached the low-level
        // hook as WM_RBUTTONDOWN and Raw Input as RI_MOUSE_LEFT_BUTTON_DOWN.
        internal static Btn Physical(Btn logical, bool swapped)
        {
            if (!swapped) return logical;
            if (logical == Btn.Left) return Btn.Right;
            if (logical == Btn.Right) return Btn.Left;
            return logical;
        }

        internal static bool Swapped() { return Native.GetSystemMetrics(Native.SM_SWAPBUTTON) != 0; }

        // The Raw Input button flag for a physical button going down or up (RI_MOUSE_*), used to find
        // the report that names the device a click came from.
        internal static ushort RawFlag(Btn b, bool down)
        {
            int bit = (int)b * 2 + (down ? 0 : 1);
            // Raw Input orders the buttons Left, Right, Middle, 4 (Back), 5 (Forward), as Btn does.
            return (ushort)(1 << bit);
        }

        internal const ushort RawWheel = 0x0400, RawHWheel = 0x0800, RawButtons = 0x03FF;
    }

    // Key names for "Keys" actions, like Ctrl+Shift+T.
    internal static class KeyNames
    {
        static readonly Dictionary<string, ushort> byName = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<ushort, string> byCode = new Dictionary<ushort, string>();

        static void Add(string name, int vk, params string[] aliases)
        {
            byName[name] = (ushort)vk;
            byCode[(ushort)vk] = name;
            foreach (string a in aliases) byName[a] = (ushort)vk;
        }

        static KeyNames()
        {
            Add("Ctrl", 0x11, "Control"); Add("Shift", 0x10); Add("Alt", 0x12); Add("Win", 0x5B, "Windows");
            for (char c = 'A'; c <= 'Z'; c++) Add(c.ToString(), c);
            for (char c = '0'; c <= '9'; c++) Add(c.ToString(), c);
            for (int i = 1; i <= 24; i++) Add("F" + i, 0x6F + i);
            Add("Enter", 0x0D, "Return"); Add("Esc", 0x1B, "Escape"); Add("Tab", 0x09); Add("Space", 0x20);
            Add("Backspace", 0x08); Add("Delete", 0x2E, "Del"); Add("Insert", 0x2D, "Ins");
            Add("Home", 0x24); Add("End", 0x23); Add("PageUp", 0x21, "PgUp"); Add("PageDown", 0x22, "PgDn");
            Add("Up", 0x26); Add("Down", 0x28); Add("Left", 0x25); Add("Right", 0x27);
            Add("PrintScreen", 0x2C, "PrtSc"); Add("CapsLock", 0x14); Add("Menu", 0x5D, "Apps");
            Add("Plus", 0xBB); Add("Minus", 0xBD); Add("Comma", 0xBC); Add("Period", 0xBE);
            Add("Semicolon", 0xBA); Add("Slash", 0xBF); Add("Backslash", 0xDC); Add("Quote", 0xDE);
            Add("Backquote", 0xC0); Add("OpenBracket", 0xDB); Add("CloseBracket", 0xDD);
            Add("VolumeUp", 0xAF); Add("VolumeDown", 0xAE); Add("Mute", 0xAD);
            Add("PlayPause", 0xB3); Add("NextTrack", 0xB0); Add("PrevTrack", 0xB1); Add("MediaStop", 0xB2);
            Add("BrowserBack", 0xA6); Add("BrowserForward", 0xA7); Add("BrowserRefresh", 0xA8); Add("BrowserHome", 0xAC);
        }

        internal static string NameOf(ushort vk)
        {
            string n;
            return byCode.TryGetValue(vk, out n) ? n : "VK" + vk.ToString("X2");
        }

        internal static bool Has(ushort vk) { return byCode.ContainsKey(vk); }

        internal static bool IsModifier(ushort vk) { return vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B; }

        // Keys Windows wants flagged as "extended" (the second copy on a full keyboard), or programs
        // read them as their number-pad twins.
        internal static bool IsExtended(ushort vk)
        {
            return (vk >= 0x21 && vk <= 0x28) || vk == 0x2C || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C
                || vk == 0x5D || vk == 0x6F || vk == 0x90 || (vk >= 0xA6 && vk <= 0xB7);
        }

        // "Ctrl+Shift+T": modifiers in any order, then at most one other key. A chord of modifiers
        // alone (like "Ctrl") holds them while the button is down; any other is a tap (see Act.Hold).
        internal static bool TryParseChord(string text, out ushort[] keys, out string canonical, out string error)
        {
            keys = null; canonical = null; error = null;
            if (text.Length == 0) { error = "Keys needs a shortcut after it, like Keys Ctrl+C"; return false; }
            List<ushort> mods = new List<ushort>();
            ushort main = 0;
            foreach (string raw in text.Split('+'))
            {
                string part = raw.Trim();
                ushort vk;
                if (part.Length == 0 || !byName.TryGetValue(part, out vk)) { error = "'" + part + "' in '" + text + "' is not a key name"; return false; }
                if (IsModifier(vk))
                {
                    if (main != 0) { error = "in '" + text + "' the modifiers (Ctrl, Shift, Alt, Win) go before the key"; return false; }
                    if (mods.Contains(vk)) { error = "'" + text + "' names " + NameOf(vk) + " twice"; return false; }
                    mods.Add(vk);
                }
                else
                {
                    if (main != 0) { error = "'" + text + "' has two keys besides Ctrl, Shift, Alt and Win; use one"; return false; }
                    main = vk;
                }
            }
            List<ushort> all = new List<ushort>(mods);
            if (main != 0) all.Add(main);
            keys = all.ToArray();
            string[] names = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++) names[i] = NameOf(keys[i]);
            canonical = string.Join("+", names);
            return true;
        }
    }
}
