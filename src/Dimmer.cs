using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace MouseFace
{
    // Screen dimming: a black, see-through, click-through layer over each monitor (not the backlight).
    // It shows only while the chosen mouse is attached, dimming is switched on and the program is not
    // paused; otherwise the screens are at full brightness and the level waits for its return.
    // Nothing here runs on a timer: it reacts to the wheel, to the mouse coming and going, and, while
    // dimmed, to windows appearing that could cover the layer.
    internal sealed class Dimmer
    {
        internal const int MinLevel = 10, MaxLevel = 100;

        readonly List<Shade> shades = new List<Shade>();
        readonly IntPtr ui; // posts the coalesced "put the layer back on top"
        readonly Native.WinEventProc onWinEvent;
        string screensKey = "";
        Hud hud;
        int level = MaxLevel, remainder, step = 3;
        bool enabled, popup = true, present, paused, placePosted;
        IntPtr foregroundHook, showHook;

        internal Dimmer(IntPtr uiWindow)
        {
            ui = uiWindow;
            onWinEvent = OnWinEvent;
            level = LoadLevel();
        }

        internal int Level { get { return level; } }
        internal bool Showing { get { return shades.Count > 0 && shades[0].Visible; } }

        internal static bool Wanted(int level, bool enabled, bool present, bool paused)
        {
            return level < MaxLevel && enabled && present && !paused;
        }

        internal void Configure(bool dimEnabled, bool showPopup, int stepPercent) { enabled = dimEnabled; popup = showPopup; step = stepPercent; Apply(); }
        internal void SetPresent(bool value) { present = value; Apply(); }
        internal void SetPaused(bool value) { paused = value; Apply(); }

        // From the hook thread, as a posted message: one wheel turn while the chord is held.
        internal void Adjust(int delta)
        {
            if (!enabled || !present || paused) return;
            int next = StepLevel(level, delta, step, ref remainder);
            if (next != level) { level = next; SaveLevel(level); Apply(); }
            if (popup) { if (hud == null) hud = new Hud(); hud.ShowLevel(level); }
        }

        internal void Reset()
        {
            level = MaxLevel;
            remainder = 0;
            SaveLevel(level);
            Apply();
        }

        // One wheel notch (120) moves one step; high-resolution wheels' smaller steps add up first.
        internal static int StepLevel(int level, int delta, int step, ref int remainder)
        {
            remainder += delta;
            int notches = remainder / 120;
            remainder -= notches * 120;
            int next = level + notches * step;
            return next < MinLevel ? MinLevel : (next > MaxLevel ? MaxLevel : next);
        }

        internal void Apply()
        {
            if (!Wanted(level, enabled, present, paused))
            {
                foreach (Shade s in shades) s.Hide();
                Unwatch();
                return;
            }
            SyncShades();
            double opacity = (MaxLevel - level) / 100.0;
            foreach (Shade s in shades)
            {
                s.Opacity = opacity;
                if (!s.Visible) s.Show();
                s.Place();
            }
            WatchForCovers();
        }

        internal void OnDisplayChanged() { if (Wanted(level, enabled, present, paused)) Apply(); }

        internal void HideAll()
        {
            Unwatch();
            foreach (Shade s in shades) s.Hide();
        }

        // ---- staying on top, without polling -------------------------------------------------------
        // Another always-on-top window that appears later sits above the layer. While dimmed, Windows
        // reports each window that comes to the front or appears, and the layer goes back on top.

        void WatchForCovers()
        {
            if (foregroundHook != IntPtr.Zero) return;
            uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
            foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, onWinEvent, 0, 0, flags);
            showHook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, onWinEvent, 0, 0, flags);
        }

        void Unwatch()
        {
            if (foregroundHook != IntPtr.Zero) { Native.UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
            if (showHook != IntPtr.Zero) { Native.UnhookWinEvent(showHook); showHook = IntPtr.Zero; }
        }

        void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != Native.OBJID_WINDOW || hwnd == IntPtr.Zero) return;
            if (evt == Native.EVENT_OBJECT_SHOW)
            {
                // Only new always-on-top windows can cover the layer.
                if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return;
                if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) == 0) return;
            }
            if (placePosted) return; // a burst of events needs one re-placing
            placePosted = true;
            Native.PostMessage(ui, UiMsg.Place, IntPtr.Zero, IntPtr.Zero);
        }

        internal void PlaceOnTop()
        {
            placePosted = false;
            if (!Showing) return;
            foreach (Shade s in shades) s.Place();
        }

        // One layer per monitor, rebuilt when the set of monitors changes (KVM, dock, projector).
        void SyncShades()
        {
            Screen[] screens = Screen.AllScreens;
            StringBuilder key = new StringBuilder();
            foreach (Screen s in screens) key.Append(s.Bounds.ToString()).Append(';');
            if (key.ToString() == screensKey && shades.Count > 0) return;
            screensKey = key.ToString();
            foreach (Shade old in shades) old.Dispose();
            shades.Clear();
            foreach (Screen s in screens)
            {
                Shade shade = new Shade(s.Bounds);
                Native.SetWindowDisplayAffinity(shade.Handle, Native.WDA_EXCLUDEFROMCAPTURE); // screenshots stay at full brightness
                shades.Add(shade);
            }
        }

        static string LevelFile { get { return Path.Combine(App.DataDir, "brightness.txt"); } }

        static int LoadLevel()
        {
            try
            {
                int v;
                if (File.Exists(LevelFile) && int.TryParse(File.ReadAllText(LevelFile).Trim(), out v))
                    return Math.Max(MinLevel, Math.Min(MaxLevel, v));
            }
            catch (Exception) { }
            return MaxLevel;
        }

        static void SaveLevel(int v)
        {
            try { File.WriteAllText(LevelFile, v.ToString()); } catch (Exception) { }
        }

        internal static float MonitorScale(Point p)
        {
            try
            {
                Native.POINT pt; pt.x = p.X; pt.y = p.Y;
                uint dx, dy;
                if (Native.GetDpiForMonitor(Native.MonitorFromPoint(pt, 2), 0, out dx, out dy) == 0) return dx / 96f;
            }
            catch (Exception) { }
            return 1f;
        }
    }

    // The black layer over one monitor. Excluded from screen capture, never takes focus, and clicks
    // go straight through it.
    internal sealed class Shade : Form
    {
        readonly Rectangle area;

        internal Shade(Rectangle bounds)
        {
            area = bounds;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Opacity = 0.5; // anything below 1 makes WinForms treat it as layered from the start
            TopMost = true;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= Native.OverlayStyles; return cp; }
        }

        internal void Place()
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, area.X, area.Y, area.Width, area.Height, Native.SWP_NOACTIVATE);
        }
    }

    // The brightness pop-up near the bottom of the screen the pointer is on.
    internal sealed class Hud : Form
    {
        readonly Timer hideTimer = new Timer(); // one-shot: hides the pop-up after the last wheel turn
        int level;
        float scale = 1f;

        internal Hud()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 32, 32);
            Opacity = 0.92;
            TopMost = true;
            DoubleBuffered = true;
            hideTimer.Interval = 1200;
            hideTimer.Tick += delegate { hideTimer.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= Native.OverlayStyles; return cp; }
        }

        internal void ShowLevel(int value)
        {
            level = value;
            Point cursor = Cursor.Position;
            Rectangle r = Screen.FromPoint(cursor).Bounds;
            scale = Dimmer.MonitorScale(cursor);
            int w = (int)(280 * scale), h = (int)(64 * scale);
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            if (!Visible) Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.X + (r.Width - w) / 2, r.Bottom - h - (int)(140 * scale), w, h, Native.SWP_NOACTIVATE);
            Invalidate();
            hideTimer.Stop();
            hideTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int pad = (int)(14 * scale);
            int barH = Math.Max(4, (int)(6 * scale));
            int barW = ClientSize.Width - 2 * pad;
            int barY = ClientSize.Height - pad - barH;
            using (Font f = new Font("Segoe UI", 16f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Brush text = new SolidBrush(Color.White))
            using (Brush track = new SolidBrush(Color.FromArgb(80, 80, 80)))
            using (Brush fill = new SolidBrush(Color.FromArgb(235, 235, 235)))
            {
                g.DrawString("Brightness  " + level + "%", f, text, pad, pad * 0.6f);
                g.FillRectangle(track, pad, barY, barW, barH);
                g.FillRectangle(fill, pad, barY, barW * level / 100, barH);
            }
        }
    }
}
