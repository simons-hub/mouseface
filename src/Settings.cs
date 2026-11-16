using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MouseFace
{
    internal enum DimKey { Off, Shift, ShiftAlt, ShiftWin }

    // Which mouse gets remapped: any Evoluent, every mouse, or listed vendor[:product] IDs.
    internal sealed class MatchRule
    {
        internal const ushort EvoluentVendor = 0x1A7C;
        readonly bool any;
        readonly List<int[]> ids;
        internal readonly string Text;

        MatchRule(bool any, List<int[]> ids, string text) { this.any = any; this.ids = ids; Text = text; }

        // { vendor, product or -1, 1 if the vendor must be a USB vendor ID }
        internal static readonly MatchRule Evoluent = new MatchRule(false, new List<int[]> { new int[] { EvoluentVendor, -1, 1 } }, "Evoluent");

        internal bool Matches(DeviceIds d)
        {
            if (any) return true;
            if (!d.Known) return false;
            foreach (int[] id in ids)
                if (id[0] == d.Vid && (id[1] < 0 || id[1] == d.Pid) && !(id[2] == 1 && d.SigVendor)) return true;
            return false;
        }

        // "Evoluent", "Any", "1A7C", "1A7C:0191", or several separated by commas.
        internal static bool TryParse(string text, out MatchRule rule, out string error)
        {
            rule = null; error = null;
            string t = (text ?? "").Trim();
            if (t.Equals("Any", StringComparison.OrdinalIgnoreCase)) { rule = new MatchRule(true, new List<int[]>(), "Any"); return true; }
            List<int[]> ids = new List<int[]>();
            List<string> parts = new List<string>();
            foreach (string raw in t.Split(','))
            {
                string p = raw.Trim();
                if (p.Equals("Evoluent", StringComparison.OrdinalIgnoreCase)) { ids.Add(new int[] { EvoluentVendor, -1, 1 }); parts.Add("Evoluent"); continue; }
                string[] vp = p.Split(':');
                int vid, pid = -1;
                if (vp.Length > 2 || !Hex4(vp[0], out vid) || (vp.Length == 2 && !Hex4(vp[1], out pid)))
                {
                    error = "'" + p + "' is not a mouse. Use Evoluent, Any, a vendor ID like 1A7C, or vendor:product like 1A7C:0191";
                    return false;
                }
                ids.Add(new int[] { vid, pid, 0 });
                parts.Add(vid.ToString("X4") + (pid >= 0 ? ":" + pid.ToString("X4") : ""));
            }
            rule = new MatchRule(false, ids, string.Join(", ", parts.ToArray()));
            return true;
        }

        static bool Hex4(string s, out int value)
        {
            value = 0;
            s = s.Trim();
            return s.Length == 4 && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
        }

        public override string ToString() { return Text; }
    }

    // Everything settings.ini can say. Immutable once built: the hook thread reads it while the UI
    // thread may swap in a new one.
    internal sealed class Config
    {
        internal readonly Act[] Buttons = new Act[5]; // by Btn
        internal MatchRule Match = MatchRule.Evoluent;
        internal bool WholeName = true;
        internal DimKey Dim = DimKey.Off;
        internal bool Popup = true;
        internal int DimStep = 3; // percent per wheel notch

        internal Config()
        {
            for (int i = 0; i < 5; i++) Buttons[i] = Act.Same((Btn)i);
        }

        internal Act For(Btn b) { return Buttons[(int)b]; }

        internal bool RemapsAnything
        {
            get { for (int i = 0; i < 5; i++) if (!Buttons[i].IsPlain((Btn)i)) return true; return false; }
        }

        internal bool UsesDouble
        {
            get { foreach (Act a in Buttons) if (a.Kind == ActKind.Double) return true; return false; }
        }

        internal string Summary()
        {
            StringBuilder s = new StringBuilder("mouse " + Match + "; buttons");
            for (int i = 0; i < 5; i++) s.Append(i == 0 ? " " : ", ").Append((Btn)i).Append('=').Append(Buttons[i]);
            s.Append("; whole names ").Append(WholeName ? "on" : "off");
            s.Append("; dimming ").Append(DimText(Dim)).Append(", step ").Append(DimStep).Append("%").Append(Popup ? "" : " (no pop-up)");
            return s.ToString();
        }

        internal static string DimText(DimKey d)
        {
            switch (d) { case DimKey.Shift: return "Shift"; case DimKey.ShiftAlt: return "Shift+Alt"; case DimKey.ShiftWin: return "Shift+Win"; }
            return "Off";
        }

        // Exactly the chord, so Ctrl+Shift+wheel and the rest keep their usual meaning.
        internal static bool IsDimChord(DimKey key, bool shift, bool ctrl, bool alt, bool win)
        {
            if (key == DimKey.Off || !shift || ctrl) return false;
            if (key == DimKey.Shift) return !alt && !win;
            if (key == DimKey.ShiftAlt) return alt && !win;
            return win && !alt;
        }

        // Reads settings.ini. On a mistake returns null and says which line and why.
        internal static Config Parse(string text, out string error)
        {
            error = null;
            Config c = new Config();
            string section = null;
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n];
                int note = line.IndexOf(';');
                if (note >= 0) line = line.Substring(0, note);
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string where = "line " + (n + 1) + ": ";
                if (line.StartsWith("["))
                {
                    if (!line.EndsWith("]")) { error = where + "a section name needs a closing ]"; return null; }
                    section = line.Substring(1, line.Length - 2).Trim();
                    if (Array.IndexOf(new string[] { "mouse", "buttons", "double", "dimming" }, section.ToLowerInvariant()) < 0)
                    {
                        error = where + "[" + section + "] is not a section. The sections are [Mouse], [Buttons], [Double] and [Dimming]";
                        return null;
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) { error = where + "expected a setting like Name = value"; return null; }
                if (section == null) { error = where + "this setting needs a section above it, like [Buttons]"; return null; }
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                string full = section + "." + key;
                if (seen.ContainsKey(full)) { error = where + key + " is set twice in [" + section + "]"; return null; }
                seen[full] = true;
                string problem = Apply(c, section.ToLowerInvariant(), key, value);
                if (problem != null) { error = where + problem; return null; }
            }
            return c;
        }

        static string Apply(Config c, string section, string key, string value)
        {
            string err;
            switch (section)
            {
                case "mouse":
                    if (!key.Equals("Match", StringComparison.OrdinalIgnoreCase)) return "[Mouse] has only Match";
                    MatchRule rule;
                    if (!MatchRule.TryParse(value, out rule, out err)) return err;
                    c.Match = rule;
                    return null;
                case "buttons":
                    Btn b;
                    if (!MouseFace.Buttons.TryParse(key, out b)) return "'" + key + "' is not a button. The buttons are Left, Right, Middle, Back and Forward";
                    Act a;
                    if (!Act.TryParse(value, out a, out err)) return err;
                    c.Buttons[(int)b] = a;
                    return null;
                case "double":
                    if (!key.Equals("WholeName", StringComparison.OrdinalIgnoreCase)) return "[Double] has only WholeName";
                    return YesNo(value, out c.WholeName);
                default: // dimming
                    if (key.Equals("Key", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = value.Replace(" ", "").Replace("+", "").ToLowerInvariant();
                        if (v == "off") c.Dim = DimKey.Off;
                        else if (v == "shift") c.Dim = DimKey.Shift;
                        else if (v == "shiftalt") c.Dim = DimKey.ShiftAlt;
                        else if (v == "shiftwin") c.Dim = DimKey.ShiftWin;
                        else return "'" + value + "' is not a dimming key. Use Shift, Shift+Alt, Shift+Win or Off";
                        return null;
                    }
                    if (key.Equals("Popup", StringComparison.OrdinalIgnoreCase)) return YesNo(value, out c.Popup);
                    if (key.Equals("Step", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (!int.TryParse(value, out n) || n < 1 || n > 20) return "'" + value + "' is not a step. Use a whole number of percent from 1 to 20";
                        c.DimStep = n;
                        return null;
                    }
                    return "[Dimming] has only Key, Step and Popup";
            }
        }

        static string YesNo(string value, out bool result)
        {
            string v = value.ToLowerInvariant();
            result = v == "yes" || v == "true" || v == "on";
            if (result || v == "no" || v == "false" || v == "off") return null;
            return "'" + value + "' should be yes or no";
        }

        // Written on first start. Nothing is remapped until the user says so.
        internal const string DefaultText =
@"; MouseFace settings. Saved changes apply at once.
; Lines starting with ; are notes. Names are not case-sensitive.

[Mouse]
; Which mouse to remap: Evoluent (any Evoluent mouse), Any (all mice), or the
; vendor:product ID of one mouse, like 1A7C:0191. The Settings window sets this.
Match = Evoluent

[Buttons]
; What each button does. Buttons are named by what they send to Windows; in the
; Settings window, pressing a button lights up its row. On a VerticalMouse 4
; Right: Left = top finger, Middle = middle finger, Right = bottom finger,
; Forward = wheel click, Back = upper thumb.
; Each can be Left, Right, Middle, Back, Forward, Double (a double click),
; ClickLock (press once to hold the left button down, press again to let go),
; None (does nothing), or Keys followed by a shortcut, like Keys Ctrl+C.
Left    = Left
Right   = Right
Middle  = Middle
Back    = Back
Forward = Forward

[Double]
; After a Double click, widen the selection to the whole name: file names, paths, links.
WholeName = yes

[Dimming]
; Dim the screens by holding this key and turning the wheel: Shift, Shift+Alt, Shift+Win or Off.
Key = Off
; How much one wheel notch changes the brightness, in percent (1 to 20).
Step = 3
; Show the brightness level while dimming.
Popup = yes
";
    }

    // settings.ini on disk.
    internal static class SettingsFile
    {
        internal static string FilePath { get { return Path.Combine(App.DataDir, "settings.ini"); } }

        // Reads the file, writing the defaults first if there is none. On a mistake returns null and
        // the reason.
        internal static Config Load(out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(FilePath)) Save(Config.DefaultText);
                return Config.Parse(File.ReadAllText(FilePath), out error);
            }
            catch (Exception ex)
            {
                error = "could not read " + FilePath + ": " + ex.Message;
                return null;
            }
        }

        // Writes through a temporary file, so a crash never leaves half a settings file.
        internal static void Save(string text)
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }

        // Changes the Match line under [Mouse], keeping every other line and note as it was.
        internal static string WithMatch(string text, string match) { return WithValue(text, "Mouse", "Match", match); }

        // Sets key = value under [section], keeping every other line, note and alignment as it was.
        // A missing line goes after the section's last setting; a missing section goes at the end.
        internal static string WithValue(string text, string section, string key, string value)
        {
            List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            int start = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i];
                int note = t.IndexOf(';');
                if (note >= 0) t = t.Substring(0, note);
                t = t.Trim();
                if (t.StartsWith("["))
                {
                    if (start >= 0) { lines.Insert(LastSetting(lines, start, i) + 1, key + " = " + value); return Join(lines); }
                    if (t.EndsWith("]") && t.Substring(1, t.Length - 2).Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) start = i;
                    continue;
                }
                if (start >= 0 && t.StartsWith(key, StringComparison.OrdinalIgnoreCase) && t.Substring(key.Length).TrimStart().StartsWith("="))
                {
                    string line = lines[i];
                    int eq = line.IndexOf('=');
                    int tail = line.IndexOf(';', eq);
                    string old = (tail >= 0 ? line.Substring(eq + 1, tail - eq - 1) : line.Substring(eq + 1));
                    string pad = tail >= 0 ? new string(' ', Math.Max(1, old.Length - 1 - value.Length)) : ""; // the note keeps its column
                    lines[i] = line.Substring(0, eq + 1) + " " + value + (tail >= 0 ? pad + line.Substring(tail) : "");
                    return Join(lines);
                }
            }
            if (start >= 0) lines.Insert(LastSetting(lines, start, lines.Count) + 1, key + " = " + value);
            else
            {
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add(key + " = " + value);
                lines.Add("");
            }
            return Join(lines);
        }

        // The last non-blank line from the section header up to (not including) end.
        static int LastSetting(List<string> lines, int header, int end)
        {
            for (int i = end - 1; i > header; i--) if (lines[i].Trim().Length > 0) return i;
            return header;
        }

        static string Join(List<string> lines) { return string.Join("\r\n", lines.ToArray()); }
    }
}
