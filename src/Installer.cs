using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MouseFace
{
    // MouseFace-Setup.exe is MouseFace.exe itself. Started from anywhere but its own folder, it offers
    // to install, update or uninstall itself, for the current user only and with no admin rights: the
    // program, a sign-in shortcut, a Start menu shortcut and its entry in Installed apps.
    internal static class Installer
    {
        internal static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), App.Name); } }
        internal static string Exe { get { return Path.Combine(Dir, App.Name + ".exe"); } }
        // Where a shortcut goes, or null when Windows has no such folder for this account (never a path
        // relative to wherever setup was started). create: make the folder if it is missing.
        internal static string Link(Environment.SpecialFolder folder, bool create)
        {
            string dir = Environment.GetFolderPath(folder, create ? Environment.SpecialFolderOption.Create : Environment.SpecialFolderOption.None);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, App.Name + ".lnk");
        }

        // True when this copy is not the installed one, so it acts as setup.
        internal static bool IsSetup(string self) { return !SamePath(self, Exe); }

        // The installed version, or null when it is not installed.
        internal static string InstalledVersion()
        {
            if (!File.Exists(Exe)) return null;
            try { return FileVersionInfo.GetVersionInfo(Exe).ProductVersion ?? "?"; }
            catch (Exception) { return "?"; }
        }

        // True when version a is newer than b. A version that can't be read is never newer.
        internal static bool Newer(string a, string b)
        {
            Version va, vb;
            return a != null && b != null && Version.TryParse(a, out va) && Version.TryParse(b, out vb) && va > vb;
        }

        // Why an older setup can't replace the installed one, for the window and the console.
        internal static string DowngradeProblem(string installed)
        {
            return App.Name + " " + installed + " is installed, which is newer than this " + App.Version + ". To use "
                + App.Version + " instead, uninstall " + installed + " first (your settings are kept), then run this again.";
        }

        // Installs (or replaces) the program and its two shortcuts, then starts it. Returns why not, or null.
        internal static string Install(string self)
        {
            string installed = InstalledVersion();
            if (Newer(installed, App.Version)) return DowngradeProblem(installed);
            string conflict = Conflicts.Running();
            if (conflict != null)
                return conflict + " is running. It remaps the mouse too, and the two would fight over every click. "
                    + "Uninstall it first (Settings > Apps > Installed apps), then run this again.";
            // Checked before anything changes: without these folders it could not start at sign-in.
            string startup = Link(Environment.SpecialFolder.Startup, true), menu = Link(Environment.SpecialFolder.Programs, true);
            if (startup == null || menu == null)
                return "Windows has no " + (startup == null ? "Startup" : "Start menu") + " folder for your account, so " + App.Name
                    + " was not installed. Nothing was changed.";
            bool wasRunning;
            if (!QuitRunning(out wasRunning)) return "The running " + App.Name + " did not close. Quit it from its tray icon, then run this again.";
            return Install(self, Dir, startup, menu, wasRunning, Register);
        }

        // The install itself, apart from where it happens and how it is listed, so the tests can run it,
        // a failure and all, in a folder of their own. register lists it in Installed apps: why not, or null.
        internal static string Install(string self, string dir, string startup, string menu, bool wasRunning, Func<string> register)
        {
            string exe = Path.Combine(dir, App.Name + ".exe");
            // What was there before, so an install that fails can put it all back: a first install leaves
            // nothing to start at sign-in, and no program without its Installed apps entry.
            bool first = !File.Exists(exe), hadDir = Directory.Exists(dir);
            string[] links = { startup, menu };
            byte[][] before = { ReadIfThere(startup), ReadIfThere(menu) };
            // The copy in place is kept aside until the new one has started, and put back if anything
            // fails, so a failed update leaves what was there, running as it was.
            string kept = exe + Kept;
            bool keeping = false;
            try
            {
                if (!SamePath(self, exe) && File.Exists(exe))
                {
                    Move(exe, kept);
                    keeping = true;
                }
                Place(self, dir, startup, menu);
                // Still running a moment later, or it counts as failed and the old copy goes back: a copy
                // that stops at once has found a problem (its log says which).
                using (Process started = Start(exe, dir))
                    if (started != null && started.WaitForExit(2000))
                        throw new InvalidOperationException("the new copy stopped as soon as it started (its log says why: " + Log.FilePath + ")");
                if (keeping) { try { File.Delete(kept); } catch (Exception) { } } // else the next install replaces it
                string unlisted = register();
                if (unlisted != null) Log.Write(unlisted); // it still works; only the Installed apps entry is missing
                return null;
            }
            catch (Exception ex)
            {
                bool back = false;
                try
                {
                    if (keeping && File.Exists(kept)) { Delete(exe); Move(kept, exe); back = true; }
                    // A first install has no copy of its own to start again: the one in place is the new
                    // one, which just failed.
                    if (wasRunning && !first && File.Exists(exe)) using (Start(exe, dir)) { }
                }
                catch (Exception) { }
                string left = PutBack(dir, first, hadDir, links, before);
                return Problem(ex, SmartAppControlOn(), Outcome(first, back, IsBlock(ex), left));
            }
        }

        static byte[] ReadIfThere(string file)
        {
            try { return File.Exists(file) ? File.ReadAllBytes(file) : null; }
            catch (Exception) { return null; }
        }

        // Puts back what an install that failed changed. A first install's program goes, and its folder
        // too if the install made it and nothing else is in it (settings.ini or a log the new copy wrote
        // keep it). Each shortcut goes back to what it was: deleted if the install made it, its old
        // contents written back if setup rewrote it. Returns what could not be deleted, or null.
        internal static string PutBack(string dir, bool first, bool hadDir, string[] links, byte[][] before)
        {
            List<string> left = new List<string>();
            string exe = Path.Combine(dir, App.Name + ".exe");
            if (first)
            {
                try { Delete(exe); }
                catch (Exception) { left.Add(exe); }
            }
            for (int i = 0; i < links.Length; i++)
            {
                if (links[i] == null) continue;
                try
                {
                    if (before[i] == null) Delete(links[i]);
                    else File.WriteAllBytes(links[i], before[i]);
                }
                catch (Exception) { if (before[i] == null) left.Add(links[i]); } // a rewrite that won't go back still starts MouseFace
            }
            if (!hadDir) { try { Directory.Delete(dir); } catch (Exception) { } } // fails, as it should, unless empty
            return left.Count == 0 ? null : string.Join(", ", left.ToArray());
        }

        // What a failed install left, as setup says it. left: what could not be deleted, or null.
        internal static string Outcome(bool first, bool back, bool blocked, string left)
        {
            List<string> parts = new List<string>();
            if (back)
            {
                parts.Add("The copy you had is back in place.");
                // Windows' rule is likely to stop that copy too (as with a first install that 1.0.0 left
                // behind when Windows blocked it).
                if (blocked) parts.Add("If Windows blocks that one too, run this again and choose Uninstall to remove it.");
            }
            else if (first && left == null) parts.Add("Nothing was installed.");
            if (left != null) parts.Add("Delete what it left by hand: " + left + ".");
            return parts.Count == 0 ? null : string.Join(" ", parts.ToArray());
        }

        // Ends Windows' words (or ours) with one full stop: .NET drops the one Windows has.
        static string Sentence(string text) { return text.Trim().TrimEnd('.') + "."; }

        static bool IsBlock(Exception ex)
        {
            Win32Exception w = ex as Win32Exception;
            return w != null && Blocked(w.NativeErrorCode);
        }

        // Why an install failed, in words a person can act on. When Windows refused to start the new copy,
        // that says which rule did it and what to do; Windows' own words close it, for a bug report.
        // outcome: what was left (the copy put back, nothing installed), or null.
        internal static string Problem(Exception ex, bool smartAppControl, string outcome)
        {
            List<string> parts = new List<string>();
            if (!IsBlock(ex))
            {
                parts.Add("Could not install: " + Sentence(ex.Message));
                if (outcome != null) parts.Add(outcome);
                return string.Join(" ", parts.ToArray());
            }
            // AppLocker and software restriction policies (1260) are a different rule: turning Smart App
            // Control off would not help.
            if (smartAppControl && ((Win32Exception)ex).NativeErrorCode != 1260)
            {
                // Only what happened: the README says how to go on, and what that costs.
                parts.Add("Windows' Smart App Control stopped " + App.Name + " from starting. It runs only apps that Microsoft already "
                    + "knows or that are code-signed, and this release of " + App.Name + " isn't signed yet.");
                if (outcome != null) parts.Add(outcome);
                return string.Join(" ", parts.ToArray());
            }
            else
            {
                parts.Add("Windows stopped " + App.Name + " from starting: a rule on this PC lets only approved apps run, and this "
                    + "release of " + App.Name + " isn't code-signed yet.");
                if (outcome != null) parts.Add(outcome);
                parts.Add("If your work or school manages this PC, ask its administrator to allow it.");
            }
            parts.Add("(Windows said: \"" + Sentence(System.Text.RegularExpressions.Regex.Replace(ex.Message, @"\s+", " ")) + "\")");
            return string.Join(" ", parts.ToArray());
        }

        // Windows' errors for a program an app control rule won't start: Smart App Control and App Control
        // for Business (4551; by reputation 4556 to 4559: malicious, unwanted, dangerous extension, reputation
        // service unreachable; and 4580 to 4582: unfriendly file, reputation lookup failed, explicitly
        // denied), and AppLocker or a software restriction policy (1260). Not the codes between, which are
        // Virtual Secure Mode's and others, nor 4583, a driver check.
        internal static bool Blocked(int error)
        {
            return error == 1260 || error == 4551 || (error >= 4556 && error <= 4559) || (error >= 4580 && error <= 4582);
        }

        // Whether Smart App Control is on, as Windows Security shows it (VerifiedAndReputablePolicyState:
        // 0 off, 1 on, 2 evaluation, which only watches). Only read; false when it can't be read.
        internal static bool SmartAppControlOn()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy"))
                {
                    object state = key == null ? null : key.GetValue("VerifiedAndReputablePolicyState");
                    return state is int && (int)state == 1;
                }
            }
            catch (Exception) { return false; }
        }

        internal const string Kept = ".old"; // the copy an update replaces, until the new one runs

        // A true move (rename), never a copy, tried for a moment while a program lets go of the file.
        static void Move(string from, string to)
        {
            Retry(delegate { if (!Native.MoveFileEx(from, to, Native.MOVEFILE_REPLACE_EXISTING)) throw new Win32Exception(); });
        }

        // Through the shell, as the shortcuts start it: nothing of this process (its console or the output
        // a script is reading) is handed down, so a script waiting on setup's output is not left waiting on
        // MouseFace too.
        static Process Start() { return Start(Exe, Dir); }
        static Process Start(string exe, string dir) { return Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true }); }

        // Removes the program, its shortcuts and its Installed apps entry, keeping settings.ini and the
        // log. Returns why not, or null. Installed apps runs the installed copy itself to do this.
        internal static string Uninstall(string self)
        {
            bool wasRunning;
            if (!QuitRunning(out wasRunning)) return "The running " + App.Name + " did not close. Quit it from its tray icon, then try again.";
            string left;
            try { left = Remove(self, Dir, Link(Environment.SpecialFolder.Startup, false), Link(Environment.SpecialFolder.Programs, false)); }
            catch (Exception ex)
            {
                // Nothing else has changed, so it can be run again; meanwhile the copy that was running
                // runs again, rather than leave the mouse plain.
                try { if (wasRunning && File.Exists(Exe)) using (Start()) { } } catch (Exception) { }
                return "Could not uninstall: " + ex.Message;
            }
            // The program is gone, so its entry goes too, whatever else is left.
            List<string> problems = new List<string>();
            if (left != null) problems.Add("could not delete " + left + " (delete it by hand)");
            try { Unregister(); }
            catch (Exception ex) { problems.Add("could not remove its entry from Installed apps (" + ex.Message + ")"); }
            return problems.Count == 0 ? null : App.Name + " is uninstalled, but " + string.Join(", and ", problems.ToArray()) + ".";
        }

        // ---- the entry in Settings > Apps > Installed apps --------------------------------------------

        // Under the current user, so it needs no admin rights; uninstalling removes it.
        internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + App.Name;

        // What Installed apps shows, and how it uninstalls: --remove asks first, --uninstall doesn't
        // (for scripts and tools like winget).
        internal static Dictionary<string, object> UninstallEntry(string exe, string dir, long bytes, DateTime installed)
        {
            Dictionary<string, object> v = new Dictionary<string, object>();
            v["DisplayName"] = App.Name;
            v["DisplayVersion"] = App.Version;
            v["Publisher"] = App.Publisher;
            v["DisplayIcon"] = exe;
            v["InstallLocation"] = dir;
            v["InstallDate"] = installed.ToString("yyyyMMdd", CultureInfo.InvariantCulture); // the Gregorian date, whatever the user's calendar
            v["UninstallString"] = "\"" + exe + "\" --remove";
            v["QuietUninstallString"] = "\"" + exe + "\" --uninstall";
            v["URLInfoAbout"] = App.Home;
            v["NoModify"] = 1;
            v["NoRepair"] = 1;
            v["EstimatedSize"] = (int)Math.Max(1, bytes / 1024); // in KB
            return v;
        }

        // Adds (or refreshes) the installed program's entry. Returns why not, or null.
        internal static string Register()
        {
            try
            {
                // Built first: with no program in place this fails before an empty key is made.
                Dictionary<string, object> entry = UninstallEntry(Exe, Dir, new FileInfo(Exe).Length, DateTime.Now);
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
                    foreach (KeyValuePair<string, object> v in entry)
                        key.SetValue(v.Key, v.Value, v.Value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
                return null;
            }
            catch (Exception ex) { return "Could not add " + App.Name + " to Installed apps: " + ex.Message; }
        }

        static void Unregister() { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); }

        // ---- the file steps, apart from where they happen so the tests can use a folder of their own -

        internal static void Place(string self, string dir, params string[] links)
        {
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, App.Name + ".exe");
            if (!SamePath(self, exe))
            {
                // Written to a new file, then renamed over the old one. Written, not copied: a copy would
                // carry the download's "came from the internet" mark, and Windows would then ask "are you
                // sure?" at every sign-in; the rename replaces the old file whole, any mark it had with it.
                // Until then the old program stays as it was, so a write that fails (a full disk, or
                // antivirus taking the new file) leaves it working.
                string fresh = exe + ".new";
                try
                {
                    File.WriteAllBytes(fresh, File.ReadAllBytes(self));
                    Retry(delegate { if (!Native.MoveFileEx(fresh, exe, Native.MOVEFILE_REPLACE_EXISTING)) throw new Win32Exception(); });
                }
                finally { try { if (File.Exists(fresh)) File.Delete(fresh); } catch (Exception) { } }
            }
            try { File.Delete(exe + Aside); } catch (Exception) { } // left by an uninstall (see MoveAside)
            foreach (string link in links) if (link != null) Shortcut(link, exe, dir);
        }

        // self is the running program. The program goes first: if it can't, nothing else has changed. A
        // shortcut that won't go doesn't stop the rest; those are returned, or null.
        internal static string Remove(string self, string dir, params string[] links)
        {
            string exe = Path.Combine(dir, App.Name + ".exe");
            if (SamePath(self, exe)) MoveAside(exe);
            else Delete(exe);
            try { File.Delete(exe + Kept); } catch (Exception) { } // left by an update that couldn't tidy up
            List<string> left = new List<string>();
            foreach (string link in links)
            {
                if (link == null) continue;
                try { Delete(link); }
                catch (Exception) { left.Add(link); }
            }
            return left.Count == 0 ? null : string.Join(", ", left.ToArray());
        }

        internal const string Aside = ".uninstalled";

        // A running program can't delete its own file, but it can move it within its drive. So the
        // installed copy, uninstalling itself, moves its file to the temp folder, for Disk Cleanup or
        // Storage Sense to delete; or, when that is on another drive, renames it where it is, for the
        // next install to delete. Never a copy and a delete, which would fail and leave it where it was.
        internal static string MoveAside(string exe)
        {
            int me;
            using (Process current = Process.GetCurrentProcess()) me = current.Id;
            string aside = Path.Combine(Path.GetTempPath(), App.Name + "-" + me + Aside);
            if (Native.MoveFileEx(exe, aside, Native.MOVEFILE_REPLACE_EXISTING)) return aside;
            aside = exe + Aside;
            if (!Native.MoveFileEx(exe, aside, Native.MOVEFILE_REPLACE_EXISTING)) throw new Win32Exception();
            return aside;
        }

        // A program that has just exited can keep its file in use for a moment, so a file step is tried
        // for up to two seconds.
        static void Retry(Action step)
        {
            for (int i = 0; ; i++)
            {
                try { step(); return; }
                catch (Exception ex)
                {
                    if (i == 20 || !(ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception)) throw;
                }
                Thread.Sleep(100);
            }
        }

        // Read-only is cleared first: it only stops a delete, and these files are this program's own.
        static void Delete(string file)
        {
            Retry(delegate { if (File.Exists(file)) { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } });
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        // Asks the running copy in this sign-in to let go of everything and exit, and waits up to 10 s
        // for it. Copies in other people's sign-ins are theirs, and are left alone.
        static bool QuitRunning(out bool wasRunning)
        {
            EventWaitHandle quit;
            wasRunning = EventWaitHandle.TryOpenExisting(Program.QuitEventName, out quit);
            if (!wasRunning) return true;
            using (quit) quit.Set();
            int me, session;
            using (Process current = Process.GetCurrentProcess()) { me = current.Id; session = current.SessionId; }
            for (int i = 0; i < 100; i++)
            {
                bool any = false;
                foreach (Process p in Process.GetProcessesByName(App.Name))
                {
                    try { if (p.Id != me && p.SessionId == session) any = true; }
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

    // The setup window: Install, or Update/Reinstall and Uninstall when it is already installed. A
    // newer installed version is never replaced from here: it has to be uninstalled first.
    internal sealed class SetupForm : ScaledForm
    {
        readonly string self;

        internal SetupForm(string selfPath)
        {
            self = selfPath;
            string installed = Installer.InstalledVersion();
            bool newerInstalled = Installer.Newer(installed, App.Version);
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
                ? "It's set up just for you on this PC, and starts by itself whenever you sign in to Windows. "
                    + (File.Exists(Path.Combine(Installer.Dir, "settings.ini")) ? "Your settings from before are kept." : "Its Settings window opens once it is installed.")
                : newerInstalled ? "This setup is the older version " + App.Version + ". To use it instead, uninstall " + installed
                    + " first, then run this again. Your settings are kept."
                : installed == App.Version ? "You can reinstall it or uninstall it."
                : "You can update it to " + App.Version + " or uninstall it. Your settings are kept either way.";
            page.Controls.Add(body);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.AutoSize = true;
            buttons.WrapContents = false;
            buttons.Anchor = AnchorStyles.Right;
            buttons.Margin = Ui.Pad(0, 20, 0, 0);
            Button go = null;
            if (!newerInstalled)
            {
                go = SettingsForm.DialogButton(installed == null ? "Install" : installed == App.Version ? "Reinstall" : "Update");
                go.Click += delegate { Run(Installer.Install(self), null); };
                buttons.Controls.Add(go);
            }
            if (installed != null)
            {
                Button remove = SettingsForm.DialogButton("Uninstall");
                remove.Click += delegate
                {
                    Run(Installer.Uninstall(self), App.Name + " is uninstalled. Your settings are still in " + Installer.Dir
                        + "; delete that folder to remove them too.");
                };
                buttons.Controls.Add(remove);
            }
            Button cancel = SettingsForm.DialogButton("Cancel");
            cancel.Click += delegate { Close(); };
            buttons.Controls.Add(cancel);
            AcceptButton = go ?? cancel;
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
