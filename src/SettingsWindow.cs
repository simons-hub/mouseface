using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MouseFace
{
    // Window sizes in pixels at 100% scaling, turned into real pixels for this screen. Text already
    // grows with Windows' scaling; without this the boxes around it would not.
    internal static class Ui
    {
        internal static readonly float Scale = ReadScale();

        static float ReadScale()
        {
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }
            catch (Exception) { return 1f; }
        }

        internal static int Dpi { get { return (int)Math.Round(Scale * 96); } }
        internal static int S(int px) { return (int)Math.Round(px * Scale); }
        internal static Padding Pad(int all) { return new Padding(S(all)); }
        internal static Padding Pad(int left, int top, int right, int bottom) { return new Padding(S(left), S(top), S(right), S(bottom)); }
        internal static Size Sz(int width, int height) { return new Size(S(width), S(height)); }
        internal static Point Pt(int x, int y) { return new Point(S(x), S(y)); }
    }

    // A window that keeps the right size on every screen. It is built for the main screen's scaling
    // (Ui.S, and fonts in points), then rescaled whenever it is on a screen scaled differently: when
    // it first appears there, and when it is moved there (Windows then sends WM_DPICHANGED).
    internal class ScaledForm : Form
    {
        readonly int builtDpi = Ui.Dpi;
        int dpi = Ui.Dpi; // what its controls are sized for now
        List<Built> built; // every control as built, noted before the first rescale

        // A control's sizes as built. Each rescale works from these, never from the last rescale, so
        // moving a window back and forth between screens can't make it drift by rounding.
        sealed class Built
        {
            internal Control Control;
            internal Padding Margin, Padding;
            internal Size Size, Min, Max;
            internal Point Location;
            internal Font Font;        // its own font, or null when it shares its parent's
            internal bool Sized;       // its size is set, not worked out from its text
            internal bool Placed;      // its place is set, not given by a layout panel
        }

        internal int Dpi { get { return dpi; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int actual = WindowDpi();
            if (actual > 0 && actual != dpi) RescaleTo(actual);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED)
            {
                int to = (int)((long)m.WParam & 0xFFFF);
                if (to > 0 && to != dpi) RescaleTo(to);
                // Where Windows suggests, so a window dragged across keeps its place under the pointer.
                Native.RECT r = (Native.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                Location = new Point(r.Left, r.Top);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        int WindowDpi()
        {
            try { return (int)Native.GetDpiForWindow(Handle); }
            catch (EntryPointNotFoundException) { return 0; } // Windows 10 before version 1607
        }

        // Sizes the whole window for newDpi: text, margins, padding, set sizes and set places, each
        // from its size as built. Windows sized by their content (all of these) then fit themselves.
        internal void RescaleTo(int newDpi)
        {
            if (newDpi == dpi) return;
            if (built == null) { built = new List<Built>(); Note(this, built); }
            dpi = newDpi;
            float k = newDpi / (float)builtDpi;
            SuspendLayout();
            try
            {
                foreach (Built b in built)
                {
                    Control c = b.Control;
                    if (b.Font != null) c.Font = new Font(b.Font.FontFamily, b.Font.Size * k, b.Font.Style, b.Font.Unit);
                    c.Margin = Times(b.Margin, k);
                    c.Padding = Times(b.Padding, k);
                    c.MaximumSize = Size.Empty; // so the new minimum is never above the old maximum
                    c.MinimumSize = Times(b.Min, k);
                    c.MaximumSize = Times(b.Max, k);
                    if (b.Sized) c.Size = Times(b.Size, k);
                    if (b.Placed) c.Location = new Point((int)Math.Round(b.Location.X * k), (int)Math.Round(b.Location.Y * k));
                }
            }
            finally { ResumeLayout(true); }
            AfterRescale();
        }

        // For a window that lines controls up by their real heights, which follow the text and so
        // don't scale exactly: line them up again.
        protected virtual void AfterRescale() { }

        static void Note(Control c, List<Built> into)
        {
            Built b = new Built();
            b.Control = c;
            b.Margin = c.Margin;
            b.Padding = c.Padding;
            b.Size = c.Size;
            b.Min = c.MinimumSize;
            b.Max = c.MaximumSize;
            b.Location = c.Location;
            b.Font = c.Parent == null || !ReferenceEquals(c.Font, c.Parent.Font) ? c.Font : null;
            // A text box works out only its height from its text; its width is set. Buttons and panels
            // that only ever grow to fit get their built size back too, or they would never shrink.
            b.Sized = c.Parent != null && (!c.AutoSize || c is TextBoxBase || GrowsOnly(c));
            b.Placed = c.Parent != null && c.Dock == DockStyle.None && !(c.Parent is TableLayoutPanel) && !(c.Parent is FlowLayoutPanel);
            into.Add(b);
            foreach (Control child in c.Controls) Note(child, into);
        }

        static bool GrowsOnly(Control c)
        {
            Panel p = c as Panel; // TableLayoutPanel and FlowLayoutPanel too
            if (p != null) return p.AutoSizeMode == AutoSizeMode.GrowOnly;
            Button b = c as Button;
            return b != null && b.AutoSizeMode == AutoSizeMode.GrowOnly;
        }

        static Padding Times(Padding p, float k)
        {
            return new Padding((int)Math.Round(p.Left * k), (int)Math.Round(p.Top * k), (int)Math.Round(p.Right * k), (int)Math.Round(p.Bottom * k));
        }

        static Size Times(Size s, float k) { return new Size((int)Math.Round(s.Width * k), (int)Math.Round(s.Height * k)); }
    }

    // The Settings window, a face for settings.ini. Saving changes only the settings changed in the
    // window, on their own lines, so notes and edits made in the file meanwhile survive. Pressing a
    // mouse button lights up its row.
    internal sealed class SettingsForm : ScaledForm
    {
        const int ContentWidth = 470;
        static readonly Color Pressed = Color.FromArgb(255, 230, 140), Released = Color.FromArgb(200, 235, 200);

        // Each action as shown, and as written to settings.ini.
        static readonly string[] ActionNames = { "Left click", "Right click", "Middle click", "Back button", "Forward button",
                                                 "Double click", "Click lock", "Shortcut", "Disabled" };
        static readonly string[] ActionValues = { "Left", "Right", "Middle", "Back", "Forward", "Double", "ClickLock", "Keys", "None" };
        const int ShortcutIndex = 7;
        static readonly string[] DimKeyNames = { "Shift", "Shift+Alt", "Shift+Win" };

        readonly Func<string, string> save; // the new settings.ini text in; an error, or null once saved
        readonly ComboBox mouse = DropDown(300);
        readonly List<string> mouseValues = new List<string>();
        readonly Panel[] rows = new Panel[5];
        readonly ComboBox[] actions = new ComboBox[5];
        readonly ShortcutBox[] shortcuts = new ShortcutBox[5];
        readonly Label hint;
        const string HintText = "Press a button on your mouse to light up its row.";
        readonly CheckBox wholeName = new CheckBox();
        readonly CheckBox dim = new CheckBox();
        readonly ComboBox dimKey = DropDown(100);
        readonly NumericUpDown step = new NumericUpDown();
        readonly CheckBox popup = new CheckBox();
        readonly Control dimDetails;
        readonly Label error = new Label();
        readonly Dictionary<string, string> shown; // each setting as the window first showed it
        int badShortcut = -1;

        internal SettingsForm(Config c, Func<string, string> saveText, Action editFile)
        {
            save = saveText;
            Text = App.Name + " settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Icon = App.Icon;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None; // sizes are scaled by Ui.S
            Font = new Font("Segoe UI", 10f);

            TableLayoutPanel page = new TableLayoutPanel();
            page.AutoSize = true;
            page.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            page.ColumnCount = 1;
            page.Padding = Ui.Pad(20, 12, 20, 16);

            // Mouse
            TableLayoutPanel top = new TableLayoutPanel();
            top.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            top.ColumnCount = 2;
            top.AutoSize = true;
            top.Dock = DockStyle.Fill;
            top.Margin = Ui.Pad(0);
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Label heading = Heading("Select mouse");
            heading.Margin = Ui.Pad(0, 4, 0, 6);
            Label version = new Label();
            version.Text = "Version " + App.Version;
            version.AutoSize = true;
            version.ForeColor = SystemColors.GrayText;
            version.Anchor = AnchorStyles.Right;
            version.Margin = Ui.Pad(0);
            top.Controls.Add(heading, 0, 0);
            top.Controls.Add(version, 1, 0);
            page.Controls.Add(top);
            FillMice(c.Match.Text);
            mouse.Margin = Ui.Pad(0, 0, 0, 2);
            page.Controls.Add(mouse);

            // Buttons
            page.Controls.Add(Heading("Buttons"));
            hint = Note(HintText);
            page.Controls.Add(hint);
            // Rows are named for the physical buttons, in their order on the mouse. Windows' own names
            // (Left, Right, Middle, Back, Forward) are kept for settings.ini.
            ButtonLayout layout = ButtonLayout.For(c.Match);
            Font bold = new Font("Segoe UI Semibold", 10f);
            foreach (Btn b in layout.Order)
            {
                int i = (int)b;
                rows[i] = new Panel();
                rows[i].Size = Ui.Sz(ContentWidth, 40);
                rows[i].BackColor = SystemColors.Window;
                rows[i].Margin = Ui.Pad(0, 0, 0, 4);
                Label name = new Label();
                name.Text = layout.Names[i];
                name.Font = bold;
                name.TextAlign = ContentAlignment.MiddleLeft;
                actions[i] = DropDown(160);
                actions[i].Items.AddRange(ActionNames);
                actions[i].Location = Ui.Pt(146, 7);
                shortcuts[i] = new ShortcutBox();
                shortcuts[i].SetBounds(Ui.S(316), Ui.S(7), Ui.S(142), Ui.S(26));
                SetAction(i, c.Buttons[i]);
                actions[i].Top = (rows[i].Height - actions[i].Height) / 2;
                // The dropdown draws its text a little below its middle; measured, 3 px at 100%.
                name.SetBounds(Ui.S(12), actions[i].Top + Ui.S(3), Ui.S(128), actions[i].Height);
                shortcuts[i].Top = (rows[i].Height - shortcuts[i].Height) / 2;
                int row = i;
                actions[i].SelectedIndexChanged += delegate
                {
                    shortcuts[row].Visible = actions[row].SelectedIndex == ShortcutIndex;
                    if (shortcuts[row].Visible) shortcuts[row].Focus(); // ready for the keys
                };
                rows[i].Controls.Add(name);
                rows[i].Controls.Add(actions[i]);
                rows[i].Controls.Add(shortcuts[i]);
                page.Controls.Add(rows[i]);
            }
            if (layout.Note != null)
            {
                Label modelNote = Note(layout.Note);
                modelNote.Margin = Ui.Pad(0, 4, 0, 0);
                page.Controls.Add(modelNote);
            }

            // Double click
            page.Controls.Add(Heading("Double click"));
            wholeName.Text = "Select whole names: file names, paths and links";
            wholeName.AutoSize = true;
            wholeName.Checked = c.WholeName;
            page.Controls.Add(wholeName);

            // Dimming
            page.Controls.Add(Heading("Dimming"));
            dim.Text = "Dim the screens with the mouse wheel";
            dim.AutoSize = true;
            dim.Checked = c.Dim != DimKey.Off;
            page.Controls.Add(dim);
            FlowLayoutPanel how = new FlowLayoutPanel();
            how.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            how.AutoSize = true;
            how.WrapContents = false;
            how.Margin = Ui.Pad(20, 2, 0, 0);
            dimKey.Items.AddRange(DimKeyNames);
            dimKey.SelectedIndex = c.Dim == DimKey.ShiftAlt ? 1 : c.Dim == DimKey.ShiftWin ? 2 : 0;
            step.Minimum = 1;
            step.Maximum = 20;
            step.Width = Ui.S(55);
            step.Value = Math.Max(1, Math.Min(20, c.DimStep));
            how.Controls.Add(Inline("Hold"));
            how.Controls.Add(dimKey);
            how.Controls.Add(Inline("and turn the wheel, by"));
            how.Controls.Add(step);
            how.Controls.Add(Inline("% a notch"));
            page.Controls.Add(how);
            popup.Text = "Show the brightness level while dimming";
            popup.AutoSize = true;
            popup.Checked = c.Popup;
            popup.Margin = Ui.Pad(20, 2, 0, 0);
            page.Controls.Add(popup);
            dimDetails = how;
            dim.CheckedChanged += delegate { ShowDimming(); };
            ShowDimming();

            // Mistakes, then the buttons
            error.AutoSize = true;
            error.MaximumSize = Ui.Sz(ContentWidth, 0);
            error.ForeColor = Color.Firebrick;
            error.Margin = Ui.Pad(0, 12, 0, 0);
            error.Visible = false;
            page.Controls.Add(error);

            TableLayoutPanel foot = new TableLayoutPanel();
            foot.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            foot.ColumnCount = 2;
            foot.AutoSize = true;
            foot.Dock = DockStyle.Fill; // as wide as the rows, so Save and Cancel sit at the right edge
            foot.Margin = Ui.Pad(0, 16, 0, 0);
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            LinkLabel file = new LinkLabel();
            file.Text = "Edit settings.ini";
            file.AutoSize = true;
            file.Anchor = AnchorStyles.Left;
            file.LinkClicked += delegate { if (editFile != null) editFile(); };
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.AutoSize = true;
            buttons.WrapContents = false;
            buttons.Margin = Ui.Pad(0);
            Button ok = DialogButton("Save");
            ok.Click += delegate { Save(); };
            Button cancel = DialogButton("Cancel");
            cancel.Click += delegate { Close(); };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += delegate { ActiveControl = ok; }; // nothing starts out highlighted
            foot.Controls.Add(file, 0, 0);
            foot.Controls.Add(buttons, 1, 0);
            page.Controls.Add(foot);

            Controls.Add(page);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            string unused;
            shown = Values(out unused) ?? new Dictionary<string, string>();
        }

        // Each row's dropdown and shortcut box centred in it again, with the name level with the
        // dropdown's text. (The constructor's 3 px nudge made up for the dropdown's height before the
        // window existed; by now its height is its real one, so the same top and height line them up.)
        protected override void AfterRescale()
        {
            for (int i = 0; i < 5; i++)
            {
                if (rows[i] == null) continue;
                actions[i].Top = (rows[i].Height - actions[i].Height) / 2;
                shortcuts[i].Top = (rows[i].Height - shortcuts[i].Height) / 2;
                Control name = rows[i].Controls[0];
                name.Top = actions[i].Top;
                name.Height = actions[i].Height;
            }
        }

        // The mice to choose from: any Evoluent, each one connected now, or all of them.
        void FillMice(string current)
        {
            AddMouse("Any Evoluent mouse", "Evoluent");
            List<MouseDevice> mice = Devices.Mice() ?? new List<MouseDevice>();
            HashSet<string> listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> names = new Dictionary<string, int>();
            foreach (MouseDevice d in mice)
            {
                if (!d.Ids.Known) continue; // nothing to save it by
                string id = d.Ids.ToString();
                if (!listed.Add(id)) continue; // one entry per mouse, not per interface
                string name = Devices.Name(d);
                int n;
                names.TryGetValue(name, out n);
                names[name] = ++n;
                AddMouse(n > 1 ? name + " " + n : name, id);
            }
            AddMouse("All mice", "Any");
            int at = mouseValues.FindIndex(delegate(string v) { return v.Equals(current, StringComparison.OrdinalIgnoreCase); });
            if (at < 0) { AddMouse("As set in settings.ini: " + current, current); at = mouseValues.Count - 1; }
            mouse.SelectedIndex = at;
        }

        void AddMouse(string label, string value) { mouse.Items.Add(label); mouseValues.Add(value); }

        void SetAction(int i, Act a)
        {
            int index;
            switch (a.Kind)
            {
                case ActKind.Button: index = (int)a.Button; break; // Left, Right, Middle, Back, Forward, as listed
                case ActKind.Double: index = 5; break;
                case ActKind.ClickLock: index = 6; break;
                case ActKind.Keys: index = ShortcutIndex; shortcuts[i].Text = a.Text.Substring("Keys ".Length); break;
                default: index = 8; break;
            }
            actions[i].SelectedIndex = index;
            shortcuts[i].Visible = index == ShortcutIndex;
        }

        void ShowDimming()
        {
            dimDetails.Enabled = dim.Checked;
            popup.Enabled = dim.Checked;
        }

        // Every setting the window shows, as settings.ini spells it, keyed "Section/Key"; or null and why.
        Dictionary<string, string> Values(out string problem)
        {
            problem = null;
            badShortcut = -1;
            Dictionary<string, string> v = new Dictionary<string, string>();
            v["Mouse/Match"] = mouseValues[mouse.SelectedIndex];
            for (int i = 0; i < 5; i++)
            {
                string value = ActionValues[actions[i].SelectedIndex];
                if (actions[i].SelectedIndex == ShortcutIndex)
                {
                    ushort[] keys; string canonical, why;
                    if (!KeyNames.TryParseChord(shortcuts[i].Text.Trim(), out keys, out canonical, out why))
                    {
                        problem = (Btn)i + ": " + why + ".";
                        badShortcut = i;
                        return null;
                    }
                    value = "Keys " + canonical;
                }
                v["Buttons/" + (Btn)i] = value;
            }
            v["Double/WholeName"] = wholeName.Checked ? "yes" : "no";
            v["Dimming/Key"] = dim.Checked ? DimKeyNames[dimKey.SelectedIndex] : "Off";
            v["Dimming/Step"] = ((int)step.Value).ToString();
            v["Dimming/Popup"] = popup.Checked ? "yes" : "no";
            return v;
        }

        // The file's text with only the settings that changed in the window written over it. Anything
        // else, including edits made in the file while the window was open, is left as it is.
        internal static string WithChanges(string text, Dictionary<string, string> shown, Dictionary<string, string> now)
        {
            foreach (KeyValuePair<string, string> s in now)
            {
                string was;
                if (shown.TryGetValue(s.Key, out was) && was == s.Value) continue;
                int slash = s.Key.IndexOf('/');
                text = SettingsFile.WithValue(text, s.Key.Substring(0, slash), s.Key.Substring(slash + 1), s.Value);
            }
            return text;
        }

        void Save()
        {
            string problem;
            Dictionary<string, string> now = Values(out problem);
            if (now == null)
            {
                ShowError(problem);
                if (badShortcut >= 0) shortcuts[badShortcut].Focus();
                return;
            }

            string text;
            try { text = File.Exists(SettingsFile.FilePath) ? File.ReadAllText(SettingsFile.FilePath) : Config.DefaultText; }
            catch (Exception ex) { ShowError("Could not read " + SettingsFile.FilePath + ": " + ex.Message); return; }
            string changed = WithChanges(text, shown, now);
            if (changed == text && File.Exists(SettingsFile.FilePath)) { Close(); return; } // nothing to save

            problem = save(changed);
            if (problem != null) { ShowError(problem); return; }
            Close();
        }

        void ShowError(string text)
        {
            error.Text = text;
            error.Visible = true;
        }

        // From the hook thread, as a posted message: light up the row of the button just pressed.
        internal void OnButton(Btn b, bool down, bool fromChosen)
        {
            for (int i = 0; i < 5; i++) rows[i].BackColor = SystemColors.Window;
            if (!fromChosen)
            {
                hint.Text = "That press came from another device, which " + App.Name + " leaves alone.";
                return;
            }
            hint.Text = HintText;
            rows[(int)b].BackColor = down ? Pressed : Released;
        }

        // ---- small builders ------------------------------------------------------------------------

        static Label Heading(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font("Segoe UI Semibold", 11f);
            l.Margin = Ui.Pad(0, 14, 0, 6);
            return l;
        }

        static Label Note(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.MaximumSize = Ui.Sz(ContentWidth, 0);
            l.ForeColor = SystemColors.GrayText;
            l.Margin = Ui.Pad(0, 0, 0, 6);
            return l;
        }

        static Label Inline(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = Ui.Pad(0, 6, 4, 0);
            return l;
        }

        static ComboBox DropDown(int width)
        {
            ComboBox b = new ComboBox();
            b.DropDownStyle = ComboBoxStyle.DropDownList;
            b.Width = Ui.S(width);
            return b;
        }

        internal static Button DialogButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink; // so it shrinks back on a screen scaled smaller
            b.MinimumSize = Ui.Sz(88, 0);
            b.Padding = Ui.Pad(8, 2, 8, 2);
            b.Margin = Ui.Pad(8, 0, 0, 0);
            return b;
        }
    }

    // About: what this is, its version and licence, and where to find the newest one. It never goes
    // online itself; the links open in the browser.
    internal sealed class AboutForm : ScaledForm
    {
        internal AboutForm()
        {
            Text = "About " + App.Name;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Icon = App.Icon;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None; // sizes are scaled by Ui.S
            Font = new Font("Segoe UI", 10f);

            TableLayoutPanel page = new TableLayoutPanel();
            page.AutoSize = true;
            page.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            page.ColumnCount = 1;
            page.Padding = Ui.Pad(24, 18, 24, 16);

            Label name = new Label();
            name.Text = App.Name;
            name.AutoSize = true;
            name.Font = new Font("Segoe UI Semibold", 16f);
            // Windows pads text by an amount that grows with its size, so the big title would start
            // to the right of the lines below it; measured, about 4 px at 100%.
            name.Margin = new Padding(-Ui.S(4), 0, 0, 0);
            page.Controls.Add(name);
            page.Controls.Add(Line("Version " + App.Version, SystemColors.GrayText, 12));
            page.Controls.Add(Line("Remaps your Evoluent mouse's buttons and dims your screens, without Evoluent's drivers.", SystemColors.ControlText, 12));
            page.Controls.Add(Line("Not affiliated with or endorsed by Evoluent LLC.", SystemColors.GrayText, 2));
            page.Controls.Add(Line("MIT License. Copyright (c) 2026 " + App.Name + ".", SystemColors.GrayText, 14));
            page.Controls.Add(Link(App.Home.Replace("https://", ""), App.Home));
            page.Controls.Add(Link("See the latest version", App.Home + "/releases"));

            Button ok = SettingsForm.DialogButton("OK");
            ok.Anchor = AnchorStyles.Right;
            ok.Margin = Ui.Pad(0, 16, 0, 0);
            ok.Click += delegate { Close(); };
            AcceptButton = ok;
            CancelButton = ok;
            page.Controls.Add(ok);

            Controls.Add(page);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        static Label Line(string text, Color color, int below)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.MaximumSize = Ui.Sz(380, 0);
            l.ForeColor = color;
            l.Margin = Ui.Pad(0, 0, 0, below);
            return l;
        }

        static LinkLabel Link(string text, string url)
        {
            LinkLabel l = new LinkLabel();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = Ui.Pad(0, 0, 0, 4);
            l.LinkClicked += delegate
            {
                try { Process.Start(url); }
                catch (Exception ex) { MessageBox.Show("Could not open " + url + ": " + ex.Message, App.Name); }
            };
            return l;
        }
    }

    // Records a shortcut by pressing it, as other key mappers do: click the box, press the keys, and
    // it shows them the way settings.ini spells them, like Ctrl+Shift+T. Holding only modifiers
    // records those alone (they are then held for as long as the mouse button is).
    internal sealed class ShortcutBox : TextBox
    {
        const int EM_SETCUEBANNER = 0x1501;
        Toast toast;
        internal int WindowsKeyHints; // for the tests

        internal ShortcutBox()
        {
            ReadOnly = true;           // typed letters would be ambiguous; keys are recorded instead
            ShortcutsEnabled = false;  // Ctrl+C and the rest are shortcuts to record, not to run
            BackColor = SystemColors.Window;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SendMessage(Handle, EM_SETCUEBANNER, new IntPtr(1), "Press the keys");
        }

        // Keys arrive here first: before the text box's own shortcuts, which would swallow Ctrl+C,
        // Ctrl+V and the like, and before the dialog's Enter and Esc. Only Tab still moves on.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Tab || keyData == (Keys.Tab | Keys.Shift)) return base.ProcessCmdKey(ref msg, keyData);
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.LWin || k == Keys.RWin)
            {
                bool repeat = ((long)msg.LParam & 0x40000000) != 0; // held down: Windows repeats the key
                if (!repeat) ExplainWindowsKey();
                return true;
            }
            string chord = Chord(keyData, Native.KeyDown(0x5B) || Native.KeyDown(0x5C));
            if (chord != null) Text = chord;
            return true;
        }

        // Windows keeps its Windows-key shortcuts for itself and never passes the rest of the keys on,
        // so say so, in a toast that stays up for 2 seconds whatever has the focus.
        void ExplainWindowsKey()
        {
            WindowsKeyHints++;
            if (!IsHandleCreated) return; // the tests: no window, and no keys sent to the real desktop
            KeepStartMenuShut();
            if (toast != null && !toast.IsDisposed) toast.Close();
            toast = new Toast("Windows key", "Shortcuts with the Windows key can't be recorded here. Type them in settings.ini.", this, 2000);
            toast.Show(FindForm());
        }

        // Windows opens the Start menu when the Windows key goes up with no other key pressed in
        // between. Pressing and releasing an unassigned key (0xE8) in between keeps it shut, the way
        // AutoHotkey does, so the Settings window keeps the focus.
        static void KeepStartMenuShut()
        {
            List<Out> mask = new List<Out> { Out.Key(0xE8, true), Out.Key(0xE8, false) };
            Native.INPUT[] inputs = Injector.Build(mask, Injector.Tag, false);
            Native.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.INPUT)));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && toast != null) toast.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnKeyDown(KeyEventArgs e) { e.Handled = true; e.SuppressKeyPress = true; }
        protected override void OnKeyPress(KeyPressEventArgs e) { e.Handled = true; }

        // For the tests: a key press, through the same path as a real one.
        internal bool Press(Keys keyData)
        {
            Message none = new Message();
            return ProcessCmdKey(ref none, keyData);
        }

        // The shortcut as settings.ini spells it, like Ctrl+Shift+T, or null for a key it has no name for.
        internal static string Chord(Keys keyData, bool win)
        {
            List<string> parts = new List<string>();
            if ((keyData & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keyData & Keys.Shift) != 0) parts.Add("Shift");
            if ((keyData & Keys.Alt) != 0) parts.Add("Alt");
            if (win) parts.Add("Win");
            Keys k = keyData & Keys.KeyCode;
            bool modifier = k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin;
            if (!modifier)
            {
                if (!KeyNames.Has((ushort)k)) return null;
                parts.Add(KeyNames.NameOf((ushort)k));
            }
            return parts.Count > 0 ? string.Join("+", parts.ToArray()) : null;
        }
    }

    // A short note that shows for a moment below a control and goes away by itself. It never takes
    // the focus, so it stays up even when another window is in front.
    internal sealed class Toast : ScaledForm
    {
        readonly Timer timer = new Timer();

        internal Toast(string title, string text, Control near, int milliseconds)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; // sizes are scaled by Ui.S
            BackColor = SystemColors.Info;
            ForeColor = SystemColors.InfoText;
            Font = new Font("Segoe UI", 9.5f);

            TableLayoutPanel page = new TableLayoutPanel();
            page.AutoSize = true;
            page.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            page.ColumnCount = 1;
            page.Padding = Ui.Pad(12, 8, 12, 10);
            Label head = new Label();
            head.Text = title;
            head.AutoSize = true;
            head.Font = new Font("Segoe UI Semibold", 9.5f);
            head.Margin = Ui.Pad(0, 0, 0, 2);
            Label body = new Label();
            body.Text = text;
            body.AutoSize = true;
            body.MaximumSize = Ui.Sz(300, 0);
            body.Margin = Ui.Pad(0);
            page.Controls.Add(head);
            page.Controls.Add(body);
            Controls.Add(page);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            // Just below the control, kept on its screen.
            Rectangle area = Screen.FromControl(near).WorkingArea;
            Point at = near.PointToScreen(new Point(0, near.Height + Ui.S(4)));
            Size size = GetPreferredSize(Size.Empty);
            Location = new Point(Math.Max(area.Left, Math.Min(at.X, area.Right - size.Width)), Math.Min(at.Y, area.Bottom - size.Height));

            timer.Interval = milliseconds;
            timer.Tick += delegate { timer.Stop(); Close(); };
            timer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: no focus, no taskbar button
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            ControlPaint.DrawBorder(e.Graphics, ClientRectangle, SystemColors.WindowFrame, ButtonBorderStyle.Solid);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    // What to call each button on screen, and in what order, for the mouse being remapped. A model
    // is listed here only once its buttons have been checked by pressing them.
    internal sealed class ButtonLayout
    {
        internal Btn[] Order;
        internal string[] Names; // by Btn
        internal string Note;

        // Evoluent VerticalMouse 4 Right, wired (1A7C:0191): checked button by button.
        static readonly ButtonLayout VerticalMouse4 = new ButtonLayout
        {
            Order = new Btn[] { Btn.Left, Btn.Middle, Btn.Right, Btn.Forward, Btn.Back },
            Names = new string[] { "Top finger", "Bottom finger", "Middle finger", "Upper thumb", "Wheel click" },
            Note = "Lower thumb button: needs Evoluent's driver, can't be set here.\n"
                + "Speed button: works on its own.",
        };

        // Any other mouse: the usual places for Windows' five buttons.
        static readonly ButtonLayout Usual = new ButtonLayout
        {
            Order = new Btn[] { Btn.Left, Btn.Right, Btn.Middle, Btn.Back, Btn.Forward },
            Names = new string[] { "Left button", "Right button", "Wheel click", "Back thumb", "Forward thumb" },
        };

        internal static ButtonLayout For(MatchRule match)
        {
            List<MouseDevice> mice = Devices.Mice();
            if (mice != null)
                foreach (MouseDevice d in mice)
                    if (d.Ids.Known && d.Ids.Vid == 0x1A7C && d.Ids.Pid == 0x0191 && match.Matches(d.Ids)) return VerticalMouse4;
            return Usual;
        }
    }
}
