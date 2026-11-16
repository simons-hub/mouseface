using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MouseFace.Tests
{
    // Tagged test clicks through Windows' real input pipeline, with the real hook, injector and
    // timing. Three hooks, called in this order (the last installed is called first):
    //   probe     records each test click and each replacement, and swallows every replacement
    //   MouseFace   the program's own hook, deciding as it would for the chosen mouse
    //   backstop  swallows anything tagged that got past MouseFace, and counts it as a failure
    // So no test click and no replacement ever reaches a program. The real mouse is left alone,
    // but another remapper running at the same time would see the traffic, so this refuses to run
    // next to one.
    static class LiveTest
    {
        static readonly IntPtr CanaryTag = new IntPtr(0x4D46434E);
        static readonly Native.HookProc probeMouse = OnProbeMouse, probeKeys = OnProbeKeys, backstop = OnBackstop;
        static int probeThread, backstopThread;
        static IntPtr probeMouseHook, probeKeysHook, backstopHook;
        static volatile bool canarySeen;
        static int leaks;

        const int Max = 8192;
        static readonly long[] when = new long[Max];
        static readonly string[] what = new string[Max];
        static readonly bool[] isOut = new bool[Max];
        static int recorded;

        const double MaxLatencyMs = 15.0;
        static double worstLatency;

        static void Check(string n, bool ok) { TestMain.Check(n, ok); }

        static void Record(string name, bool output)
        {
            int i = Interlocked.Increment(ref recorded) - 1;
            if (i >= Max) return;
            when[i] = Stopwatch.GetTimestamp();
            what[i] = name;
            isOut[i] = output;
        }

        static IntPtr OnProbeMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                IntPtr extra = Marshal.ReadIntPtr(lParam, Native.MouseExtraOffset);
                if (extra == CanaryTag) { canarySeen = true; return new IntPtr(1); }
                if (extra == Injector.Tag || extra == HookThread.TestTag)
                {
                    Record(MouseName(wParam.ToInt32(), (uint)Marshal.ReadInt32(lParam, 8)), extra == Injector.Tag);
                    if (extra == Injector.Tag) return new IntPtr(1); // a replacement: never let it reach a program
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        static IntPtr OnProbeKeys(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Marshal.ReadIntPtr(lParam, Native.KeyExtraOffset) == Injector.Tag)
            {
                bool up = (Marshal.ReadInt32(lParam, 8) & 0x80) != 0; // LLKHF_UP
                Record(KeyNames.NameOf((ushort)Marshal.ReadInt32(lParam, 0)) + (up ? "-" : "+"), true);
                return new IntPtr(1);
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        static IntPtr OnBackstop(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                IntPtr extra = Marshal.ReadIntPtr(lParam, Native.MouseExtraOffset);
                if (extra == HookThread.TestTag || extra == Injector.Tag || extra == CanaryTag)
                {
                    if (extra == HookThread.TestTag) Interlocked.Increment(ref leaks);
                    return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        static string MouseName(int msg, uint data)
        {
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN: return "Left+";
                case Native.WM_LBUTTONUP: return "Left-";
                case Native.WM_RBUTTONDOWN: return "Right+";
                case Native.WM_RBUTTONUP: return "Right-";
                case Native.WM_MBUTTONDOWN: return "Middle+";
                case Native.WM_MBUTTONUP: return "Middle-";
                case Native.WM_XBUTTONDOWN: return ((data >> 16) == 1 ? "Back" : "Forward") + "+";
                case Native.WM_XBUTTONUP: return ((data >> 16) == 1 ? "Back" : "Forward") + "-";
            }
            return "msg" + msg.ToString("X");
        }

        // A hook alone on its own thread with a message loop, as Windows requires.
        static Thread HookOn(Native.HookProc keysProc, Native.HookProc mouseProc, bool isProbe)
        {
            ManualResetEvent ready = new ManualResetEvent(false);
            Thread t = new Thread(delegate()
            {
                Native.MSG m;
                Native.PeekMessage(out m, IntPtr.Zero, 0, 0, 0);
                IntPtr mouse = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, Native.GetModuleHandle(null), 0);
                IntPtr keys = keysProc == null ? IntPtr.Zero : Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keysProc, Native.GetModuleHandle(null), 0);
                if (isProbe) { probeThread = Native.GetCurrentThreadId(); probeMouseHook = mouse; probeKeysHook = keys; }
                else { backstopThread = Native.GetCurrentThreadId(); backstopHook = mouse; }
                ready.Set();
                while (Native.GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
                if (keys != IntPtr.Zero) Native.UnhookWindowsHookEx(keys);
                Native.UnhookWindowsHookEx(mouse);
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.Highest;
            t.Start();
            ready.WaitOne(3000);
            return t;
        }

        static void SendTest(uint flags, uint data, IntPtr tag)
        {
            Native.INPUT[] one = new Native.INPUT[1];
            one[0].type = Native.INPUT_MOUSE;
            one[0].u.mi.dwFlags = flags;
            one[0].u.mi.mouseData = data;
            one[0].u.mi.dwExtraInfo = tag;
            Native.SendInput(1, one, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        static int OutputsSince(int start)
        {
            int n = 0, end = Math.Min(recorded, Max);
            for (int i = start; i < end; i++) if (isOut[i]) n++;
            return n;
        }

        // Does something, waits for the replacements, then checks what came out and how long each took
        // after the test click before it.
        static void Expect(string name, int inputs, string want, ThreadStart act)
        {
            int start = recorded, leaked = leaks;
            act();
            int wantCount = want.Length == 0 ? 0 : want.Split(' ').Length;
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 2000 && OutputsSince(start) < wantCount) Thread.Sleep(1);
            Thread.Sleep(wantCount == 0 ? 150 : 40); // catch anything extra or late

            StringBuilder got = new StringBuilder();
            long lastIn = 0, firstIn = 0, lastOut = 0;
            int seenIn = 0;
            double worst = 0;
            int end = Math.Min(recorded, Max);
            for (int i = start; i < end; i++)
            {
                if (!isOut[i]) { seenIn++; lastIn = when[i]; if (firstIn == 0) firstIn = when[i]; continue; }
                got.Append(got.Length > 0 ? " " : "").Append(what[i]);
                if (lastIn != 0) worst = Math.Max(worst, (when[i] - lastIn) * 1000.0 / Stopwatch.Frequency);
                lastOut = when[i];
            }
            bool fast = worst <= MaxLatencyMs, inOk = seenIn == inputs, noLeak = leaks == leaked;
            bool ok = got.ToString() == want && fast && inOk && noLeak;
            if (!ok) TestMain.Failures++;
            worstLatency = Math.Max(worstLatency, worst);
            string timing = wantCount == 0 || firstIn == 0 ? "" : string.Format("  [slowest {0:0.000} ms; all {1} in {2:0.0} ms]", worst, wantCount, (lastOut - firstIn) * 1000.0 / Stopwatch.Frequency);
            Console.WriteLine("{0}  {1}{2}", ok ? "pass" : "FAIL", name, timing);
            if (got.ToString() != want)
            {
                string g = got.ToString(), w = want;
                if (g.Length > 120) g = g.Substring(0, 120) + "...";
                if (w.Length > 120) w = w.Substring(0, 120) + "...";
                Console.WriteLine("      wanted [{0}]\n      got    [{1}]", w, g);
            }
            if (!fast) Console.WriteLine("      too slow: over {0} ms", MaxLatencyMs);
            if (!inOk) Console.WriteLine("      the probe saw {0} of {1} test clicks", seenIn, inputs);
            if (!noLeak) Console.WriteLine("      {0} test click(s) got past the program's hook (caught by the backstop)", leaks - leaked);
        }

        static void Clicks(string name, uint[] flags, uint[] data, string want)
        {
            Expect(name, flags.Length, want, delegate { for (int i = 0; i < flags.Length; i++) SendTest(flags[i], data[i], HookThread.TestTag); });
        }

        internal static int Run()
        {
            string conflict = Conflicts.Running();
            bool first;
            using (Mutex running = new Mutex(false, @"Local\" + App.Name, out first))
            {
                if (!first) conflict = App.Name;
            }
            if (conflict != null)
            {
                Console.WriteLine("SKIP  live test not run: " + conflict + " is running and would see the test traffic. Quit it first. No test clicks were sent.");
                return 2;
            }

            Remapper remapper = new Remapper();
            remapper.Config = Unit.Parse("[Buttons]\nMiddle = Right\nRight = Double\nForward = None\nBack = Keys F24\n");
            Injector injector = new Injector();
            injector.Start();
            Thread back = HookOn(null, backstop, false);
            if (backstopHook == IntPtr.Zero) { Console.WriteLine("FAIL  the backstop hook did not install"); return 1; }
            HookThread hook = new HookThread(remapper, injector);
            hook.LiveTest = true;
            WholeNameSelector selector = new WholeNameSelector(); // not started: asks are only counted
            hook.Selector = selector;
            if (!hook.Start()) { Console.WriteLine("FAIL  the program's hook did not install"); return 1; }
            Thread probe = HookOn(probeKeys, probeMouse, true);
            if (probeMouseHook == IntPtr.Zero || probeKeysHook == IntPtr.Zero) { Console.WriteLine("FAIL  the probe hooks did not install"); return 1; }

            // A sideways scroll of 1/120 of a notch, harmless even if something were wrong: it must
            // reach the probe, or no test click is sent. (Windows drops a zero-length scroll before any
            // hook sees it, so it must be 1.)
            int rawBefore = hook.RawReportsSeen;
            SendTest(Native.MOUSEEVENTF_HWHEEL, 1, CanaryTag);
            Stopwatch w = Stopwatch.StartNew();
            while (!canarySeen && w.ElapsedMilliseconds < 1500) Thread.Sleep(1);
            if (!canarySeen)
            {
                Console.WriteLine("SKIP  live test not run: no input reached the hooks (an admin window may be in front). No test clicks were sent.");
                return 2;
            }
            Check("the probe hears test input before anything else", true);
            while (hook.RawReportsSeen == rawBefore && w.ElapsedMilliseconds < 1500) Thread.Sleep(5);
            Check("Raw Input reaches the hook thread (input from a program arrives with no device named)", hook.RawReportsSeen > rawBefore);

            Clicks("middle finger (Middle) sends a right click", new uint[] { Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP }, new uint[] { 0, 0 }, "Right+ Right-");
            Clicks("bottom finger (Right) sends a double click", new uint[] { Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP }, new uint[] { 0, 0 }, "Left+ Left- Left+ Left-");
            Clicks("bottom finger held: the double click's second press stays down (drag selects words)", new uint[] { Native.MOUSEEVENTF_RIGHTDOWN }, new uint[] { 0 }, "Left+ Left- Left+");
            int asks = selector.Asks;
            Clicks("bottom finger lifted: the held press comes up", new uint[] { Native.MOUSEEVENTF_RIGHTUP }, new uint[] { 0 }, "Left-");
            Check("bottom finger lifted: asks once for the whole-name selection", selector.Asks == asks + 1);
            Clicks("wheel click (Forward) set to None sends nothing", new uint[] { Native.MOUSEEVENTF_XDOWN, Native.MOUSEEVENTF_XUP }, new uint[] { 2, 2 }, "");
            Clicks("upper thumb (Back) set to Keys F24 sends the key", new uint[] { Native.MOUSEEVENTF_XDOWN, Native.MOUSEEVENTF_XUP }, new uint[] { 1, 1 }, "F24+ F24-");

            const int rounds = 50;
            uint[] bf = new uint[rounds * 4], bd = new uint[rounds * 4];
            StringBuilder bw = new StringBuilder();
            for (int r = 0; r < rounds; r++)
            {
                bf[r * 4] = Native.MOUSEEVENTF_MIDDLEDOWN; bf[r * 4 + 1] = Native.MOUSEEVENTF_MIDDLEUP;
                bf[r * 4 + 2] = Native.MOUSEEVENTF_RIGHTDOWN; bf[r * 4 + 3] = Native.MOUSEEVENTF_RIGHTUP;
                bw.Append(bw.Length > 0 ? " " : "").Append("Right+ Right- Left+ Left- Left+ Left-");
            }
            asks = selector.Asks;
            Clicks("rapid pressing, 50 rounds of middle + bottom: every click arrives, in order", bf, bd, bw.ToString());
            Check("rapid pressing: one whole-name ask per bottom click (" + (selector.Asks - asks) + ")", selector.Asks - asks == rounds);

            // A crash while a button is held: the crash handler lets go of it, and the finger's own
            // release afterwards sends nothing more.
            Clicks("middle finger held down", new uint[] { Native.MOUSEEVENTF_MIDDLEDOWN }, new uint[] { 0 }, "Right+");
            Expect("a crash now lets go of the held right button", 0, "Right-", delegate
            {
                List<Out> outs = new List<Out>();
                remapper.ReleaseAll(outs); // what Program.OnCrash does
                Injector.Send(outs);
            });
            Clicks("  ...and the finger's release afterwards sends nothing", new uint[] { Native.MOUSEEVENTF_MIDDLEUP }, new uint[] { 0 }, "");

            Console.WriteLine("info  slowest hook call: {0:0.000} ms (Windows waits on this for every mouse event)", hook.MaxHookTicks * 1000.0 / Stopwatch.Frequency);
            Console.WriteLine("info  slowest replacement: {0:0.000} ms after the click it replaces", worstLatency);

            Native.PostThreadMessage(probeThread, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            hook.Quit(1000);
            Native.PostThreadMessage(backstopThread, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            probe.Join(1000);
            back.Join(1000);
            return TestMain.Done();
        }
    }
}
