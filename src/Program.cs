using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MouseFace
{
    // Threads, so a slow part can never stall the mouse:
    //   hook thread      the mouse hook and Raw Input; decides and returns (HookThread)
    //   injector thread  sends the replacement clicks and keys (Injector)
    //   select thread    widens a Double click's selection; may wait on programs (WholeNameSelector)
    //   UI thread        this class: dimming, pop-up, tray, settings, and the only way out
    // It fails to a plain mouse, never into a loop: on any error it lets go of every button it holds,
    // takes the dimming down, says why and exits. Nothing restarts it until the next sign-in.
    internal static class Program
    {
        static readonly Remapper remapper = new Remapper();
        static readonly Injector injector = new Injector();
        static HookThread hook;
        static WholeNameSelector selector;
        static Dimmer dimmer;
        static TrayUi tray;
        static UiWindow ui;
        static SettingsForm settingsForm;
        static AboutForm aboutForm;
        static FileSystemWatcher settingsWatcher;
        static System.Windows.Forms.Timer settingsSettle;
        static readonly BlockingCollection<string> watchLines = new BlockingCollection<string>();
        static bool watching, stopping, paused;
        static int crashed;

        const string Usage = "usage: " + App.Name + " [--watch] [--data-dir DIR] [--quit] [--install] [--uninstall] [--remove] [--register] [--version] [--help]\n"
            + "  --watch          also show each button decision in a console window\n"
            + "  --data-dir DIR   keep settings.ini, the log and the brightness in DIR\n"
            + "  --quit           ask the running copy to let go of everything and exit\n"
            + "  --install        install this copy for the current user and start it, without asking\n"
            + "  --uninstall      remove the program, its shortcuts and its Installed apps entry, keeping the settings\n"
            + "  --remove         the same, asking first (what Settings > Apps > Installed apps runs)\n"
            + "  --register       list the installed program in Installed apps (install.ps1 uses it)\n"
            + "Settings: settings.ini in %LOCALAPPDATA%\\" + App.Name + " (tray menu > Settings).";
        internal const string QuitEventName = @"Local\" + App.Name + ".quit";

        const string NotAsAdmin = App.Name + " didn't start because it was opened with Run as administrator, which it doesn't need. "
            + "Open it the normal way instead.";

        [STAThread]
        static int Main(string[] args)
        {
            // It needs no admin rights, and with them its clicks would reach admin windows too.
            bool elevated = Desk.ElevatedByUac();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--watch") watching = true;
                else if (args[i] == "--data-dir" && i + 1 < args.Length) App.DataDir = args[++i];
                else if (args[i] == "--version") { Console2.Use(); Console.WriteLine(App.Name + " " + App.Version); return 0; }
                else if (args[i] == "--install" || args[i] == "--uninstall")
                {
                    // For scripts: the result goes to the console and the exit code, never to a message box.
                    string problem = elevated ? NotAsAdmin
                        : args[i] == "--install" ? Installer.Install(App.ExePath) : Installer.Uninstall(App.ExePath, false);
                    Console2.Use();
                    Console.WriteLine(problem ?? (args[i] == "--install" ? App.Name + " " + App.Version + " installed and started." : App.Name + " uninstalled; settings kept in " + Installer.Dir + "."));
                    return problem == null ? 0 : 1;
                }
                else if (args[i] == "--register")
                {
                    // install.ps1, after placing the program: list it in Installed apps.
                    string problem = elevated ? NotAsAdmin : Installer.Register();
                    Console2.Use();
                    Console.WriteLine(problem ?? App.Name + " is listed in Settings > Apps > Installed apps.");
                    return problem == null ? 0 : 1;
                }
                else if (args[i] == "--remove")
                {
                    // Uninstall in Settings > Apps > Installed apps: ask first, then say how it went.
                    if (elevated) { Box(NotAsAdmin, true); return 5; }
                    Application.EnableVisualStyles();
                    bool deleteSettings;
                    if (!MessageForm.Ask(null, App.Name, "Uninstall " + App.Name + "?", "Uninstall", "Cancel", Installer.DeleteSettingsText, out deleteSettings)) return 1;
                    string problem = Installer.Uninstall(App.ExePath, deleteSettings);
                    MessageForm.Tell(null, App.Name, problem ?? Installer.UninstalledText(deleteSettings), problem != null);
                    return problem == null ? 0 : 1;
                }
                else if (args[i] == "--quit")
                {
                    EventWaitHandle running;
                    if (!EventWaitHandle.TryOpenExisting(QuitEventName, out running)) return 1; // not running
                    running.Set();
                    return 0;
                }
                else { Console2.Use(); Console.WriteLine(Usage); return args[i] == "--help" ? 0 : 2; }
            }
            if (watching) Console2.Use();
            if (elevated) { Box(NotAsAdmin, true); return 5; }

            // Started from anywhere but its install folder (a download, a build folder), it is setup.
            if (args.Length == 0 && Installer.IsSetup(App.ExePath))
            {
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (Exception) { }
                Application.EnableVisualStyles();
                Application.Run(new SetupForm(App.ExePath));
                return 0;
            }

            bool first;
            Mutex single = new Mutex(false, @"Local\" + App.Name, out first);
            if (!first)
            {
                Box(App.Name + " is already running. Its icon is in the tray, by the clock (it may be under the ^ arrow).", false);
                return 0;
            }

            string conflict = Conflicts.Running();
            if (conflict != null)
            {
                Log.Write("not started: " + conflict + " is running");
                Box(conflict + " is running. It remaps the mouse too, and the two would fight over every click.\n\n"
                    + "Uninstall " + conflict + " (Settings > Apps > Installed apps), then start " + App.Name + " again from the Start menu.", true);
                return 4;
            }

            // Physical pixels on every monitor, so each dimming layer covers its screen exactly.
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                Log.Write("error: " + e.Exception); // the whole of it, for a bug report; the message box gets the gist
                Stop("an error: " + e.Exception.GetType().Name + ": " + e.Exception.Message, false);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { OnCrash(e.ExceptionObject); };
            KeepFast();

            string error;
            bool unreadable;
            bool firstRun = !File.Exists(SettingsFile.FilePath); // then the Settings window opens to set it up
            Config config = SettingsFile.Load(true, out error, out unreadable);
            if (config == null)
            {
                lastSettingsError = error;
                string what = unreadable ? App.Name + " could not read settings.ini" : "settings.ini has a mistake";
                Log.Write(what + ", so nothing is remapped until it is fixed: " + error);
                BoxLater(what + ", so " + App.Name + " is running with nothing remapped and no dimming until it is fixed.\n\n" + error
                    + (unreadable ? "\n\nAnother program may have it open. Saving the file again applies it at once."
                                  : "\n\nTray icon > Settings > Edit settings.ini. Saving the fixed file applies it at once."));
                config = new Config();
            }
            remapper.Config = config;

            ui = new UiWindow();
            dimmer = new Dimmer(ui.Handle);
            hook = new HookThread(remapper, injector);
            hook.Ui = ui.Handle;
            if (watching)
            {
                hook.Watch = delegate(string line) { watchLines.Add(line); };
                Log.Echo = delegate(string line) { watchLines.Add(line); };
                Thread printer = new Thread(delegate() { foreach (string line in watchLines.GetConsumingEnumerable()) Console.WriteLine(line); });
                printer.IsBackground = true;
                printer.Start();
                Console.WriteLine("Press each mouse button, and try the dimming chord. Close this window to stop watching.");
            }
            injector.Start();
            StartSelector(config);
            if (!hook.Start())
            {
                Log.Write("could not install the mouse hook");
                Box(App.Name + " could not install its mouse hook, so it is not running. Details: " + Log.FilePath, true);
                return 3;
            }
            dimmer.Configure(config.Dim != DimKey.Off, config.Popup, config.DimStep);
            dimmer.SetPresent(hook.Present);
            // Raised on a thread of its own choosing, so it is passed to the UI thread.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate { Native.PostMessage(ui.Handle, UiMsg.Display, IntPtr.Zero, IntPtr.Zero); };

            tray = new TrayUi();
            tray.PauseChanged = SetPaused;
            tray.ResetBrightness = delegate { dimmer.Reset(); Log.Write("brightness reset to 100% from the tray"); };
            tray.Settings = ShowSettings;
            tray.About = ShowAbout;
            tray.Quit = delegate { Stop("quit from the tray", true); };
            tray.Show();
            if (firstRun) ShowSettings();

            WatchSettings();
            ListenForQuit();
            Log.Write(App.Name + " " + App.Version + " started; " + config.Summary() + "; chosen mouse " + (hook.Present ? "attached" : "not attached")
                + (watching ? "; watching" : ""));
            if (Native.GetSystemMetrics(Native.SM_SWAPBUTTON) != 0) Log.Write("note: Windows has the left and right buttons swapped (Settings > Mouse)");

            Application.Run();
            GC.KeepAlive(single);
            return 0;
        }

        // Keep Windows from parking this on slow cores to save power: the hook must answer at once.
        static void KeepFast()
        {
            try
            {
                Native.POWER_THROTTLING pt; pt.Version = 1; pt.ControlMask = 1; pt.StateMask = 0; // opt out of EcoQoS
                Native.SetProcessInformation(Native.GetCurrentProcess(), 4, ref pt, (uint)Marshal.SizeOf(typeof(Native.POWER_THROTTLING)));
            }
            catch (Exception) { }
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch (Exception) { }
        }

        static void StartSelector(Config c)
        {
            if (selector != null || !c.WholeName || !c.UsesDouble) return;
            selector = new WholeNameSelector();
            if (watching) selector.Watch = delegate(string line) { watchLines.Add(line); };
            selector.Start();
            hook.Selector = selector;
        }

        // "MouseFace --quit" (the installer uses it) sets a named event; a thread that only waits on it
        // passes the request to the UI thread.
        static void ListenForQuit()
        {
            EventWaitHandle quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            Thread t = new Thread(delegate()
            {
                quit.WaitOne();
                Native.PostMessage(ui.Handle, UiMsg.Quit, IntPtr.Zero, IntPtr.Zero);
            });
            t.IsBackground = true;
            t.Name = "quit";
            t.Start();
        }

        static void SetPaused(bool value)
        {
            paused = value;
            hook.Paused = value;
            if (value) hook.Post(HookThread.WM_APP_RELEASE); // let go of anything held mid-press
            dimmer.SetPaused(value);
            tray.SetPaused(value);
            Log.Write(value ? "paused from the tray: plain mouse, full brightness" : "resumed from the tray");
        }

        // ---- settings ------------------------------------------------------------------------------

        // Windows says when the file changes; a short one-shot wait lets an editor finish saving.
        static void WatchSettings()
        {
            settingsSettle = new System.Windows.Forms.Timer();
            settingsSettle.Interval = 300;
            settingsSettle.Tick += delegate { settingsSettle.Stop(); ReloadSettings(); };
            try
            {
                settingsWatcher = new FileSystemWatcher(App.DataDir, "settings.ini");
                settingsWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                FileSystemEventHandler changed = delegate { Native.PostMessage(ui.Handle, UiMsg.Settings, IntPtr.Zero, IntPtr.Zero); };
                settingsWatcher.Changed += changed;
                settingsWatcher.Created += changed;
                settingsWatcher.Renamed += delegate { Native.PostMessage(ui.Handle, UiMsg.Settings, IntPtr.Zero, IntPtr.Zero); };
                settingsWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Write("settings changes will apply only after a restart: " + ex.Message); }
        }

        static string lastSettingsError;

        static void ReloadSettings()
        {
            if (stopping || !File.Exists(SettingsFile.FilePath)) return;
            string error;
            bool unreadable;
            Config c = SettingsFile.Load(false, out error, out unreadable);
            if (c == null)
            {
                if (error == lastSettingsError) return;
                lastSettingsError = error;
                Log.Write("settings.ini not applied: " + error);
                BoxLater((unreadable ? "settings.ini could not be read" : "The settings you saved were not applied") + ", so nothing has changed.\n\n" + error);
                if (FormOpen(settingsForm)) settingsForm.ShowError(InUseText);
                return;
            }
            lastSettingsError = null;
            if (FormOpen(settingsForm)) { settingsForm.Reload(c); settingsForm.ShowError(null); } // the open window shows a hand edit too
            if (c.Summary() == remapper.Config.Summary()) return; // saved without changes
            remapper.Config = c;
            hook.Post(HookThread.WM_APP_RECONFIG); // the chosen mouse may have changed
            StartSelector(c);
            dimmer.Configure(c.Dim != DimKey.Off, c.Popup, c.DimStep);
            Log.Write("settings applied; " + c.Summary());
        }

        static void EditSettings()
        {
            // The full path, so a notepad.exe planted in the working folder or on PATH is never the one run.
            try { Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + SettingsFile.FilePath + "\""); }
            catch (Exception ex) { Box("Could not open " + SettingsFile.FilePath + ": " + ex.Message, true); }
        }

        static bool FormOpen(Form f) { return f != null && !f.IsDisposed; }

        // Shown in the Settings window while settings.ini can't be used.
        static string InUseText { get { return "settings.ini can't be used, so this shows the settings in use, not the file. " + lastSettingsError; } }

        static void ShowSettings()
        {
            if (!FormOpen(settingsForm))
            {
                settingsForm = new SettingsForm(remapper.Config, SaveSettingsText, EditSettings);
                settingsForm.FormClosed += delegate { hook.ReportButtons = false; };
                if (lastSettingsError != null) settingsForm.ShowError(InUseText);
            }
            hook.ReportButtons = true; // so pressing a mouse button lights up its row
            settingsForm.Show();
            settingsForm.Activate();
        }

        // From the Settings window: checks the new text, saves it and applies it. Returns why not, or null.
        static string SaveSettingsText(string text)
        {
            string error;
            if (Config.Parse(text, out error) == null) return "Not saved: " + error;
            try { SettingsFile.Save(text); }
            catch (Exception ex) { return "Could not save " + SettingsFile.FilePath + ": " + ex.Message; }
            Log.Write("settings saved from the Settings window");
            ReloadSettings(); // the file watcher would too; this is at once
            return null;
        }

        static void ShowAbout()
        {
            if (aboutForm == null || aboutForm.IsDisposed) aboutForm = new AboutForm();
            aboutForm.Show();
            aboutForm.Activate();
        }

        // ---- messages from the hook thread -----------------------------------------------------------

        sealed class UiWindow : NativeWindow
        {
            internal UiWindow()
            {
                CreateParams cp = new CreateParams();
                cp.Parent = Native.HWND_MESSAGE;
                CreateHandle(cp);
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case UiMsg.Dim: dimmer.Adjust(m.WParam.ToInt32()); return;
                    case UiMsg.Place: dimmer.PlaceOnTop(); return;
                    case UiMsg.Display: dimmer.OnDisplayChanged(); return;
                    case UiMsg.Hide: dimmer.HideAll(); return;
                    case UiMsg.Presence: OnPresence(m.WParam.ToInt32() != 0); return;
                    case UiMsg.Note: string note; while (hook.Notes.TryDequeue(out note)) Log.Write(note); return;
                    case UiMsg.Settings: settingsSettle.Stop(); settingsSettle.Start(); return;
                    case UiMsg.Stop: Stop(StopReason(m.WParam.ToInt32()), false); return;
                    case UiMsg.Quit: Stop("asked to quit (" + App.Name + " --quit)", true); return;
                    case UiMsg.Button:
                        int w = m.WParam.ToInt32();
                        if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.OnButton((Btn)(w & 0xF), (w & 0x10) != 0, (w & 0x20) != 0);
                        return;
                }
                base.WndProc(ref m);
            }
        }

        static void OnPresence(bool present)
        {
            dimmer.SetPresent(present);
            Log.Write(present ? "chosen mouse attached: remapping on" : "chosen mouse gone: remapping off, full brightness");
        }

        static string StopReason(int code)
        {
            if (code == HookThread.StopStuck)
                return "sending replacement clicks stopped returning (usually another program's mouse hook has hung)";
            return "Windows kept dropping the mouse hook (" + RehookBudget.Limit + " times in 10 minutes). Another program may be capturing "
                + "the mouse, such as a tool that shares one mouse between computers (Mouse without Borders, Barrier, Input Leap)";
        }

        // ---- the ways out ----------------------------------------------------------------------------

        // Every way out ends here: let go of anything held, take the dimming and the tray icon down,
        // unhook, exit. Windows is left with a plain mouse at full brightness.
        static void Stop(string reason, bool byUser)
        {
            if (stopping) return;
            stopping = true;
            Log.Write("stopping, mouse back to normal: " + reason);
            try
            {
                if (hook != null) { hook.Failed = true; hook.Quit(500); } // releases held buttons, then unhooks
                for (int i = 0; i < 30 && !injector.Stuck() && !injector.Idle; i++) Thread.Sleep(10);
            }
            catch (Exception) { }
            try { if (dimmer != null) dimmer.HideAll(); } catch (Exception) { }
            try { if (tray != null) tray.Dispose(); } catch (Exception) { }
            // Never go quiet: a silent stop just looks like a broken mouse. The hook is gone, so the
            // mouse works normally while this box is up.
            if (!byUser)
                Box(App.Name + " has turned itself off, so your mouse is plain again.\n\nWhy: " + reason
                    + "\n\nTo turn it back on: Start menu > " + App.Name + " (or sign out and in).\nDetails: " + Log.FilePath, true);
            Environment.Exit(byUser ? 0 : 1);
        }

        // An error on a thread other than the UI thread: .NET ends the process when this returns. First
        // let the mouse go plain and let go of every button held down, so nothing stays stuck.
        static void OnCrash(object error)
        {
            if (Interlocked.Exchange(ref crashed, 1) != 0) return;
            try
            {
                List<Out> outs = new List<Out>();
                if (hook != null)
                {
                    hook.Failed = true; // from here on the hook lets everything through
                    // The hook thread owns the remapper's state, so it lets go itself, unless it is
                    // the thread that crashed or does not answer.
                    if (hook.ThreadId == Native.GetCurrentThreadId() || !hook.Quit(300))
                    {
                        remapper.KeysBlocked = false; // on the way out everything goes, whatever is in front
                        remapper.ReleaseAll(outs);
                    }
                }
                outs.AddRange(injector.TakeUnsent());
                if (outs.Count > 0) Injector.Send(outs); // sent from here: the injector may be the thread that died
            }
            catch (Exception) { }
            try { if (ui != null) Native.PostMessage(ui.Handle, UiMsg.Hide, IntPtr.Zero, IntPtr.Zero); } catch (Exception) { }
            Log.Write("crashed, mouse back to normal: " + error);
            Box(App.Name + " ran into an error and has turned itself off. Your mouse is plain again and no button is held down.\n\n"
                + "To turn it back on: Start menu > " + App.Name + ".\nDetails: " + Log.FilePath, true);
        }

        static void Box(string text, bool warning)
        {
            try
            {
                Native.MessageBox(IntPtr.Zero, text, App.Name,
                    (warning ? Native.MB_ICONWARNING : Native.MB_ICONINFORMATION) | Native.MB_SETFOREGROUND | Native.MB_TOPMOST);
            }
            catch (Exception) { }
        }

        // A message box that does not hold up the UI thread (dimming, tray) while it is open.
        static void BoxLater(string text)
        {
            Thread t = new Thread(delegate() { Box(text, true); });
            t.IsBackground = true;
            t.Start();
        }
    }

    // A windowless program has no console. Keep a redirected output if there is one, else borrow the
    // caller's console, else open one.
    internal static class Console2
    {
        internal static void Use()
        {
            IntPtr h = Native.GetStdHandle(-11);
            if (h == IntPtr.Zero || h == new IntPtr(-1) || Native.GetFileType(h) == 0)
            {
                if (!Native.AttachConsole(-1)) Native.AllocConsole();
            }
            StreamWriter w = new StreamWriter(Console.OpenStandardOutput());
            w.AutoFlush = true;
            Console.SetOut(w);
        }
    }

    // Other mouse software that would fight over the same clicks.
    internal static class Conflicts
    {
        static readonly string[,] Known =
        {
            { "EvoMouseExec", "Evoluent Mouse Manager" },
            { "EvoMouseListener", "Evoluent Mouse Manager" },
            { "EvoluentSystemTray", "Evoluent Mouse Manager" },
            { "XMouseButtonControl", "X-Mouse Button Control" },
        };

        // The name of the first conflicting program running in this session, or null.
        internal static string Running()
        {
            int session = Process.GetCurrentProcess().SessionId;
            for (int i = 0; i < Known.GetLength(0); i++)
            {
                foreach (Process p in Process.GetProcessesByName(Known[i, 0]))
                {
                    try { if (p.SessionId == session) return Known[i, 1]; }
                    catch (Exception) { }
                    finally { p.Dispose(); }
                }
            }
            return null;
        }
    }
}
