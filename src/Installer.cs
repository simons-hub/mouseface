using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MouseFace
{
    // MouseFace-Setup.exe is MouseFace.exe itself. Started from anywhere but its own folder, it offers
    // to install, update or uninstall itself, for the current user only: no admin rights and nothing
    // in the registry, just the program, a sign-in shortcut and a Start menu shortcut.
    internal static class Installer
    {
        internal static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), App.Name); } }
        internal static string Exe { get { return Path.Combine(Dir, App.Name + ".exe"); } }
        internal static string StartupLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), App.Name + ".lnk"); } }
        internal static string MenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), App.Name + ".lnk"); } }

        // True when this copy is not the installed one, so it acts as setup.
        internal static bool IsSetup(string self)
        {
            return !string.Equals(Path.GetFullPath(self), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);
        }

        // The installed version, or null when it is not installed.
        internal static string InstalledVersion()
        {
            if (!File.Exists(Exe)) return null;
            try { return FileVersionInfo.GetVersionInfo(Exe).ProductVersion ?? "?"; }
            catch (Exception) { return "?"; }
        }

        // Installs (or replaces) the program and its two shortcuts, then starts it. Returns why not, or null.
        internal static string Install(string self)
        {
            string conflict = Conflicts.Running();
            if (conflict != null)
                return conflict + " is running. It remaps the mouse too, and the two would fight over every click. "
                    + "Uninstall it first (Settings > Apps > Installed apps), then run this again.";
            if (!QuitRunning()) return "The running " + App.Name + " did not close. Quit it from its tray icon, then run this again.";
            try
            {
                Place(self, Dir, StartupLink, MenuLink);
                Process.Start(new ProcessStartInfo(Exe) { WorkingDirectory = Dir, UseShellExecute = false });
                return null;
            }
            catch (Exception ex) { return "Could not install: " + ex.Message; }
        }

        // Removes the program and its shortcuts, keeping settings.ini and the log. Returns why not, or null.
        internal static string Uninstall()
        {
            if (!QuitRunning()) return "The running " + App.Name + " did not close. Quit it from its tray icon, then try again.";
            try { Remove(Dir, StartupLink, MenuLink); return null; }
            catch (Exception ex) { return "Could not uninstall: " + ex.Message; }
        }

        // The file steps, apart from where they happen so the tests can use a folder of their own.
        internal static void Place(string self, string dir, params string[] links)
        {
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, App.Name + ".exe");
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) File.Copy(self, exe, true);
            foreach (string link in links) Shortcut(link, exe, dir);
        }

        internal static void Remove(string dir, params string[] links)
        {
            foreach (string link in links) if (File.Exists(link)) File.Delete(link);
            string exe = Path.Combine(dir, App.Name + ".exe");
            if (File.Exists(exe)) File.Delete(exe);
        }

        // Asks a running copy to let go of everything and exit, and waits up to 10 s for it.
        static bool QuitRunning()
        {
            EventWaitHandle quit;
            if (!EventWaitHandle.TryOpenExisting(Program.QuitEventName, out quit)) return true; // not running
            using (quit) quit.Set();
            for (int i = 0; i < 100; i++)
            {
                bool any = false;
                foreach (Process p in Process.GetProcessesByName(App.Name))
                {
                    try { if (p.Id != Process.GetCurrentProcess().Id) any = true; }
                    finally { p.Dispose(); }
                }
                if (!any) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        // A .lnk through the shell's own scripting object, as Windows' setup tools make them.
        static void Shortcut(string path, string target, string workingDir)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            object link = null;
            try
            {
                link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workingDir });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { App.Name + ": mouse button remapper and screen dimmer" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    // The setup window: Install, or Update/Reinstall and Uninstall when it is already installed.
    internal sealed class SetupForm : Form
    {
        readonly string self;

        internal SetupForm(string selfPath)
        {
            self = selfPath;
            string installed = Installer.InstalledVersion();
            Text = App.Name + " setup";
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

            Label title = new Label();
            title.AutoSize = true;
            title.Font = new Font("Segoe UI Semibold", 14f);
            title.Margin = Ui.Pad(0, 0, 0, 10);
            title.Text = installed == null ? "Install " + App.Name + " " + App.Version : App.Name + " " + installed + " is installed";
            page.Controls.Add(title);

            Label body = new Label();
            body.AutoSize = true;
            body.MaximumSize = Ui.Sz(400, 0);
            body.Text = installed == null
                ? "It installs for you only, needs no admin rights, and starts when you sign in. Its Settings window opens once it is installed."
                : installed == App.Version ? "You can reinstall it or uninstall it." : "You can update it to " + App.Version + " or uninstall it. Your settings are kept either way.";
            page.Controls.Add(body);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.WrapContents = false;
            buttons.Anchor = AnchorStyles.Right;
            buttons.Margin = Ui.Pad(0, 20, 0, 0);
            Button go = SettingsForm.DialogButton(installed == null ? "Install" : installed == App.Version ? "Reinstall" : "Update");
            go.Click += delegate { Run(Installer.Install(self), null); };
            buttons.Controls.Add(go);
            if (installed != null)
            {
                Button remove = SettingsForm.DialogButton("Uninstall");
                remove.Click += delegate
                {
                    Run(Installer.Uninstall(), App.Name + " is uninstalled. Your settings are still in " + Installer.Dir
                        + "; delete that folder to remove them too.");
                };
                buttons.Controls.Add(remove);
            }
            Button cancel = SettingsForm.DialogButton("Cancel");
            cancel.Click += delegate { Close(); };
            buttons.Controls.Add(cancel);
            AcceptButton = go;
            CancelButton = cancel;
            page.Controls.Add(buttons);

            Controls.Add(page);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        void Run(string problem, string done)
        {
            if (problem != null) { MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (done != null) MessageBox.Show(this, done, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
    }
}
