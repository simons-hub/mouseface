using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MouseFace.Tests
{
    // MouseFace.Tests.exe is built from the program's own sources plus this folder.
    //   --unit     the logic, with no Windows state touched (safe anywhere, run by CI)
    //   --system   needs a signed-in desktop: windows, the hook, Raw Input, UI Automation
    //   --live     tagged test clicks through Windows' real input pipeline (see LiveTest.cs)
    static class TestMain
    {
        internal static int Failures;

        [STAThread]
        static int Main(string[] args)
        {
            Console2.Use();
            App.DataDir = Path.Combine(Path.GetTempPath(), App.Name + "-tests-" + Process.GetCurrentProcess().Id);
            string mode = args.Length > 0 ? args[0] : "--unit";
            int code;
            try
            {
                if (mode == "--unit") code = Unit.Run();
                else if (mode == "--system") code = SystemChecks.Run();
                else if (mode == "--live") code = LiveTest.Run();
                else if (mode == "--select-at" && args.Length > 1) code = SelectAt(args[1]);
                else if (mode == "--ui-shots" && args.Length > 1) code = UiShots(args[1]);
                else if (mode == "--wait") { Thread.Sleep(30000); code = 0; } // a running program for the system checks to move
                else { Console.WriteLine("usage: " + App.Name + ".Tests --unit | --system | --live | --select-at X,Y"); return 2; }
            }
            finally
            {
                try { Directory.Delete(App.DataDir, true); } catch (Exception) { }
            }
            return code;
        }

        // Renders each window to a PNG in dir, off screen, so its layout can be looked at.
        static int UiShots(string dir)
        {
            // Scaled like the real program, so the shots show this screen's text size.
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (Exception) { }
            Application.EnableVisualStyles(); // as the program does, so the controls look as they will
            Directory.CreateDirectory(dir);
            Config c = SampleConfig();
            SettingsForm settings = new SettingsForm(c, delegate(string text) { return null; }, null);
            Shot(settings, Path.Combine(dir, "settings.png"), delegate { settings.OnButton(Btn.Right, true, true); });
            SettingsForm bigger = new SettingsForm(c, delegate(string text) { return null; }, null);
            Shot(bigger, Path.Combine(dir, "settings-150.png"), delegate { bigger.RescaleTo(bigger.Dpi * 3 / 2); });
            SettingsForm touchpad = new SettingsForm(c, delegate(string text) { return null; }, null);
            Shot(touchpad, Path.Combine(dir, "settings-touchpad.png"), delegate { touchpad.OnButton(Btn.Left, false, false); });
            Shot(new AboutForm(), Path.Combine(dir, "about.png"), null);
            Shot(new SetupForm(Assembly.GetExecutingAssembly().Location), Path.Combine(dir, "setup.png"), null);
            Shot(new MessageForm(App.Name, "Uninstall " + App.Name + "?", MessageForm.Kind.Question, "Uninstall", "Cancel", Installer.DeleteSettingsText),
                Path.Combine(dir, "uninstall.png"), null);
            Shot(new MessageForm(App.Name + " setup", Installer.Problem(new System.ComponentModel.Win32Exception(4551), true, "Nothing was installed."),
                MessageForm.Kind.Warning, "OK", null, null), Path.Combine(dir, "blocked.png"), null);
            using (Form host = new Form())
            {
                host.Show();
                Shot(new Toast("Windows key", "Shortcuts with the Windows key can't be recorded here. Type them in settings.ini.", host, 60000),
                    Path.Combine(dir, "toast.png"), null);
                host.Close();
            }
            return 0;
        }

        internal static Config SampleConfig()
        {
            string error;
            Config c = Config.Parse("[Buttons]\nMiddle = Right\nRight = Double\nBack = Keys Alt+Left\nForward = None\n", out error);
            if (c == null) throw new InvalidOperationException(error);
            return c;
        }

        static void Shot(Form f, string file, Action prepare)
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-20000, -20000);
            f.ShowInTaskbar = false;
            f.Show();
            if (prepare != null) prepare();
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("saved " + file + " (" + f.Width + "x" + f.Height + ")");
            f.Close();
        }

        internal static void Check(string name, bool ok)
        {
            Console.WriteLine("{0}  {1}", ok ? "pass" : "FAIL", name);
            if (!ok) Failures++;
        }

        internal static void Same(string name, string want, string got)
        {
            Check(name, want == got);
            if (want != got) Console.WriteLine("      wanted [{0}]\n      got    [{1}]", want, got);
        }

        internal static int Done()
        {
            Console.WriteLine(Failures == 0 ? "ALL PASS" : Failures + " FAILED");
            return Failures == 0 ? 0 : 1;
        }

        // Test aid: run the whole-name step once at a screen point (physical pixels), no hook.
        static int SelectAt(string at)
        {
            string[] xy = at.Split(',');
            int x, y;
            if (xy.Length != 2 || !int.TryParse(xy[0], out x) || !int.TryParse(xy[1], out y)) { Console.WriteLine("--select-at wants X,Y"); return 2; }
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (Exception) { }
            string result = null;
            Thread t = new Thread(delegate()
            {
                try { result = new WholeNameSelector().SelectAt(x, y, null); }
                catch (Exception ex) { result = "skipped, " + ex.GetType().Name + ": " + ex.Message; }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            if (!t.Join(10000)) { Console.WriteLine("no answer from the program in 10 s"); return 1; }
            Console.WriteLine(result);
            return 0;
        }
    }

    static class Unit
    {
        static void Check(string n, bool ok) { TestMain.Check(n, ok); }
        static void Same(string n, string want, string got) { TestMain.Same(n, want, got); }

        // Whether a shortcut leads to target, read back through the shell, letter for letter. Both are
        // compared in their long form: a temp folder can be named in its short one (C:\Users\RUNNER~1).
        static bool LeadsTo(string link, string target)
        {
            Native.IShellLinkW l = (Native.IShellLinkW)new Native.ShellLink();
            try
            {
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)l).Load(link, 0);
                StringBuilder path = new StringBuilder(32768);
                l.GetPath(path, path.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH: as it was saved
                return string.Equals(LongName(path.ToString()), LongName(target), StringComparison.OrdinalIgnoreCase);
            }
            finally { Marshal.FinalReleaseComObject(l); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetLongPathName(string path, StringBuilder longPath, uint length);
        static string LongName(string path)
        {
            StringBuilder name = new StringBuilder(32768);
            return GetLongPathName(path, name, (uint)name.Capacity) == 0 ? path : name.ToString();
        }

        // A link to a folder (a junction), as mklink /J makes it: whether this PC let the test make one.
        static bool Junction(string link, string target)
        {
            try
            {
                using (Process mk = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    mk.StandardOutput.ReadToEnd();
                    mk.StandardError.ReadToEnd();
                    mk.WaitForExit();
                }
                return (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception) { return false; }
        }

        // Windows' "came from the internet" mark is a hidden stream named Zone.Identifier on the file.
        static void Mark(string file)
        {
            IntPtr h = Native.CreateFile(file + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0x80, IntPtr.Zero); // write, create always
            if (h != new IntPtr(-1)) Native.CloseHandle(h);
        }

        static bool HasMark(string file)
        {
            IntPtr h = Native.CreateFile(file + ":Zone.Identifier", 0x80000000, 1, IntPtr.Zero, 3, 0x80, IntPtr.Zero); // read, open existing
            if (h == new IntPtr(-1)) return false;
            Native.CloseHandle(h);
            return true;
        }

        internal static Config Parse(string text)
        {
            string error;
            Config c = Config.Parse(text, out error);
            if (c == null) throw new Exception("test settings did not parse: " + error);
            return c;
        }

        // A VerticalMouse 4 Right set up as: top Left, middle finger Right, bottom finger Double,
        // wheel click and upper thumb off.
        internal const string SampleSettings = "[Buttons]\nMiddle = Right\nRight = Double\nForward = None\nBack = None\n";

        static string Decide(Remapper r, Btn b, bool down, bool fromChosen, bool paused, out bool swallow, out bool wholeName)
        {
            List<Out> outs = new List<Out>();
            swallow = r.Decide(b, down, fromChosen, paused, outs, out wholeName);
            return (swallow ? "swallow" : "pass") + (outs.Count > 0 ? " " + Out.Describe(outs) : "");
        }

        static string D(Remapper r, Btn b, bool down) { bool s, w; return Decide(r, b, down, true, false, out s, out w); }
        static string D(Remapper r, Btn b, bool down, bool fromChosen) { bool s, w; return Decide(r, b, down, fromChosen, false, out s, out w); }

        internal static int Run()
        {
            Remapping();
            ClickLockAndKeys();
            SettingsParsing();
            Matching();
            AttributionChecks();
            Watchdog();
            InjectorChecks();
            Dimming();
            WholeNames();
            return TestMain.Done();
        }

        static void Remapping()
        {
            Remapper r = new Remapper();
            r.Config = Parse(SampleSettings);
            bool s, w;
            Same("middle finger down sends Right down", "swallow Right+", D(r, Btn.Middle, true));
            Same("middle finger up sends Right up", "swallow Right-", Decide(r, Btn.Middle, false, true, false, out s, out w));
            Check("  ...and asks for no whole-name selection", !w);
            Same("bottom finger down sends a double click and holds its second press", "swallow Left+ Left- Left+", Decide(r, Btn.Right, true, true, false, out s, out w));
            Check("  ...and asks for no whole-name selection yet", !w);
            Same("bottom finger up lets go of the held press", "swallow Left-", Decide(r, Btn.Right, false, true, false, out s, out w));
            Check("  ...and asks for the whole-name selection", w);
            Same("top finger down passes through", "pass", D(r, Btn.Left, true));
            Same("top finger up passes through", "pass", D(r, Btn.Left, false));
            Same("wheel click (Forward) set to None: down swallowed", "swallow", D(r, Btn.Forward, true));
            Same("wheel click (Forward) set to None: up swallowed", "swallow", D(r, Btn.Forward, false));
            Same("upper thumb (Back) set to None: down swallowed", "swallow", D(r, Btn.Back, true));
            Same("upper thumb (Back) set to None: up swallowed", "swallow", D(r, Btn.Back, false));

            Same("another device's middle press passes through (a touchpad, a second mouse)", "pass", D(r, Btn.Middle, true, false));
            Same("  ...and so does its release", "pass", D(r, Btn.Middle, false, false));
            Same("another device's right press passes through", "pass", D(r, Btn.Right, true, false));
            Same("  ...and so does its release", "pass", D(r, Btn.Right, false, false));

            Same("paused: the chosen mouse's middle press passes through", "pass", Decide(r, Btn.Middle, true, true, true, out s, out w));
            Same("paused: its release passes through", "pass", Decide(r, Btn.Middle, false, true, true, out s, out w));

            Same("press from the chosen mouse", "swallow Right+", D(r, Btn.Middle, true));
            Same("its release still pairs with the press, whatever the device check says now", "swallow Right-", D(r, Btn.Middle, false, false));
            Same("a release with no press recorded passes through", "pass", D(r, Btn.Right, false));

            Same("press, then the program lets go of everything (pause, mouse gone)", "swallow Right+", D(r, Btn.Middle, true));
            List<Out> released = new List<Out>();
            r.ReleaseAll(released);
            Same("letting go sends the held Right up", "Right-", Out.Describe(released));
            Same("the finger's own release afterwards is swallowed, not sent twice", "swallow", D(r, Btn.Middle, false));
            Same("double click held, then let go of everything", "swallow Left+ Left- Left+", D(r, Btn.Right, true));
            released.Clear();
            r.ReleaseAll(released);
            Same("letting go sends the double click's held Left up", "Left-", Out.Describe(released));
            released.Clear();
            r.ReleaseAll(released);
            Same("letting go with nothing held sends nothing", "", Out.Describe(released));
            D(r, Btn.Right, false);

            Same("a held remapped press", "swallow Right+", D(r, Btn.Middle, true));
            Same("another device presses the same button: the held one is let go first, then its press passes", "pass Right-", D(r, Btn.Middle, true, false));
            Same("  ...that device's release passes", "pass", D(r, Btn.Middle, false, false));
            Same("  ...and the chosen mouse's late release sends nothing more", "pass", D(r, Btn.Middle, false));
            Same("a missed release: the next press lets the old one go first", "swallow Left+ Left- Left+", D(r, Btn.Right, true));
            Same("  ...(the release never came) and the next press", "swallow Left- Left+ Left- Left+", D(r, Btn.Right, true));
            D(r, Btn.Right, false);
            Same("an error mid-press: the press passed through unchanged", "swallow Right+", D(r, Btn.Middle, true));
            r.MarkPassed(Btn.Middle);
            Same("  ...so its release passes through too", "pass", D(r, Btn.Middle, false));

            Remapper off = new Remapper();
            off.Config = Parse(SampleSettings + "[Double]\nWholeName = no\n");
            D(off, Btn.Right, true);
            Decide(off, Btn.Right, false, true, false, out s, out w);
            Check("WholeName = no: a Double click asks for no whole-name selection", !w);

            Remapper plain = new Remapper();
            plain.Config = new Config();
            Same("default settings: every button passes through", "pass pass pass pass pass",
                D(plain, Btn.Left, true) + " " + D(plain, Btn.Right, true) + " " + D(plain, Btn.Middle, true) + " " + D(plain, Btn.Back, true) + " " + D(plain, Btn.Forward, true));

            // The checker itself must be able to fail, or every pass above means nothing.
            Check("control: a wrong expectation is caught", D(r, Btn.Middle, true) != "swallow Left+");
            D(r, Btn.Middle, false);
        }

        static void ClickLockAndKeys()
        {
            Remapper r = new Remapper();
            r.Config = Parse("[Buttons]\nBack = ClickLock\nForward = Keys Ctrl+Shift+T\nMiddle = Keys Win\n");
            Same("ClickLock press holds the left button down", "swallow Left+", D(r, Btn.Back, true));
            Same("  ...its release does nothing more", "swallow", D(r, Btn.Back, false));
            Check("  ...and the lock is on", r.ClickLocked);
            Same("ClickLock pressed again lets the left button go", "swallow Left-", D(r, Btn.Back, true));
            D(r, Btn.Back, false);
            Check("  ...and the lock is off", !r.ClickLocked);
            D(r, Btn.Back, true); D(r, Btn.Back, false);
            string realDown = D(r, Btn.Left, true), realUp = D(r, Btn.Left, false);
            Same("while locked, a real left click passes through", "pass pass", realDown + " " + realUp);
            Check("  ...and its release ends the lock (Windows saw the button go up)", !r.ClickLocked);
            D(r, Btn.Back, true); D(r, Btn.Back, false);
            List<Out> released = new List<Out>();
            r.ReleaseAll(released);
            Same("letting go of everything ends a click lock", "Left-", Out.Describe(released));

            Same("Keys Ctrl+Shift+T: a shortcut is pressed and let go at once, in order", "swallow Ctrl+ Shift+ T+ T- Shift- Ctrl-", D(r, Btn.Forward, true));
            Same("  ...so nothing is left to let go when the finger lifts", "swallow", D(r, Btn.Forward, false));
            Same("Keys Win: a modifier alone is held while the button is", "swallow Win+", D(r, Btn.Middle, true));
            released.Clear();
            r.ReleaseAll(released);
            Same("letting go of everything lets held keys go", "Win-", Out.Describe(released));
            D(r, Btn.Middle, false);
            Check("held: Ctrl, Shift, Alt and Win on their own, and a single key (push-to-talk)", Parse("[Buttons]\nBack = Keys Ctrl+Shift\n").For(Btn.Back).Hold
                && Parse("[Buttons]\nBack = Keys F13\n").For(Btn.Back).Hold && Parse("[Buttons]\nBack = Keys Space\n").For(Btn.Back).Hold);
            Check("pressed and let go at once: a modifier with another key", !Parse("[Buttons]\nBack = Keys Alt+F4\n").For(Btn.Back).Hold
                && !Parse("[Buttons]\nBack = Keys Win+L\n").For(Btn.Back).Hold);

            // Something else lets the locked left button go: the next Click lock press locks again.
            D(r, Btn.Back, true); D(r, Btn.Back, false);
            r.LockLost();
            Same("a lock something else ended: the next press locks again", "swallow Left+", D(r, Btn.Back, true));
            D(r, Btn.Back, false);
            D(r, Btn.Back, true); D(r, Btn.Back, false);
            UnheardReleases();
        }

        // Windows keeps a press or release from the hook while an admin window has it, but Raw Input still
        // reports it. A release is only owed once a later report has come and the hook still hasn't heard
        // it: one report can hold several changes, which the hook hears one by one after it.
        static void UnheardReleases()
        {
            Remapper r = new Remapper();
            r.Config = Parse("[Buttons]\nMiddle = Right\nRight = Double\nBack = Keys Ctrl\nForward = Keys Alt+F4\n");
            List<Out> sent = new List<Out>();
            D(r, Btn.Middle, true);
            r.RawUp(Btn.Middle); // the finger lifted over an admin window; the hook heard nothing
            r.Settle(sent, false);
            Same("a release in the latest report is not owed yet (the hook may still hear it)", "", Out.Describe(sent));
            r.NextReport();      // the mouse moved on
            r.Settle(sent, false);
            Same("a release the hook never heard: the held Right is let go at the next event heard", "Right-", Out.Describe(sent));
            Same("  ...and the button starts afresh", "swallow Right+", D(r, Btn.Middle, true));
            r.RawUp(Btn.Middle);
            Same("a release Raw Input reports just before the hook hears it is let go once, by the hook", "swallow Right-", D(r, Btn.Middle, false));
            r.NextReport();
            sent.Clear(); r.Settle(sent, false);
            Same("  ...with nothing left over", "", Out.Describe(sent));

            // One report: the bottom finger lets go as the middle one presses (rolling between buttons).
            D(r, Btn.Right, true);
            r.RawUp(Btn.Right);
            string other = D(r, Btn.Middle, true);  // the hook hears the press first...
            sent.Clear(); r.Settle(sent, false);
            Check("two changes in one report: the other button's press doesn't let go of this one early", other == "swallow Right+" && sent.Count == 0);
            Same("  ...its own release, heard next, lets go of the double click's Left and is swallowed", "swallow Left-", D(r, Btn.Right, false));
            D(r, Btn.Middle, false);

            D(r, Btn.Right, true);
            r.RawUp(Btn.Right);   // released unheard...
            r.NextReport();
            r.RawDown(Btn.Right); // ...and pressed again unheard
            Same("a press after an unheard release: the old double click's Left goes first, and the new release passes",
                "pass Left-", D(r, Btn.Right, false));

            D(r, Btn.Back, true);
            r.Defer(Btn.Back); // its release came while an admin window was in front
            sent.Clear(); r.Settle(sent, true);
            Same("held Ctrl waits while an admin window is in front, where Windows would drop its release", "", Out.Describe(sent));
            r.Settle(sent, false);
            Same("  ...and goes once it has gone", "Ctrl-", Out.Describe(sent));

            D(r, Btn.Back, true);
            r.RawUp(Btn.Back); r.NextReport(); // let go over an admin window...
            r.RawDown(Btn.Back);                // ...and pressed again over it
            sent.Clear(); r.Settle(sent, true);
            Same("held Ctrl from a press that ended unheard still waits while the admin window is in front", "", Out.Describe(sent));
            r.KeysBlocked = true;
            Same("  ...the second press's release, heard, passes (its press went to the admin window)", "pass", D(r, Btn.Back, false));
            r.Settle(sent, false);
            Same("  ...and Ctrl goes once the admin window has", "Ctrl-", Out.Describe(sent));

            // Keys go to the window in front, so they needn't wait for the hook to hear the mouse again.
            r.KeysBlocked = false;
            D(r, Btn.Back, true);
            r.RawUp(Btn.Back); // let go over an admin window, with an ordinary window in front
            Check("a held key's release seen only by Raw Input keeps the window watch on", r.OwesKeys);
            sent.Clear(); r.SettleKeys(sent, false, false);
            Same("  ...but in the latest report it waits (the hook may yet hear it)", "", Out.Describe(sent));
            r.NextReport();
            r.SettleKeys(sent, false, false);
            Same("  ...and once owed, Ctrl goes at once, from Raw Input alone", "Ctrl-", Out.Describe(sent));
            D(r, Btn.Back, true);
            r.RawUp(Btn.Back);
            sent.Clear(); r.SettleKeys(sent, false, true); // another window came to the front
            Same("a window coming to the front lets a held key go early", "Ctrl-", Out.Describe(sent));
            Same("  ...and its release, if the hook still hears it, is swallowed", "swallow", D(r, Btn.Back, false));

            D(r, Btn.Back, true);
            r.KeysBlocked = true; // an admin window is in front
            Same("another device presses the same button: held Ctrl is owed, not sent where it would be dropped", "pass", D(r, Btn.Back, true, false));
            sent.Clear(); r.SettleKeys(sent, false, false);
            Same("  ...and goes once nothing blocks it", "Ctrl-", Out.Describe(sent));
            D(r, Btn.Back, false, false);
            D(r, Btn.Back, true);
            r.KeysBlocked = true;
            sent.Clear(); r.ReleaseAll(sent); // paused, or the mouse unplugged, with an admin window in front
            Same("letting go of everything with an admin window in front owes held Ctrl", "", Out.Describe(sent));
            r.SettleKeys(sent, false, false);
            Same("  ...until it has gone", "Ctrl-", Out.Describe(sent));
            D(r, Btn.Back, false);
            D(r, Btn.Back, true);
            r.KeysBlocked = false; // on the way out
            sent.Clear(); r.ReleaseAll(sent);
            Same("on the way out held keys go whatever is in front", "Ctrl-", Out.Describe(sent));
            D(r, Btn.Back, false);

            D(r, Btn.Forward, true);
            r.RawUp(Btn.Forward); r.NextReport(); // a shortcut already let go; its finger lifted unheard
            sent.Clear(); r.Settle(sent, false);
            Check("an unheard release after a shortcut sends nothing more", sent.Count == 0);
            Same("  ...and the next unheard press's release passes through (it was never swallowed)", "pass", D(r, Btn.Forward, false));

            D(r, Btn.Middle, true);
            r.RawUp(Btn.Middle); r.NextReport();
            sent.Clear(); r.ReleaseAll(sent);
            Same("letting go of everything after an unheard release", "Right-", Out.Describe(sent));
            Same("  ...leaves no swallowed release behind", "pass", D(r, Btn.Middle, false));

            Act a; string err;
            Check("parse: Keys Ctrl+C", Act.TryParse("Keys ctrl+c", out a, out err) && a.Text == "Keys Ctrl+C");
            Check("parse: Keys Alt+F4", Act.TryParse("Keys Alt+F4", out a, out err) && a.Text == "Keys Alt+F4");
            Check("parse: Keys PageDown", Act.TryParse("Keys PgDn", out a, out err) && a.Text == "Keys PageDown");
            Check("parse: Keys BrowserBack", Act.TryParse("Keys BrowserBack", out a, out err) && a.Kind == ActKind.Keys);
            Check("parse: Keys Left is the arrow key, Left is the button",
                Act.TryParse("Keys Left", out a, out err) && a.Kind == ActKind.Keys && Act.TryParse("left", out a, out err) && a.IsPlain(Btn.Left));
            Check("parse rejects: Keys with nothing after it", !Act.TryParse("Keys", out a, out err));
            Check("parse rejects: an unknown key name", !Act.TryParse("Keys Ctrl+Foo", out a, out err) && err.Contains("Foo"));
            Check("parse rejects: a modifier after the key", !Act.TryParse("Keys C+Ctrl", out a, out err));
            Check("parse rejects: two plain keys", !Act.TryParse("Keys A+B", out a, out err));
            Check("parse rejects: a modifier twice", !Act.TryParse("Keys Ctrl+Ctrl+C", out a, out err));
            Check("parse rejects: an unknown action", !Act.TryParse("Tripple", out a, out err) && err.Contains("Tripple"));
            Check("extended keys: arrows, Home, Delete, Win", KeyNames.IsExtended(0x26) && KeyNames.IsExtended(0x24) && KeyNames.IsExtended(0x2E) && KeyNames.IsExtended(0x5B));
            Check("not extended: letters, Ctrl, Enter", !KeyNames.IsExtended(0x43) && !KeyNames.IsExtended(0x11) && !KeyNames.IsExtended(0x0D));
        }

        static void SettingsParsing()
        {
            string error;
            Config d = Config.Parse(Config.DefaultText, out error);
            Check("the default settings file parses", d != null);
            if (d != null)
            {
                Check("  ...remaps nothing", !d.RemapsAnything);
                Check("  ...chooses any Evoluent mouse, dimming off, whole names on, pop-up on",
                    d.Match.Text == "Evoluent" && d.Dim == DimKey.Off && d.WholeName && d.Popup && d.DimStep == 3);
            }
            Config c = Parse("[Mouse]\nMatch = 1a7c:0191\n" + SampleSettings + "[Dimming]\nKey = shift + alt ; a note\nStep = 3\nPopup = no\n");
            Same("a full settings file reads as written",
                "mouse 1A7C:0191; buttons Left=Left, Right=Double, Middle=Right, Back=None, Forward=None; whole names on; dimming Shift+Alt, step 3% (no pop-up)",
                c.Summary());
            Check("names are not case-sensitive", Parse("[BUTTONS]\nmiddle = RIGHT\n").For(Btn.Middle).IsPlain(Btn.Right));

            string[,] bad =
            {
                { "[Buttons]\nMiddle = Rihgt\n", "line 2:", "Rihgt" },
                { "[Buttons]\nThumb = Left\n", "line 2:", "Thumb" },
                { "\n\n[Mouse]\nMatch = Logitech\n", "line 4:", "Logitech" },
                { "[Colours]\n", "line 1:", "Colours" },
                { "Middle = Right\n", "line 1:", "section" },
                { "[Buttons]\nMiddle Right\n", "line 2:", "Name = value" },
                { "[Buttons]\nMiddle = Right\nmiddle = Left\n", "line 3:", "twice" },
                { "[Dimming]\nKey = Ctrl\n", "line 2:", "Ctrl" },
                { "[Double]\nWholeName = maybe\n", "line 2:", "yes or no" },
                { "[Dimming]\nStep = 25\n", "line 2:", "1 to 20" },
                { "[Buttons\n", "line 1:", "]" },
            };
            for (int i = 0; i < bad.GetLength(0); i++)
            {
                Config none = Config.Parse(bad[i, 0], out error);
                Check("a mistake names its line and the problem: " + bad[i, 2], none == null && error != null && error.StartsWith(bad[i, 1]) && error.Contains(bad[i, 2]));
                if (none != null || error == null || !error.StartsWith(bad[i, 1])) Console.WriteLine("      got: " + error);
            }

            string text = "; notes stay\r\n[Mouse]\r\n; which mouse\r\nMatch = Evoluent\r\n\r\n[Buttons]\r\nMiddle = Right\r\n";
            string changed = SettingsFile.WithMatch(text, "1A7C:0191");
            Same("choosing a mouse rewrites only the Match line", text.Replace("Match = Evoluent", "Match = 1A7C:0191"), changed);
            Same("choosing a mouse finds a [ Mouse ] header with a note on it, as the parser does",
                "[ Mouse ] ; mine\r\nMatch = Any\r\n[Buttons]", SettingsFile.WithMatch("[ Mouse ] ; mine\r\nMatch = Evoluent\r\n[Buttons]", "Any"));
            Same("choosing a mouse adds Match to a [Mouse] section without one",
                "[Mouse]\r\nMatch = Any\r\n[Buttons]", SettingsFile.WithMatch("[Mouse]\r\n[Buttons]", "Any"));
            Same("choosing a mouse adds a [Mouse] section at the end when there is none",
                "[Buttons]\r\n\r\n[Mouse]\r\nMatch = Any\r\n", SettingsFile.WithMatch("[Buttons]", "Any"));
            Same("the Settings window keeps a line's note in its column",
                "[Buttons]\r\nLeft    = Double          ; top finger",
                SettingsFile.WithValue("[Buttons]\r\nLeft    = Left            ; top finger", "Buttons", "Left", "Double"));
            Same("a missing setting goes after its section's last one, before the blank line",
                "[Dimming]\r\nKey = Off\r\nStep = 5\r\n\r\n[Double]",
                SettingsFile.WithValue("[Dimming]\r\nKey = Off\r\n\r\n[Double]", "Dimming", "Step", "5"));
            Same("a key that only starts like another is left alone",
                "[Buttons]\r\nLeftover = x\r\nLeft = None",
                SettingsFile.WithValue("[Buttons]\r\nLeftover = x", "Buttons", "Left", "None"));
            Same("a file with LF line endings keeps them", "[Buttons]\nMiddle = Double\nBack = None\n",
                SettingsFile.WithValue("[Buttons]\nMiddle = Right\nBack = None\n", "Buttons", "Middle", "Double"));
            Same("a key in a second block of its section is changed where it is, as the parser reads it",
                "[Buttons]\r\nLeft = Left\r\n[Dimming]\r\nKey = Off\r\n[Buttons]\r\nMiddle = Double",
                SettingsFile.WithValue("[Buttons]\r\nLeft = Left\r\n[Dimming]\r\nKey = Off\r\n[Buttons]\r\nMiddle = Right", "Buttons", "Middle", "Double"));
            Same("a missing setting goes after the section's last setting, not after a note for the next section",
                "[Dimming]\r\nKey = Off\r\nStep = 5\r\n; double clicks\r\n[Double]",
                SettingsFile.WithValue("[Dimming]\r\nKey = Off\r\n; double clicks\r\n[Double]", "Dimming", "Step", "5"));
            MatchRule listed;
            Check("Any in a list says it goes alone", !MatchRule.TryParse("Any, Evoluent", out listed, out error) && error.Contains("alone"));

            // An editor that saves in the ANSI code page: a save from the window keeps its accented notes
            // byte for byte, and leaves no temporary file behind.
            string note = "; café – mine\r\n[Buttons]\r\nMiddle = ";
            File.WriteAllBytes(SettingsFile.FilePath, Encoding.Default.GetBytes(note + "Right\r\n"));
            SettingsFile.Save(SettingsFile.WithValue(SettingsFile.Read(), "Buttons", "Middle", "Double"));
            Check("an ANSI settings.ini keeps its accented notes through a save",
                Convert.ToBase64String(File.ReadAllBytes(SettingsFile.FilePath)) == Convert.ToBase64String(Encoding.Default.GetBytes(note + "Double\r\n")));
            Check("  ...and no settings.ini.tmp is left", !File.Exists(SettingsFile.FilePath + ".tmp"));
            byte[] bom = new UTF8Encoding(true).GetPreamble(), body = Encoding.ASCII.GetBytes("[Buttons]\r\nMiddle = Right\r\n");
            byte[] marked = new byte[bom.Length + body.Length];
            bom.CopyTo(marked, 0);
            body.CopyTo(marked, bom.Length);
            File.WriteAllBytes(SettingsFile.FilePath, marked);
            SettingsFile.Save(SettingsFile.WithValue(SettingsFile.Read(), "Buttons", "Middle", "Double"));
            byte[] saved = File.ReadAllBytes(SettingsFile.FilePath);
            Check("a UTF-8 file with its mark keeps the mark, once", saved.Length > 6 && saved[0] == bom[0] && saved[1] == bom[1] && saved[2] == bom[2] && saved[3] == (byte)'[');
            File.Delete(SettingsFile.FilePath);

            // Setup's file steps, in a folder of the test's own.
            string home = Path.Combine(Path.GetTempPath(), App.Name + "-setup-test-" + Process.GetCurrentProcess().Id);
            string dir = Path.Combine(home, "app"), startup = Path.Combine(home, "Startup", App.Name + ".lnk"), menu = Path.Combine(home, "Programs", App.Name + ".lnk");
            try
            {
                Installer.Place(Assembly.GetExecutingAssembly().Location, dir, startup, menu);
                Check("setup copies the program into its folder", File.Exists(Path.Combine(dir, App.Name + ".exe")));
                Check("  ...through a new file renamed over the old, which is not left behind", !File.Exists(Path.Combine(dir, App.Name + ".exe.new")));
                Check("setup makes the sign-in and Start menu shortcuts", File.Exists(startup) && File.Exists(menu));
                Check("  ...each leading to the program", LeadsTo(startup, Path.Combine(dir, App.Name + ".exe")) && LeadsTo(menu, Path.Combine(dir, App.Name + ".exe")));
                // A user whose name has letters outside the PC's language (a Polish name on a PC set to
                // English, say), whose shortcuts Windows' older shortcut maker couldn't save.
                string far = Path.Combine(home, "Łukasz Ω 日本 Иван");
                string farExe = Path.Combine(far, "app", App.Name + ".exe"), farLink = Path.Combine(far, "Startup", App.Name + ".lnk");
                Installer.Place(Assembly.GetExecutingAssembly().Location, Path.Combine(far, "app"), farLink);
                Check("  ...for a user whose name has letters outside the PC's language too, leading to the program letter for letter",
                    File.Exists(farLink) && LeadsTo(farLink, farExe));
                File.SetAttributes(farLink, FileAttributes.ReadOnly);
                string why = null;
                try { Installer.Shortcut(farLink, farExe, far); }
                catch (IOException ex) { why = ex.Message; }
                Check("a shortcut that can't be saved says which, in Windows' words rather than COM's",
                    why != null && why.StartsWith("Windows couldn't save the shortcut " + farLink + ". ") && !why.Contains("HRESULT"));
                File.SetAttributes(farLink, FileAttributes.Normal);
                File.WriteAllText(Path.Combine(dir, "settings.ini"), "[Mouse]");
                File.SetAttributes(menu, FileAttributes.ReadOnly);
                string left = Installer.Remove(Assembly.GetExecutingAssembly().Location, dir, startup, menu, null);
                Check("uninstall removes the program and both shortcuts, a read-only one too", left == null
                    && !File.Exists(Path.Combine(dir, App.Name + ".exe")) && !File.Exists(startup) && !File.Exists(menu));
                Check("uninstall keeps settings.ini", File.Exists(Path.Combine(dir, "settings.ini")));

                // Installed apps runs the installed copy itself to uninstall it, and a running program's
                // file can't be deleted, so it is moved out of the way (the system checks try it running).
                string own = Path.Combine(dir, App.Name + ".exe");
                File.WriteAllText(own, "a stand-in for the installed program");
                File.WriteAllText(startup, "a stand-in shortcut");
                Installer.Remove(own, dir, startup);
                string aside = Path.Combine(Path.GetTempPath(), App.Name + "-" + Process.GetCurrentProcess().Id + Installer.Aside);
                Check("uninstall run from the installed copy moves its file to the temp folder", !File.Exists(own) && File.Exists(aside) && !File.Exists(startup));
                File.Delete(aside);
                File.WriteAllText(own + Installer.Aside, "renamed where it was, when the temp folder is on another drive");
                Installer.Place(Assembly.GetExecutingAssembly().Location, dir);
                Check("the next install deletes a program an uninstall renamed where it was", !File.Exists(own + Installer.Aside));

                // A download carries Windows' "came from the internet" mark; the installed copy must not.
                string download = Path.Combine(home, "MouseFace-Setup.exe"), installed = Path.Combine(dir, App.Name + ".exe");
                // A stand-in, not a program: a real program marked as downloaded makes Windows Defender
                // upload it to Microsoft for a look, with a notification, every time the tests run.
                File.WriteAllText(download, "a stand-in for the downloaded setup, not a program");
                Mark(download);
                Check("(the test can mark a file as downloaded)", HasMark(download));
                Installer.Place(download, dir);
                Check("setup installs a downloaded copy without its downloaded mark", File.Exists(installed) && !HasMark(installed));
                Mark(installed);
                Installer.Place(download, dir);
                Check("updating drops a mark the old installed copy had", File.Exists(installed) && !HasMark(installed));

                // An install that fails (Windows wouldn't start the new copy) puts back what was there.
                string fresh = Path.Combine(home, "fresh"), freshExe = Path.Combine(fresh, App.Name + ".exe");
                string[] links = { startup, menu };
                Installer.Place(Assembly.GetExecutingAssembly().Location, dir, menu); // a shortcut that was there before, to another copy
                byte[] menuBefore = File.ReadAllBytes(menu);
                Installer.Place(Assembly.GetExecutingAssembly().Location, fresh, startup, menu);
                Check("(setup rewrites a shortcut that was there)", Convert.ToBase64String(File.ReadAllBytes(menu)) != Convert.ToBase64String(menuBefore));
                string undone = Installer.PutBack(fresh, true, false, links, new byte[][] { null, menuBefore });
                Check("a failed first install removes the program and the shortcut it made", undone == null && !File.Exists(freshExe) && !File.Exists(startup));
                Check("  ...and gives a shortcut that was there before its old contents back",
                    File.Exists(menu) && Convert.ToBase64String(File.ReadAllBytes(menu)) == Convert.ToBase64String(menuBefore));
                Check("  ...and removes its folder, which it made and is now empty", !Directory.Exists(fresh));
                Installer.Place(Assembly.GetExecutingAssembly().Location, fresh);
                File.WriteAllText(Path.Combine(fresh, "settings.ini"), "[Mouse]");
                Installer.PutBack(fresh, true, false, new string[0], new byte[0][]);
                Check("  ...but keeps the folder, and settings.ini, when that is in it", !File.Exists(freshExe) && File.Exists(Path.Combine(fresh, "settings.ini")));
                File.Delete(Path.Combine(fresh, "settings.ini"));
                Installer.Place(Assembly.GetExecutingAssembly().Location, fresh);
                Installer.PutBack(fresh, true, true, new string[0], new byte[0][]);
                Check("  ...and a folder that was there before, even empty", !File.Exists(freshExe) && Directory.Exists(fresh));
                Installer.Place(Assembly.GetExecutingAssembly().Location, fresh, startup);
                Installer.PutBack(fresh, false, true, new string[] { startup }, new byte[][] { null });
                Check("a failed update keeps the program (the copy put back) and removes a shortcut it made",
                    File.Exists(freshExe) && !File.Exists(startup));
                using (FileStream held = new FileStream(startup, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    string stuck = Installer.PutBack(fresh, true, true, new string[] { startup }, new byte[][] { null });
                    Check("  ...and returns what it could not delete", stuck == startup);
                }
                File.Delete(startup);
                // The file a new copy is written to first, left when something had it open as Place tidied up.
                string freshNew = freshExe + Installer.Fresh;
                File.WriteAllText(freshNew, "a new copy antivirus had open");
                Check("  ...and deletes the file the new copy was written to first, when Place couldn't",
                    Installer.PutBack(fresh, false, true, new string[0], new byte[0][]) == null && !File.Exists(freshNew));
                File.WriteAllText(freshNew, "a new copy antivirus has open");
                using (new FileStream(freshNew, FileMode.Open, FileAccess.Read, FileShare.None))
                    Check("  ...or names it, so a first install doesn't say nothing was installed",
                        Installer.PutBack(fresh, true, true, new string[0], new byte[0][]) == freshNew);
                File.Delete(freshNew);

                // End to end: the whole install, given a "program" Windows won't start (as when Smart App
                // Control blocks it, the start throws), in folders of the test's own. Nothing here touches
                // the real install, its shortcuts, its Installed apps entry or a running MouseFace.
                string stand = Path.Combine(home, "not-a-program.exe");
                File.WriteAllText(stand, "a stand-in setup whose installed copy Windows can't start, as when a rule blocks it");
                string e2e = Path.Combine(home, "e2e"), e2eExe = Path.Combine(e2e, App.Name + ".exe"), e2eIni = Path.Combine(e2e, "settings.ini");
                string s2 = Path.Combine(home, "Startup2", App.Name + ".lnk"), m2 = Path.Combine(home, "Programs2", App.Name + ".lnk");
                bool e2eListed = false;
                Func<string> register = delegate { e2eListed = true; return null; };
                string e2eNote;
                string said = Installer.Install(stand, e2e, s2, m2, false, register, out e2eNote);
                Check("end to end: a first install Windows won't start says why, and that nothing was installed",
                    said != null && said.StartsWith("Could not install: ") && said.EndsWith(". Nothing was installed."));
                Check("  ...and leaves no program, no shortcut, no folder and no Installed apps entry",
                    !File.Exists(e2eExe) && !File.Exists(s2) && !File.Exists(m2) && !Directory.Exists(e2e) && !e2eListed);
                Directory.CreateDirectory(e2e);
                File.WriteAllText(e2eIni, "[Mouse]");
                Installer.Place(Assembly.GetExecutingAssembly().Location, dir, m2); // a Start menu shortcut there before
                byte[] m2Before = File.ReadAllBytes(m2);
                said = Installer.Install(stand, e2e, s2, m2, false, register, out e2eNote);
                Check("  ...after an uninstall that kept settings.ini: keeps it and its folder, removes the program and the new shortcut",
                    File.Exists(e2eIni) && !File.Exists(e2eExe) && !File.Exists(s2) && said.EndsWith(". Nothing was installed."));
                Check("  ...and gives a shortcut that was there before its old contents back",
                    Convert.ToBase64String(File.ReadAllBytes(m2)) == Convert.ToBase64String(m2Before));
                File.WriteAllText(e2eExe, "the copy you had");
                File.WriteAllText(e2eExe + Installer.Kept, "a copy an earlier failed update could not put back");
                said = Installer.Install(stand, e2e, s2, m2, false, register, out e2eNote);
                Check("end to end: an update Windows won't start puts back the copy that was there, and says so",
                    File.ReadAllText(e2eExe) == "the copy you had" && said.EndsWith(". The copy that was there is back in place."));
                Check("  ...never writing over a copy an earlier failed update set aside, nor leaving one of its own",
                    File.Exists(e2eExe + Installer.Kept) && File.ReadAllText(e2eExe + Installer.Kept) == "a copy an earlier failed update could not put back" && !File.Exists(e2eExe + Installer.Kept + "1"));
                Check("  ...removes the shortcut it made, keeps settings.ini, and is not listed",
                    !File.Exists(s2) && File.Exists(m2) && File.Exists(e2eIni) && !e2eListed);
                File.WriteAllText(e2eExe + Installer.Kept + "1", "another");
                File.WriteAllText(e2eExe + Installer.Kept + "2", "a read-only one");
                File.SetAttributes(e2eExe + Installer.Kept + "2", FileAttributes.ReadOnly);
                string[] yours = { e2eExe + Installer.Kept + "-backup", e2eExe + ".older", e2eExe + ".bak" };
                foreach (string file in yours) File.WriteAllText(file, "a file of yours, named like a copy set aside");
                Installer.DeleteKept(e2eExe);
                Check("once a new copy runs, every copy set aside goes, a read-only one too", !File.Exists(e2eExe + Installer.Kept)
                    && !File.Exists(e2eExe + Installer.Kept + "1") && !File.Exists(e2eExe + Installer.Kept + "2") && File.Exists(e2eExe));
                Check("  ...but never a file of yours whose name only starts the same", File.Exists(yours[0]) && File.Exists(yours[1]) && File.Exists(yours[2]));
                foreach (string file in yours) File.Delete(file);
                Check("a copy is set aside under a name no file has", Installer.FreeName(e2eExe + Installer.Kept) == e2eExe + Installer.Kept
                    && Installer.FreeName(e2eExe) == e2eExe + "1");
                // An update that can't even set the copy that was there aside (another program has it open)
                // changes nothing, and says so.
                using (new FileStream(e2eExe, FileMode.Open, FileAccess.Read, FileShare.Read))
                    said = Installer.Install(stand, e2e, s2, m2, false, register, out e2eNote);
                Check("end to end: an update that can't set the copy that was there aside leaves it, and says it is still in place",
                    File.ReadAllText(e2eExe) == "the copy you had" && said.EndsWith(". The copy that was there is still in place.")
                    && !File.Exists(s2) && !File.Exists(e2eExe + Installer.Kept) && !e2eListed);

                // A shortcut there that can't be read is left alone by a failed install, never taken for one it made.
                string lockedLink = Path.Combine(home, "locked.lnk");
                File.WriteAllText(lockedLink, "a shortcut in use");
                using (new FileStream(lockedLink, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check("a shortcut that can't be read is told apart from a missing one", ReferenceEquals(Installer.ReadIfThere(lockedLink), Installer.Unreadable)
                        && Installer.ReadIfThere(Path.Combine(home, "none.lnk")) == null);
                Installer.PutBack(e2e, false, true, new string[] { lockedLink }, new byte[][] { Installer.Unreadable });
                Check("  ...and a failed install leaves it as it was", File.ReadAllText(lockedLink) == "a shortcut in use");

                // Uninstall with "Also delete my settings" ticked: MouseFace's own files go, and then the folder.
                string data = Path.Combine(home, "data");
                Directory.CreateDirectory(data);
                foreach (string name in new string[] { "settings.ini", "settings.ini.tmp", "brightness.txt", App.Name + ".log", App.Name + ".log.old",
                    App.Name + ".exe.old", App.Name + ".exe.old3", App.Name + ".exe" + Installer.Fresh })
                    File.WriteAllText(Path.Combine(data, name), "MouseFace's own");
                File.SetAttributes(Path.Combine(data, "settings.ini"), FileAttributes.ReadOnly);
                string cwd = Environment.CurrentDirectory;
                Environment.CurrentDirectory = data; // as when Installed apps starts the uninstall in it
                string notGone = Installer.DeleteData(data);
                Check("deleting the settings deletes MouseFace's files (a read-only settings.ini too) and the folder it is working in",
                    notGone == null && !Directory.Exists(data));
                Environment.CurrentDirectory = cwd;
                Directory.CreateDirectory(Path.Combine(data, "mine"));
                File.WriteAllText(Path.Combine(data, "settings.ini"), "[Mouse]");
                File.WriteAllText(Path.Combine(data, "settings-backup.ini"), "a copy of yours");
                string[] alike = { App.Name + ".exe.bak", App.Name + ".exe - Shortcut.lnk", App.Name + ".exe.old-backup" };
                foreach (string name in alike) File.WriteAllText(Path.Combine(data, name), "a file of yours, named like the program");
                notGone = Installer.DeleteData(data);
                Check("  ...but nothing else of yours kept there, not even a file named like the program, which keeps the folder",
                    notGone == null && !File.Exists(Path.Combine(data, "settings.ini")) && File.Exists(Path.Combine(data, "settings-backup.ini"))
                    && Directory.Exists(Path.Combine(data, "mine")) && File.Exists(Path.Combine(data, alike[0]))
                    && File.Exists(Path.Combine(data, alike[1])) && File.Exists(Path.Combine(data, alike[2])));
                string renamed = Path.Combine(data, App.Name + ".exe" + Installer.Aside), open = Path.Combine(data, "settings.ini");
                File.WriteAllText(renamed, "a program an uninstall renamed where it was, still running");
                File.WriteAllText(open, "[Mouse]");
                using (new FileStream(renamed, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.Read))
                    notGone = Installer.DeleteData(data);
                Check("  ...and names what it could not delete, but not a renamed program still running", notGone == open);
                Check("  ...and is fine when the folder is already gone", Installer.DeleteData(Path.Combine(home, "never")) == null);
                Check("  ...and never touches a folder named relative to wherever it runs", Installer.DeleteData(App.Name) == App.Name);
                // A folder that is a link to another (a junction), where this PC lets the test make one.
                string target = Path.Combine(home, "target"), link = Path.Combine(home, "link"), mineThere = Path.Combine(target, "yours.txt");
                Directory.CreateDirectory(target);
                File.WriteAllText(mineThere, "a file of yours, in the folder the link leads to");
                if (!Junction(link, target)) Console.WriteLine("skip  folder links: this PC won't let the test make one");
                else
                {
                    Check("  ...and a folder that is a link to one holding a file of yours stays, and so does the file",
                        Installer.DeleteData(link) == null && Directory.Exists(link) && File.Exists(mineThere));
                    File.Delete(mineThere);
                    Check("  ...but goes once that folder is empty, which stays", Installer.DeleteData(link) == null && !Directory.Exists(link) && Directory.Exists(target));
                    Directory.Delete(target);
                    Directory.CreateDirectory(target);
                    if (Junction(link, target))
                    {
                        Directory.Delete(target);
                        Check("  ...and a folder that is a link to nowhere is left as it is, not a crash", Installer.DeleteData(link) == link);
                    }
                }
            }
            finally { try { Directory.Delete(home, true); } catch (Exception) { } }
            Check("uninstalled: says so, and that the settings are kept",
                Installer.UninstalledText(false) == "MouseFace uninstalled successfully. Your settings are kept in case you install it again.");
            Check("  ...or just says so when they were deleted too", Installer.UninstalledText(true) == "MouseFace uninstalled successfully.");
            // What setup says when Windows won't start the copy it installed.
            Win32Exception blocked = new Win32Exception(4551);
            string sac = Installer.Problem(blocked, true, "Nothing was installed.");
            Check("a copy Smart App Control blocks: setup says so, and that nothing was installed, and no more",
                sac == "Windows' Smart App Control stopped MouseFace from starting. It runs only apps that Microsoft already knows "
                    + "or that are code-signed, and this release of MouseFace isn't signed yet. Nothing was installed.");
            string rule = Installer.Problem(new Win32Exception(1260), true, null);
            Check("  ...an AppLocker block is not blamed on Smart App Control, even while that is on", !rule.Contains("Smart App Control") && rule.Contains("administrator"));
            Check("  ...nor is a block while Smart App Control is off", !Installer.Problem(blocked, false, null).Contains("Smart App Control"));
            // Windows' own words come in the PC's language, so these checks give their own.
            Check("  ...which closes with Windows' words, with one full stop and single spaces",
                Installer.Problem(new Win32Exception(4556, "Windows' words,\r\n  on two lines."), false, null).EndsWith("(Windows said: \"Windows' words, on two lines.\")"));
            Check("the app control blocks count", Installer.Blocked(1260) && Installer.Blocked(4551) && Installer.Blocked(4556) && Installer.Blocked(4559)
                && Installer.Blocked(4580) && Installer.Blocked(4582));
            Check("  ...the codes beside them don't (policy errors, Virtual Secure Mode, a driver check)", !Installer.Blocked(4552) && !Installer.Blocked(4555)
                && !Installer.Blocked(4560) && !Installer.Blocked(4579) && !Installer.Blocked(4583) && !Installer.Blocked(5));
            Check("any other failure keeps Windows' words, ending them with a full stop, then what was left",
                Installer.Problem(new Win32Exception(5, "Access is denied"), true, "Nothing was installed.") == "Could not install: Access is denied. Nothing was installed.");
            Check("an install Installed apps wouldn't list says so in plain words, and how to uninstall it later",
                Installer.UnlistedNote == "It couldn't be listed in Installed apps, so to uninstall it later, run this setup again and choose Uninstall.");
            // Outcome(first, moved, back, blocked, left, stranded)
            Check("what a failed first install says", Installer.Outcome(true, false, false, true, null, null) == "Nothing was installed.");
            Check("  ...or what it couldn't delete, without seeming to blame Smart App Control for it",
                Installer.Outcome(true, false, false, true, @"C:\x\MouseFace.lnk", null) == @"Setup couldn't delete everything it added, so delete by hand: C:\x\MouseFace.lnk.");
            Check("a failed update says the copy is back", Installer.Outcome(false, true, true, false, null, null) == "The copy that was there is back in place.");
            Check("  ...and, when Windows blocked it, how to remove that copy if it is blocked too", Installer.Outcome(false, true, true, true, null, null).Contains("choose Uninstall"));
            Check("  ...or, when the copy was never moved, that it is still in place", Installer.Outcome(false, false, false, false, null, null) == "The copy that was there is still in place.");
            Check("  ...or, when it could not put the copy back, where that copy is, and how to remove the new one",
                Installer.Outcome(false, true, false, true, null, @"C:\x\MouseFace.exe.old") == @"The copy that was there couldn't be put back; it is saved as C:\x\MouseFace.exe.old. "
                    + "If the new copy doesn't start when you sign in, run this again and choose Uninstall to remove it.");
            // One setup at a time: while one runs, another (on another thread, as another program would be) says to wait.
            string inner = null;
            string outer = Installer.OneAtATime(delegate
            {
                Thread other = new Thread(delegate() { inner = Installer.OneAtATime(delegate { return "ran"; }); });
                other.Start();
                other.Join();
                return "done";
            });
            Check("one setup at a time: a second says to wait while the first runs", outer == "done" && inner == Installer.Busy);
            Check("  ...and goes ahead once it is done", Installer.OneAtATime(delegate { return "ran"; }) == "ran");
            Installer.SmartAppControlOn(); // reads, never throws
            Check("a copy outside the install folder is setup", Installer.IsSetup(Path.Combine(home, App.Name + ".exe")));
            Check("the installed copy is not setup", !Installer.IsSetup(Installer.Exe.ToUpperInvariant()));
            // The entry in Settings > Apps > Installed apps (built here, never written by the tests).
            Dictionary<string, object> entry = Installer.UninstallEntry(@"C:\Users\x\AppData\Local\MouseFace\MouseFace.exe",
                @"C:\Users\x\AppData\Local\MouseFace", 150000, new DateTime(2026, 11, 15));
            Check("Installed apps lists it as MouseFace, by ZebraTree Media, with its version",
                (string)entry["DisplayName"] == "MouseFace" && (string)entry["Publisher"] == "ZebraTree Media" && (string)entry["DisplayVersion"] == App.Version);
            System.Globalization.CultureInfo was = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("th-TH"); // the Thai Buddhist calendar: year 2569
                Same("the install date is the usual one on a PC set to another calendar", "20261115",
                    (string)Installer.UninstallEntry("x", "y", 1, new DateTime(2026, 11, 15))["InstallDate"]);
            }
            finally { Thread.CurrentThread.CurrentCulture = was; }
            Same("Uninstall in Installed apps asks first", "\"C:\\Users\\x\\AppData\\Local\\MouseFace\\MouseFace.exe\" --remove", (string)entry["UninstallString"]);
            Check("tools like winget uninstall without asking", ((string)entry["QuietUninstallString"]).EndsWith("\" --uninstall"));
            Check("it can't be modified or repaired, and gives its size and install date",
                (int)entry["NoModify"] == 1 && (int)entry["NoRepair"] == 1 && (int)entry["EstimatedSize"] == 146 && (string)entry["InstallDate"] == "20261115");
            Check("the entry is under the current user's own key, so it needs no admin rights",
                Installer.UninstallKey == @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MouseFace");

            // Versions: setup never replaces a newer installed version.
            Check("1.1.0 is newer than 1.0.0", Installer.Newer("1.1.0", "1.0.0"));
            Check("1.10.0 is newer than 1.9.0 (numbers, not text)", Installer.Newer("1.10.0", "1.9.0"));
            Check("the same version is not newer", !Installer.Newer("1.0.0", "1.0.0"));
            Check("an older version is not newer", !Installer.Newer("1.0.0", "1.1.0"));
            Check("an unreadable version is never newer", !Installer.Newer("?", "1.0.0") && !Installer.Newer(null, "1.0.0"));

            // The Settings window writes only what changed in it, so an edit made in the file while the
            // window was open survives a Save.
            Dictionary<string, string> shown = new Dictionary<string, string>();
            shown["Buttons/Middle"] = "Right"; shown["Buttons/Back"] = "Back"; shown["Dimming/Step"] = "3";
            Dictionary<string, string> now = new Dictionary<string, string>(shown);
            now["Buttons/Middle"] = "Double";
            Same("a Save keeps a file edit the window didn't change, and writes the one it did",
                "[Buttons]\r\nMiddle = Double\r\nBack = Keys Win+D\r\n\r\n[Dimming]\r\nStep = 3",
                SettingsForm.WithChanges("[Buttons]\r\nMiddle = Right\r\nBack = Keys Win+D\r\n\r\n[Dimming]\r\nStep = 3", shown, now));
            Same("a Save with nothing changed leaves the file as it is", "[Buttons]\r\nBack = Keys Win+D",
                SettingsForm.WithChanges("[Buttons]\r\nBack = Keys Win+D", shown, shown));

            // The icons are built in, each with a frame drawn for every tray size.
            foreach (string ico in new string[] { App.Name + ".ico", App.Name + "-paused.ico" })
                foreach (int px in new int[] { 16, 20, 24, 32 })
                    using (System.Drawing.Icon i = App.LoadIcon(ico, new System.Drawing.Size(px, px)))
                        Check(ico + " has a " + px + " px frame", i.Width == px && i.Height == px);

            // Recording a shortcut by pressing it.
            Same("Ctrl+C records as Ctrl+C", "Ctrl+C", ShortcutBox.Chord(Keys.Control | Keys.C, false));
            Same("Ctrl+Shift+T records in settings.ini order", "Ctrl+Shift+T", ShortcutBox.Chord(Keys.Control | Keys.Shift | Keys.T, false));
            Same("Alt+F4 records", "Alt+F4", ShortcutBox.Chord(Keys.Alt | Keys.F4, false));
            Same("Ctrl held alone records as Ctrl", "Ctrl", ShortcutBox.Chord(Keys.ControlKey | Keys.Control, false));
            Same("Win with a letter records", "Win+E", ShortcutBox.Chord(Keys.E, true));
            Check("a key settings.ini has no name for records nothing", ShortcutBox.Chord(Keys.NumPad5, false) == null);
            using (ShortcutBox box = new ShortcutBox())
            {
                bool taken = box.Press(Keys.Control | Keys.V);
                Check("the box takes Ctrl+V for itself, before the text box's paste", taken && box.Text == "Ctrl+V");
                bool tab = box.Press(Keys.Tab);
                Check("Tab still moves on, and is not recorded", !tab && box.Text == "Ctrl+V");
                bool win = box.Press(Keys.LWin | Keys.None);
                Check("the Windows key explains itself and records nothing", win && box.Text == "Ctrl+V" && box.WindowsKeyHints == 1);
                foreach (string chord in new string[] { "Ctrl+C", "Ctrl+V", "Ctrl+Shift+T", "Alt+F4", "Ctrl" })
                {
                    ushort[] keys; string canonical, why;
                    Check("a recorded " + chord + " is one settings.ini accepts", KeyNames.TryParseChord(chord, out keys, out canonical, out why) && canonical == chord);
                }
            }
            Check("a rewritten file still parses", Config.Parse(changed, out error) != null && Config.Parse(changed, out error).Match.Text == "1A7C:0191");
        }

        static DeviceIds Ids(string path) { return DevicePath.Parse(path); }

        static void Matching()
        {
            string usb = @"\\?\HID#VID_1A7C&PID_0191#7&12d7d3a2&3&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
            string receiver = @"\\?\HID#VID_046D&PID_C52B&MI_01&Col01#8&2b2c2d&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
            string classic = @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&00021a7c_PID&0191&Col01#9&1a2b3c&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
            string ble = @"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&021a7c_PID&0191_REV&0001_c0ffee123456&Col01#a&1b2c3d&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
            string touchpad = @"\\?\HID#ELAN06FA&Col01#5&206f74c9&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
            string rdp = @"\\?\Root#RDP_MOU#0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
            Same("USB mouse: IDs and bus", "1A7C:0191 USB", Ids(usb) + " " + Ids(usb).Bus);
            Same("USB receiver interface: IDs and bus", "046D:C52B USB", Ids(receiver) + " " + Ids(receiver).Bus);
            Same("Bluetooth Classic mouse: IDs and bus", "1A7C:0191 Bluetooth", Ids(classic) + " " + Ids(classic).Bus);
            Same("Bluetooth Low Energy mouse: IDs and bus", "1A7C:0191 Bluetooth", Ids(ble) + " " + Ids(ble).Bus);
            Same("built-in touchpad: no IDs", "no ID other", Ids(touchpad) + " " + Ids(touchpad).Bus);
            Same("remote desktop's mouse: no IDs", "no ID other", Ids(rdp) + " " + Ids(rdp).Bus);

            string sig = @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&00011a7c_PID&0191#9&1#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
            MatchRule evo = MatchRule.Evoluent, one, any, two;
            string err;
            Check("Evoluent: any Evoluent over USB, Bluetooth Classic and Low Energy", evo.Matches(Ids(usb)) && evo.Matches(Ids(classic)) && evo.Matches(Ids(ble)));
            Check("Evoluent: not another maker's mouse, the touchpad or remote desktop", !evo.Matches(Ids(receiver)) && !evo.Matches(Ids(touchpad)) && !evo.Matches(Ids(rdp)));
            Check("Evoluent: not a Bluetooth device whose vendor number was issued by the Bluetooth SIG", Ids(sig).SigVendor && !evo.Matches(Ids(sig)));
            Check("a chosen vendor:product matches it all the same", MatchRule.TryParse("1A7C:0191", out one, out err) && one.Matches(Ids(sig)));
            Check("1A7C:0191 parses", MatchRule.TryParse("1a7c:0191", out one, out err) && one.Text == "1A7C:0191");
            Check("1A7C:0191 matches that model only", one.Matches(Ids(usb)) && !one.Matches(Ids(@"\\?\HID#VID_1A7C&PID_0195#x")));
            Check("Any matches every mouse with an ID", MatchRule.TryParse("any", out any, out err) && any.Matches(Ids(receiver)) && any.Matches(Ids(usb)) && any.Matches(Ids(classic)));
            Check("  ...but not a touchpad or remote desktop's mouse, which have none", !any.Matches(Ids(touchpad)) && !any.Matches(Ids(rdp)));
            Check("a list: Evoluent, 046D", MatchRule.TryParse("Evoluent, 046D", out two, out err) && two.Matches(Ids(receiver)) && two.Matches(Ids(usb)) && two.Text == "Evoluent, 046D");
            Check("rejects a name it does not know", !MatchRule.TryParse("Logitech", out one, out err));
            Check("rejects a short ID", !MatchRule.TryParse("1A7:0191", out one, out err));
            Check("rejects three parts", !MatchRule.TryParse("1A7C:0191:1", out one, out err));
        }

        static void AttributionChecks()
        {
            IntPtr mouse = new IntPtr(0x101), other = new IntPtr(0x202);
            ushort middleDown = Buttons.RawFlag(Btn.Middle, true), middleUp = Buttons.RawFlag(Btn.Middle, false);
            Check("Raw Input flags: Left down 0x1, Right up 0x8, Back down 0x40, Forward up 0x200",
                Buttons.RawFlag(Btn.Left, true) == 0x1 && Buttons.RawFlag(Btn.Right, false) == 0x8 && Buttons.RawFlag(Btn.Back, true) == 0x40 && Buttons.RawFlag(Btn.Forward, false) == 0x200);
            Attribution a = new Attribution();
            IntPtr dev;
            a.OnReport(mouse, middleDown, 1000);
            Check("a click claims the report that came before it", a.Claim(middleDown, 1001, out dev) && dev == mouse);
            Check("  ...only once", !a.Claim(middleDown, 1002, out dev));
            Check("a click with no report of its own finds none", !a.Claim(middleUp, 1003, out dev) && dev == IntPtr.Zero);
            a.OnReport(IntPtr.Zero, middleDown, 1004);
            Check("a report with no device (a program, this one's own clicks, a precision touchpad) names none", !a.Claim(middleDown, 1005, out dev));
            a.OnReport(mouse, middleDown, 2000);
            Check("a report older than " + Attribution.MaxAgeMs + " ms is not claimed", !a.Claim(middleDown, 2000 + Attribution.MaxAgeMs + 1, out dev));
            a.OnReport(mouse, middleDown, 3000); // an Evoluent press an admin window kept from the hook
            a.OnReport(IntPtr.Zero, middleDown, 3010);
            Check("a precision touchpad's click right after one the hook never heard is not taken for the mouse's", !a.Claim(middleDown, 3011, out dev));
            a.OnReport(mouse, middleDown, 3100);
            a.OnReport(other, middleDown, 3110);
            Check("nor is a second mouse's", a.Claim(middleDown, 3111, out dev) && dev == other);
            a.OnReport(mouse, (ushort)(middleDown | Buttons.RawFlag(Btn.Left, false)), 4000);
            Check("one report with two buttons serves both", a.Claim(middleDown, 4001, out dev) && dev == mouse && a.Claim(Buttons.RawFlag(Btn.Left, false), 4001, out dev) && dev == mouse);
            a.OnReport(other, Buttons.RawWheel, 5000);
            a.OnReport(mouse, Buttons.RawWheel, 5001);
            Check("the wheel claims its newest report", a.Claim(Buttons.RawWheel, 5002, out dev) && dev == mouse);
            Check("  ...once", !a.Claim(Buttons.RawWheel, 5003, out dev));
            Check("tick counts that wrap past zero still age correctly", Wraps(a, mouse, middleUp));
        }

        static bool Wraps(Attribution a, IntPtr mouse, ushort flag)
        {
            IntPtr dev;
            a.OnReport(mouse, flag, int.MaxValue - 5);
            return a.Claim(flag, int.MinValue + 5, out dev) && dev == mouse;
        }

        static void Watchdog()
        {
            HookWatch w = new HookWatch();
            Check("dead hook: the hook heard something recently, all is well", !w.OnChosenMouseUse(10000, 9990, 0, 0));
            Check("dead hook: silent over 3 s while the mouse is used, first report: not yet", !w.OnChosenMouseUse(20000, 16000, 100, 100));
            Check("dead hook: still silent 1 s later: not yet", !w.OnChosenMouseUse(21000, 16000, 150, 100));
            Check("dead hook: still silent 2 s later, the pointer moving: the hook looks dropped", w.OnChosenMouseUse(22000, 16000, 200, 100));
            w = new HookWatch();
            w.OnChosenMouseUse(20000, 16000, 100, 100);
            w.OnChosenMouseUse(21000, 20900, 150, 100);
            Check("dead hook: the hook hearing something in between starts the count again", !w.OnChosenMouseUse(22100, 16000, 200, 100) && !w.OnChosenMouseUse(23000, 16000, 250, 100));
            w = new HookWatch();
            w.OnChosenMouseUse(10000, 5000, 100, 100); // one late reading...
            Check("two late readings far apart, the hook heard between them: not dropped", !w.OnChosenMouseUse(610000, 600000, 300, 300));
            w = new HookWatch();
            w.OnChosenMouseUse(20000, 16000, 960, 540);
            w.OnChosenMouseUse(21000, 16000, 960, 540);
            Check("silent while the pointer holds still (a tool has taken the mouse to another PC): not dropped", !w.OnChosenMouseUse(23000, 16000, 960, 540));
            RehookBudget b = new RehookBudget();
            bool r1 = b.Allow(1000), r2 = b.Allow(2000), r3 = b.Allow(3000);
            Check("a dropped hook is re-attached up to " + RehookBudget.Limit + " times", r1 && r2 && r3);
            Check("a 4th drop within 10 minutes stops instead", !b.Allow(4000));
            Check("drops older than 10 minutes no longer count", b.Allow(4000 + RehookBudget.WindowMs + 1));
        }

        static void InjectorChecks()
        {
            Check("INPUT struct size matches Windows (" + Marshal.SizeOf(typeof(Native.INPUT)) + " bytes)",
                Marshal.SizeOf(typeof(Native.INPUT)) == (IntPtr.Size == 8 ? 40 : 28));
            List<Out> outs = new List<Out>();
            outs.Add(Out.Mouse(Btn.Right, true));
            outs.Add(Out.Mouse(Btn.Back, false));
            outs.Add(Out.Key(0x11, true));
            outs.Add(Out.Key(0x26, false));
            IntPtr tag = new IntPtr(77);
            Native.INPUT[] i = Injector.Build(outs, tag, false);
            Check("a right press becomes a mouse input with the right-down flag", i[0].type == Native.INPUT_MOUSE && i[0].u.mi.dwFlags == Native.MOUSEEVENTF_RIGHTDOWN && i[0].u.mi.dwExtraInfo == tag);
            Check("a Back release is X button 1 up", i[1].u.mi.dwFlags == Native.MOUSEEVENTF_XUP && i[1].u.mi.mouseData == 1);
            Check("a Ctrl press is a keyboard input, not flagged up", i[2].type == Native.INPUT_KEYBOARD && i[2].u.ki.wVk == 0x11 && (i[2].u.ki.dwFlags & Native.KEYEVENTF_KEYUP) == 0 && i[2].u.ki.dwExtraInfo == tag);
            Check("an Up arrow release is flagged up and extended", i[3].u.ki.dwFlags == (Native.KEYEVENTF_KEYUP | Native.KEYEVENTF_EXTENDEDKEY));
            Native.INPUT[] sw = Injector.Build(outs, tag, true);
            Check("buttons swapped in Windows: a Right press is sent as the physical left button", sw[0].u.mi.dwFlags == Native.MOUSEEVENTF_LEFTDOWN);
            Check("buttons swapped: Back stays Back", sw[1].u.mi.dwFlags == Native.MOUSEEVENTF_XUP && sw[1].u.mi.mouseData == 1);
            Check("buttons swapped: the hook's Left is the physical right button in Raw Input",
                Buttons.RawFlag(Buttons.Physical(Btn.Left, true), true) == 0x4 && Buttons.RawFlag(Buttons.Physical(Btn.Right, true), false) == 0x2
                && Buttons.RawFlag(Buttons.Physical(Btn.Middle, true), true) == 0x10);

            Injector inj = new Injector();
            Check("the click sender is not stuck before it sends", !inj.Stuck());
            inj.MarkSending();
            Check("a send that has just started is not stuck (start time written before the busy flag)", !inj.Stuck());
            FieldInfo started = typeof(Injector).GetField("sendStartedTick", BindingFlags.Instance | BindingFlags.NonPublic);
            started.SetValue(inj, Environment.TickCount - 1000);
            Check("a send busy for 1 s is slow, not stuck (other programs' hooks can delay it)", !inj.Stuck());
            started.SetValue(inj, Environment.TickCount - Injector.StuckMs - 500);
            Check("a send busy for over " + Injector.StuckMs + " ms is stuck", inj.Stuck());
            inj.MarkDone();
            Check("a finished send is not stuck", !inj.Stuck());
            List<Out> two = new List<Out>();
            two.Add(Out.Mouse(Btn.Left, false));
            inj.Enqueue(two);
            Check("the crash handler can take what was queued but not sent", inj.TakeUnsent().Count == 1 && inj.TakeUnsent().Count == 0);
        }

        static void Dimming()
        {
            Check("dims when below full, switched on, the mouse attached and not paused", Dimmer.Wanted(60, true, true, false));
            Check("full brightness when the chosen mouse is gone (KVM switched away)", !Dimmer.Wanted(60, true, false, false));
            Check("full brightness when dimming is switched off in settings", !Dimmer.Wanted(60, false, true, false));
            Check("full brightness while paused", !Dimmer.Wanted(60, true, true, true));
            Check("nothing to show at 100%", !Dimmer.Wanted(100, true, true, false));
            int rem = 0;
            Check("wheel up at full brightness stays at 100", Dimmer.StepLevel(100, 120, 2, ref rem) == 100);
            rem = 0;
            Check("one notch down dims by the step (2)", Dimmer.StepLevel(100, -120, 2, ref rem) == 98);
            rem = 0;
            Check("one notch down dims by the step (5)", Dimmer.StepLevel(100, -120, 5, ref rem) == 95);
            rem = 0;
            Check("dimming stops at the 10% floor", Dimmer.StepLevel(Dimmer.MinLevel + 1, -1200, 2, ref rem) == Dimmer.MinLevel);
            rem = 0;
            int half = Dimmer.StepLevel(50, 60, 2, ref rem);
            Check("two half notches make one step", half == 50 && Dimmer.StepLevel(half, 60, 2, ref rem) == 52);
            Check("chord: Shift + wheel", Config.IsDimChord(DimKey.Shift, true, false, false, false));
            Check("chord: plain wheel scrolls", !Config.IsDimChord(DimKey.Shift, false, false, false, false));
            Check("chord: Ctrl+Shift + wheel is left alone", !Config.IsDimChord(DimKey.Shift, true, true, false, false));
            Check("chord: Shift+Alt is not the Shift chord", !Config.IsDimChord(DimKey.Shift, true, false, true, false));
            Check("chord: Shift+Alt when set to Shift+Alt", Config.IsDimChord(DimKey.ShiftAlt, true, false, true, false));
            Check("chord: Shift+Win when set to Shift+Win", Config.IsDimChord(DimKey.ShiftWin, true, false, false, true));
            Check("chord: Off never dims", !Config.IsDimChord(DimKey.Off, true, false, false, false));
        }

        static string Grown(string left, string inner, string right)
        {
            int addLeft, addRight;
            WholeNameSelector.Grow(left, inner, right, out addLeft, out addRight);
            return left.Substring(left.Length - addLeft) + inner + right.Substring(0, addRight);
        }

        static void Grows(string name, string left, string inner, string right, string want)
        {
            Same("whole name: " + name, want, Grown(left, inner, right));
        }

        static void WholeNames()
        {
            // Left of the selection, the selection a program made, right of it.
            Grows("a click on 'release' selects the whole file name", "see ", "release", "-notes-v2.md now", "release-notes-v2.md");
            Grows("a click in the middle (notes) does too", "see release-", "notes", "-v2.md now", "release-notes-v2.md");
            Grows("a path is one name", "at docs/guides/", "release", "-notes-v2.md here", "docs/guides/release-notes-v2.md");
            Grows("a full stop ending the sentence is left out", "see ", "release", "-notes-v2.md.", "release-notes-v2.md");
            Grows("so are a comma and a colon", "see ", "release", "-notes.md, then", "release-notes.md");
            Grows("brackets end the name", "see (", "release", "-notes-v2.md) now", "release-notes-v2.md");
            Grows("quotes and backticks end the name", "run '", "install", ".ps1' or `x`", "install.ps1");
            Grows("a terminal panel's box border ends the name", "\u2502 ", "release", "-notes.md \u2502", "release-notes.md");
            Grows("a line break ends the name", "end\r\n", "release", "-notes\r\nnext", "release-notes");
            Grows("a plain word stays as it is", "plain ", "words", " only", "words");
            Grows("a URL is one name, minus the full stop", "at ", "https", "://example.com/a-b?c=d.", "https://example.com/a-b?c=d");
            Grows("a selected bracket does not grow", "x ", "(", "release-notes) y", "(");
            Grows("at the very start and end of the text", "", "release", "-notes", "release-notes");
            Check("control: a wrong whole-name expectation is caught", Grown("see ", "release", "-notes.md") != "release");
        }
    }

    // Checks that need a signed-in desktop: real windows, the hook, Raw Input, UI Automation. They
    // install a hook that lets everything through and send no input.
    static class SystemChecks
    {
        static void Check(string n, bool ok) { TestMain.Check(n, ok); }

        static void Bounds(Control c, Dictionary<Control, Rectangle> into)
        {
            foreach (Control child in c.Controls) { into[child] = child.Bounds; Bounds(child, into); }
        }

        internal static int Run()
        {
            using (Shade s = new Shade(new Rectangle(0, 0, 10, 10)))
            {
                int ex = Native.GetWindowLong(s.Handle, Native.GWL_EXSTYLE);
                Check("the dimming layer is click-through, layered, on top and never takes focus", (ex & Native.OverlayStyles) == Native.OverlayStyles);
            }
            bool uia;
            try { uia = ((IUIAutomation)new CUIAutomation()).GetRootElement() != null; } catch (Exception) { uia = false; }
            Check("UI Automation is available for whole-name selection", uia);
            int own = Desk.IntegrityOf(Native.GetCurrentProcess());
            Check("reads its own integrity level (" + own.ToString("X") + "; medium is 2000)", own >= 0x1000);

            string error;
            bool unreadable;
            Config c = SettingsFile.Load(true, out error, out unreadable);
            Check("first start writes the default settings.ini, which reads back", c != null && File.Exists(SettingsFile.FilePath) && !c.RemapsAnything);

            // A hand edit made while the Settings window is open.
            using (SettingsForm f = new SettingsForm(Unit.Parse("[Buttons]\nMiddle = Right\nBack = None\n"), delegate(string text) { return null; }, null))
            {
                f.Pick(Btn.Back, "Double click"); // chosen in the window...
                f.Reload(Unit.Parse("[Buttons]\nMiddle = Double\nBack = Left\n")); // ...while settings.ini is edited by hand
                Dictionary<string, string> showing = f.Showing();
                Check("a hand edit shows in the open Settings window", showing["Buttons/Middle"] == "Double");
                Check("  ...but not over a choice made in the window", showing["Buttons/Back"] == "Double");
                TestMain.Same("  ...and Save then writes only that choice", "[Buttons]\r\nMiddle = Double\r\nBack = Double",
                    SettingsForm.WithChanges("[Buttons]\r\nMiddle = Double\r\nBack = Left", f.InFile, showing));
            }

            HookThread h = new HookThread(new Remapper(), new Injector());
            bool up = h.Start();
            Check("the hook installs on its own thread", up && h.ThreadId != Native.GetCurrentThreadId());
            Check("Raw Input reports the mouse to the hook thread, in the background too", h.HearsMouse);
            IntPtr before = h.HookHandle;
            if (up) h.Post(HookThread.WM_APP_REHOOK);
            Stopwatch rw = Stopwatch.StartNew();
            while (h.HookHandle == before && rw.ElapsedMilliseconds < 2000) Thread.Sleep(5);
            Check("re-attaching swaps in a fresh hook on the hook thread", up && h.HookHandle != before && h.Hooked);
            Check("the hook thread lets go, unhooks and ends when asked", h.Quit(2000) && !h.Hooked);

            Check("started without admin rights, so it would run", !Desk.ElevatedByUac());

            // Installed apps runs the installed copy itself to uninstall it: a running program's file
            // can't be deleted, but it can be moved out of the way. A copy of these tests stands in.
            string home = Path.Combine(Path.GetTempPath(), App.Name + "-aside-test-" + Process.GetCurrentProcess().Id);
            string running = Path.Combine(home, "running.exe");
            Directory.CreateDirectory(home);
            File.Copy(Assembly.GetExecutingAssembly().Location, running, true);
            using (Process p = Process.Start(new ProcessStartInfo(running, "--wait") { UseShellExecute = false, CreateNoWindow = true }))
            {
                Thread.Sleep(500);
                try { File.Delete(running); } catch (UnauthorizedAccessException) { }
                Check("(a running program's file can't be deleted)", File.Exists(running) && !p.HasExited);
                string aside = null;
                try { aside = Installer.MoveAside(running); } catch (Exception ex) { Console.WriteLine("      " + ex.Message); }
                Check("but it can be moved to the temp folder, so uninstalling from Installed apps works", aside != null && !File.Exists(running) && File.Exists(aside));
                try { p.Kill(); } catch (Exception) { }
                p.WaitForExit(5000);
                if (aside != null) try { File.Delete(aside); } catch (Exception) { }
            }
            try { Directory.Delete(home, true); } catch (Exception) { }

            // MouseFace's message window: focus starts on its first button, never on the tick box, which
            // starts unticked; moved to a screen scaled differently, its icon is drawn afresh at the new size.
            using (MessageForm m = new MessageForm(App.Name, "Uninstall " + App.Name + "?", MessageForm.Kind.Question, "Uninstall", "Cancel", Installer.DeleteSettingsText))
            {
                m.StartPosition = FormStartPosition.Manual;
                m.Location = new Point(-20000, -20000);
                m.ShowInTaskbar = false;
                m.Show();
                Application.DoEvents();
                Button focused = m.ActiveControl as Button;
                Check("the uninstall question starts with focus on Uninstall, not on the tick box, and unticked",
                    focused != null && focused.Text == "Uninstall" && !m.Ticked);
                int was = m.IconImage.Width;
                m.RescaleTo(m.Dpi * 2);
                Application.DoEvents();
                Check("  ...and on a screen scaled twice as much its icon is drawn again at its new size (" + was + " to " + m.IconImage.Width + " px)",
                    m.IconImage.Width == m.IconSize && m.IconSize >= was * 2 - 2);
                m.Close();
            }
            // It opens over setup's Uninstall: the second click of a double-click there must not answer it.
            using (MessageForm m = new MessageForm(App.Name, "Uninstall " + App.Name + "?", MessageForm.Kind.Question, "Uninstall", "Cancel", Installer.DeleteSettingsText))
            {
                m.StartPosition = FormStartPosition.Manual;
                m.Location = new Point(-20000, -20000);
                m.ShowInTaskbar = false;
                m.Show();
                Application.DoEvents();
                Button yes = (Button)m.AcceptButton;
                yes.PerformClick();
                bool ignored = m.DialogResult == DialogResult.None;
                Thread.Sleep(SystemInformation.DoubleClickTime + 100);
                yes.PerformClick();
                Check("  ...and ignores a click on Uninstall within a double-click's time of opening, but not one after",
                    ignored && m.DialogResult == DialogResult.OK);
                m.Close();
            }

            // A window moved to a screen scaled differently grows or shrinks to match, and back again.
            Config shown = TestMain.SampleConfig();
            using (SettingsForm f = new SettingsForm(shown, delegate(string text) { return null; }, null))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-20000, -20000);
                f.ShowInTaskbar = false;
                f.Show();
                Application.DoEvents();
                // One trip there and back first: a window's very first layout can be a pixel or two off
                // (a dropdown's row), and the first rescale lays it out afresh.
                int dpi = f.Dpi;
                f.RescaleTo(dpi * 3 / 2);
                f.RescaleTo(dpi);
                Application.DoEvents();
                int w = f.Width, ht = f.Height;
                float text = f.Font.Size;
                Dictionary<Control, Rectangle> was = new Dictionary<Control, Rectangle>();
                Bounds(f, was);
                f.RescaleTo(dpi * 3 / 2);
                Application.DoEvents();
                double grew = f.Width / (double)w, grewH = f.Height / (double)ht;
                Check("a window moved to a screen at 150% of this one's scaling grows to match (" + grew.ToString("0.00") + "x wide, "
                    + grewH.ToString("0.00") + "x tall)", grew > 1.4 && grew < 1.6 && grewH > 1.4 && grewH < 1.6 && Math.Abs(f.Font.Size / text - 1.5) < 0.01);
                f.RescaleTo(dpi);
                Application.DoEvents();
                Dictionary<Control, Rectangle> back = new Dictionary<Control, Rectangle>();
                Bounds(f, back);
                int moved = 0;
                foreach (KeyValuePair<Control, Rectangle> b in was)
                    if (back[b.Key] != b.Value)
                    {
                        moved++;
                        Console.WriteLine("info    " + b.Key.GetType().Name + " \"" + b.Key.Text + "\": " + b.Value + " -> " + back[b.Key]);
                    }
                Check("and moved back, every control returns exactly to its size and place (" + w + "x" + ht + " -> " + f.Width + "x" + f.Height + ")",
                    moved == 0 && f.Width == w && f.Height == ht && f.Font.Size == text);
                f.Close();
            }

            List<MouseDevice> mice = Devices.Mice();
            Check("lists the pointing devices", mice != null);
            if (mice != null)
                foreach (MouseDevice d in mice)
                    Console.WriteLine("info  " + Devices.Describe(d) + (MatchRule.Evoluent.Matches(d.Ids) ? "  <- an Evoluent" : ""));
            string conflict = Conflicts.Running();
            Console.WriteLine("info  conflicting mouse software running: " + (conflict ?? "none"));
            Console.WriteLine("info  Windows has left and right swapped: " + (Native.GetSystemMetrics(Native.SM_SWAPBUTTON) != 0 ? "yes" : "no"));
            return TestMain.Done();
        }
    }
}
