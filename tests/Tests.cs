using System;
using System.Collections.Generic;
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
            string error;
            Config c = Config.Parse("[Buttons]\nMiddle = Right\nRight = Double\nBack = Keys Alt+Left\nForward = None\n", out error);
            if (c == null) { Console.WriteLine(error); return 1; }
            SettingsForm settings = new SettingsForm(c, delegate(string text) { return null; }, null);
            Shot(settings, Path.Combine(dir, "settings.png"), delegate { settings.OnButton(Btn.Right, true, true); });
            SettingsForm touchpad = new SettingsForm(c, delegate(string text) { return null; }, null);
            Shot(touchpad, Path.Combine(dir, "settings-touchpad.png"), delegate { touchpad.OnButton(Btn.Left, false, false); });
            Shot(new AboutForm(), Path.Combine(dir, "about.png"), null);
            Shot(new SetupForm(Assembly.GetExecutingAssembly().Location), Path.Combine(dir, "setup.png"), null);
            using (Form host = new Form())
            {
                host.Show();
                Shot(new Toast("Windows key", "Shortcuts with the Windows key can't be recorded here. Type them in settings.ini.", host, 60000),
                    Path.Combine(dir, "toast.png"), null);
                host.Close();
            }
            return 0;
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
                try { result = new WholeNameSelector().SelectAt(x, y); }
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

            Same("Keys Ctrl+Shift+T: press holds the keys down in order", "swallow Ctrl+ Shift+ T+", D(r, Btn.Forward, true));
            Same("  ...release lets them go in reverse", "swallow T- Shift- Ctrl-", D(r, Btn.Forward, false));
            Same("Keys Win: a modifier alone is held while the button is", "swallow Win+", D(r, Btn.Middle, true));
            released.Clear();
            r.ReleaseAll(released);
            Same("letting go of everything lets held keys go", "Win-", Out.Describe(released));

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

            // Setup's file steps, in a folder of the test's own.
            string home = Path.Combine(Path.GetTempPath(), App.Name + "-setup-test-" + Process.GetCurrentProcess().Id);
            string dir = Path.Combine(home, "app"), startup = Path.Combine(home, "Startup", App.Name + ".lnk"), menu = Path.Combine(home, "Programs", App.Name + ".lnk");
            try
            {
                Installer.Place(Assembly.GetExecutingAssembly().Location, dir, startup, menu);
                Check("setup copies the program into its folder", File.Exists(Path.Combine(dir, App.Name + ".exe")));
                Check("setup makes the sign-in and Start menu shortcuts", File.Exists(startup) && File.Exists(menu));
                File.WriteAllText(Path.Combine(dir, "settings.ini"), "[Mouse]");
                Installer.Remove(dir, startup, menu);
                Check("uninstall removes the program and both shortcuts", !File.Exists(Path.Combine(dir, App.Name + ".exe")) && !File.Exists(startup) && !File.Exists(menu));
                Check("uninstall keeps settings.ini", File.Exists(Path.Combine(dir, "settings.ini")));
            }
            finally { try { Directory.Delete(home, true); } catch (Exception) { } }
            Check("a copy outside the install folder is setup", Installer.IsSetup(Path.Combine(home, App.Name + ".exe")));
            Check("the installed copy is not setup", !Installer.IsSetup(Installer.Exe.ToUpperInvariant()));

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
            Check("Any matches every device, even one with no IDs", MatchRule.TryParse("any", out any, out err) && any.Matches(Ids(touchpad)) && any.Matches(Ids(receiver)));
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
            Check("a click claims the report that came before it", a.Claim(middleDown, false, 1001, out dev) && dev == mouse);
            Check("  ...only once", !a.Claim(middleDown, false, 1002, out dev));
            Check("a click with no report of its own (a precision touchpad) finds none", !a.Claim(middleUp, false, 1003, out dev) && dev == IntPtr.Zero);
            a.OnReport(IntPtr.Zero, middleDown, 1004);
            Check("a report with no device (a program, this one's own clicks) is never claimed", !a.Claim(middleDown, false, 1005, out dev));
            a.OnReport(mouse, middleDown, 2000);
            Check("a report older than " + Attribution.MaxAgeMs + " ms is not claimed", !a.Claim(middleDown, false, 2000 + Attribution.MaxAgeMs + 1, out dev));
            a.OnReport(mouse, middleDown, 3000);
            a.OnReport(other, middleDown, 3001);
            Check("two presses close together are claimed in order (first)", a.Claim(middleDown, false, 3002, out dev) && dev == mouse);
            Check("two presses close together are claimed in order (second)", a.Claim(middleDown, false, 3003, out dev) && dev == other);
            a.OnReport(mouse, (ushort)(middleDown | Buttons.RawFlag(Btn.Left, false)), 4000);
            Check("one report with two buttons serves both", a.Claim(middleDown, false, 4001, out dev) && dev == mouse && a.Claim(Buttons.RawFlag(Btn.Left, false), false, 4001, out dev) && dev == mouse);
            a.OnReport(other, Buttons.RawWheel, 5000);
            a.OnReport(mouse, Buttons.RawWheel, 5001);
            Check("the wheel claims its newest report", a.Claim(Buttons.RawWheel, true, 5002, out dev) && dev == mouse);
            Check("  ...and clears the rest", !a.Claim(Buttons.RawWheel, true, 5003, out dev));
            for (int i = 0; i < 9; i++) a.OnReport(i == 0 ? other : mouse, middleDown, 6000 + i);
            Check("a ninth report waiting drops the oldest", a.Claim(middleDown, false, 6010, out dev) && dev == mouse);
            Check("tick counts that wrap past zero still age correctly", Wraps(a, mouse, middleUp));
        }

        static bool Wraps(Attribution a, IntPtr mouse, ushort flag)
        {
            IntPtr dev;
            a.OnReport(mouse, flag, int.MaxValue - 5);
            return a.Claim(flag, false, int.MinValue + 5, out dev) && dev == mouse;
        }

        static void Watchdog()
        {
            HookWatch w = new HookWatch();
            Check("dead hook: the hook heard something recently, all is well", !w.OnChosenMouseUse(10000, 9990));
            Check("dead hook: silent over 3 s while the mouse is used, first report: not yet", !w.OnChosenMouseUse(20000, 16000));
            Check("dead hook: still silent 1 s later: not yet", !w.OnChosenMouseUse(21000, 16000));
            Check("dead hook: still silent 2 s later: the hook looks dropped", w.OnChosenMouseUse(22000, 16000));
            w = new HookWatch();
            w.OnChosenMouseUse(20000, 16000);
            w.OnChosenMouseUse(21000, 20900);
            Check("dead hook: the hook hearing something in between starts the count again", !w.OnChosenMouseUse(22100, 16000) && !w.OnChosenMouseUse(23000, 16000));
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
            Config c = SettingsFile.Load(out error);
            Check("first start writes the default settings.ini, which reads back", c != null && File.Exists(SettingsFile.FilePath) && !c.RemapsAnything);

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
