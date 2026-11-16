using System;
using System.Drawing;
using System.Windows.Forms;

namespace MouseFace
{
    // The tray icon and its menu, the only window the program normally shows.
    internal sealed class TrayUi : IDisposable
    {
        readonly NotifyIcon icon = new NotifyIcon();
        readonly Icon activeIcon, pausedIcon;
        readonly ToolStripMenuItem pauseItem;
        bool paused;

        internal Action<bool> PauseChanged;
        internal Action ResetBrightness, Settings, About, Quit;

        internal TrayUi()
        {
            // The size the tray draws at on this screen (20 px at 125%), so Windows needn't stretch one.
            activeIcon = App.LoadIcon(App.Name + ".ico", SystemInformation.SmallIconSize);
            pausedIcon = App.LoadIcon(App.Name + "-paused.ico", SystemInformation.SmallIconSize);
            ContextMenuStrip menu = new ContextMenuStrip();
            pauseItem = new ToolStripMenuItem("Pause", null, delegate { SetPaused(!paused); if (PauseChanged != null) PauseChanged(paused); });
            menu.Items.Add(pauseItem);
            menu.Items.Add("Reset brightness", null, delegate { if (ResetBrightness != null) ResetBrightness(); });
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem settings = new ToolStripMenuItem("Settings...", null, delegate { if (Settings != null) Settings(); });
            settings.Font = new Font(settings.Font, FontStyle.Bold); // the default action, as on a double click
            menu.Items.Add(settings);
            menu.Items.Add("About " + App.Name, null, delegate { if (About != null) About(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit " + App.Name, null, delegate { if (Quit != null) Quit(); });
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += delegate { if (Settings != null) Settings(); };
            icon.Icon = activeIcon;
            SetPaused(false);
        }

        internal void Show() { icon.Visible = true; }

        internal void SetPaused(bool value)
        {
            paused = value;
            pauseItem.Text = paused ? "Resume" : "Pause";
            icon.Icon = paused ? pausedIcon : activeIcon;
            icon.Text = App.Name + (paused ? ": paused" : "");
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            activeIcon.Dispose();
            pausedIcon.Dispose();
        }
    }
}
