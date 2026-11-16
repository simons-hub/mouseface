using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MouseFace
{
    // Messages the hook thread posts to the UI thread's window. Posting never waits.
    internal static class UiMsg
    {
        internal const int Dim = 0x8003, Presence = 0x8006, Stop = 0x8007, Note = 0x8008, Button = 0x8009,
            Place = 0x800A, Settings = 0x800B, Quit = 0x800C, Display = 0x800D, Hide = 0x800E;
    }

    // The mouse hook, alone on its own thread. Windows waits on it for every mouse event, so it only
    // decides and returns: it never sends input, writes to disk, draws or waits. The same thread reads
    // Raw Input, which names the device behind each click and says when mice come and go.
    internal sealed class HookThread
    {
        internal const uint WM_APP_RELEASE = 0x8001, WM_APP_QUIT = 0x8002, WM_APP_REHOOK = 0x8004, WM_APP_RECONFIG = 0x8005;
        internal const int StopStuck = 1, StopHookDropped = 2;
        // The live test's clicks, which count as clicks from the chosen mouse.
        internal static readonly IntPtr TestTag = new IntPtr(0x4D465254);

        readonly Native.HookProc proc;
        readonly Remapper remapper;
        readonly Injector injector;
        readonly Attribution attribution = new Attribution();
        readonly HookWatch watch = new HookWatch();
        readonly RehookBudget budget = new RehookBudget();
        readonly Dictionary<IntPtr, bool> chosen = new Dictionary<IntPtr, bool>();
        readonly List<Out> pending = new List<Out>(16);
        readonly IntPtr rawBuffer = Marshal.AllocHGlobal(16384);
        readonly IntPtr oneRaw = Marshal.AllocHGlobal(256);
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        RawSink sink;
        IntPtr hook;
        int threadId;
        Thread thread;
        int errors, unlabelled;
        int deciding = -1; // the button being decided, for the error path; -1 when none

        internal volatile bool Paused, Failed, Present, ReportButtons;
        internal volatile int LastHookTick = Environment.TickCount;
        internal bool LiveTest = false;      // set only by the live test

        internal IntPtr Ui;                  // the UI thread's message window; set before Start
        internal WholeNameSelector Selector; // null when whole-name selection is off
        internal Action<string> Watch;       // --watch: one line per decision; must not block
        internal long MaxHookTicks;
        internal int RawReportsSeen;         // for the tests
        internal readonly ConcurrentQueue<string> Notes = new ConcurrentQueue<string>();

        internal HookThread(Remapper remapper, Injector injector)
        {
            this.remapper = remapper;
            this.injector = injector;
            proc = OnMouse; // a field keeps the delegate alive for as long as Windows may call it
        }

        internal bool Hooked { get { return hook != IntPtr.Zero; } }
        internal IntPtr HookHandle { get { return hook; } }
        internal bool HearsMouse { get { return sink != null && sink.HearsMouse; } }
        internal int ThreadId { get { return threadId; } }

        internal bool Start()
        {
            WarmUp();
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.Highest;
            thread.Name = "hook";
            thread.Start();
            return ready.WaitOne(3000) && hook != IntPtr.Zero;
        }

        internal void Post(uint msg) { if (threadId != 0) Native.PostThreadMessage(threadId, msg, IntPtr.Zero, IntPtr.Zero); }

        // Lets go of anything held, unhooks and ends the thread.
        internal bool Quit(int waitMs)
        {
            Post(WM_APP_QUIT);
            return thread == null || thread.Join(waitMs);
        }

        void Run()
        {
            threadId = Native.GetCurrentThreadId();
            Native.MSG m;
            Native.PeekMessage(out m, IntPtr.Zero, 0, 0, 0); // create the message queue before anyone posts to it
            try { sink = new RawSink(this); }
            catch (Exception ex) { Note("Raw Input did not start, so clicks cannot be told apart by device: " + ex.Message); }
            RefreshPresence();
            hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
            ready.Set();
            if (hook == IntPtr.Zero) return;
            // Windows calls OnMouse from inside GetMessage; the loop itself only waits.
            while (Native.GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
            {
                if (m.hwnd != IntPtr.Zero) { Native.DispatchMessage(ref m); continue; }
                if (m.message == WM_APP_REHOOK) Rehook();
                else if (m.message == WM_APP_RECONFIG) { chosen.Clear(); RefreshPresence(); }
                else if (m.message == WM_APP_RELEASE) ReleaseHeld();
                else if (m.message == WM_APP_QUIT) { ReleaseHeld(); break; }
            }
            Native.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
            if (sink != null) sink.DestroyHandle();
        }

        // ---- the hook ---------------------------------------------------------------------------

        IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            LastHookTick = Environment.TickCount;
            if (nCode >= 0 && !Failed)
            {
                int msg = wParam.ToInt32();
                if (msg != Native.WM_MOUSEMOVE) // plain movement: nothing to decide
                {
                    long started = Stopwatch.GetTimestamp();
                    bool swallow = false;
                    try { swallow = Handle(msg, lParam); }
                    catch (Exception ex)
                    {
                        // A bug here must never take the mouse down: let go of anything held, let this
                        // event through unchanged (and later its up), and keep going.
                        try { ReleaseHeld(); if (deciding >= 0) remapper.MarkPassed((Btn)deciding); } catch (Exception) { }
                        NoteError(ex);
                        swallow = false;
                    }
                    deciding = -1;
                    long took = Stopwatch.GetTimestamp() - started;
                    if (took > MaxHookTicks) MaxHookTicks = took;
                    if (swallow) return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        bool Handle(int msg, IntPtr lParam)
        {
            uint data = (uint)Marshal.ReadInt32(lParam, 8);
            bool injected = (Marshal.ReadInt32(lParam, 12) & Native.LLMHF_INJECTED) != 0;
            IntPtr extra = Marshal.ReadIntPtr(lParam, Native.MouseExtraOffset);
            bool test = LiveTest && injected && extra == TestTag;
            // Input that programs send (this one's own, remote desktop, a precision touchpad that
            // Windows drives) passes untouched. The live test turns that round: only its tagged
            // clicks count, and the real mouse is left alone.
            if (LiveTest ? !test : injected) return false;
            if (injector.Stuck()) { Fail(StopStuck); return false; }
            if (msg == Native.WM_MOUSEWHEEL) return !LiveTest && OnWheel(data);

            Btn b; bool down;
            if (!Buttons.Classify(msg, data, out b, out down)) return false;
            IntPtr dev = IntPtr.Zero;
            bool fromChosen = test;
            if (!test)
            {
                bool labelled = Identify(Buttons.RawFlag(Buttons.Physical(b, Buttons.Swapped()), down), false, out dev);
                fromChosen = labelled && IsChosen(dev);
                if (!labelled && down && Present && unlabelled++ < 5)
                    Note("a " + b + " press came with no device named (a precision touchpad does this); passed it through unchanged");
            }

            pending.Clear();
            bool wholeName;
            deciding = down ? (int)b : -1;
            bool swallow = remapper.Decide(b, down, fromChosen, Paused, pending, out wholeName);
            if (pending.Count > 0) injector.Enqueue(pending);
            if (wholeName && Selector != null) Selector.Ask(Marshal.ReadInt32(lParam, 0), Marshal.ReadInt32(lParam, 4)); // MSLLHOOKSTRUCT.pt
            if (ReportButtons && Ui != IntPtr.Zero)
                Native.PostMessage(Ui, UiMsg.Button, new IntPtr((int)b | (down ? 0x10 : 0) | (fromChosen ? 0x20 : 0)), dev);
            Action<string> w = Watch;
            if (w != null)
                w(string.Format("{0,-7} {1,-4} {2,-16} ->  {3}", b, down ? "down" : "up", fromChosen ? "(chosen mouse)" : "(other device)",
                    swallow ? (pending.Count > 0 ? Out.Describe(pending) : "nothing") : "passed through"));
            return swallow;
        }

        // Shift + wheel (or the chosen chord) on the chosen mouse dims the screens.
        bool OnWheel(uint data)
        {
            Config c = remapper.Config;
            if (Paused || c.Dim == DimKey.Off || !Present || Ui == IntPtr.Zero) return false;
            if (!Config.IsDimChord(c.Dim, Native.KeyDown(Native.VK_SHIFT), Native.KeyDown(Native.VK_CONTROL),
                Native.KeyDown(Native.VK_MENU), Native.KeyDown(Native.VK_LWIN) || Native.KeyDown(Native.VK_RWIN))) return false;
            IntPtr dev;
            if (!Identify(Buttons.RawWheel, true, out dev) || !IsChosen(dev)) return false;
            Native.PostMessage(Ui, UiMsg.Dim, new IntPtr((int)(short)(data >> 16)), IntPtr.Zero);
            return true;
        }

        bool Identify(ushort flag, bool newest, out IntPtr dev)
        {
            Drain(); // reports already queued but not yet read
            return attribution.Claim(flag, newest, Environment.TickCount, out dev);
        }

        // Whether a device is the mouse being remapped. Remembered per device; forgotten when devices
        // come and go or the settings change.
        bool IsChosen(IntPtr dev)
        {
            if (dev == IntPtr.Zero) return false;
            bool yes;
            if (chosen.TryGetValue(dev, out yes)) return yes;
            string path = Devices.PathOf(dev);
            if (path == null) return false; // already gone
            yes = remapper.Config.Match.Matches(DevicePath.Parse(path));
            chosen[dev] = yes;
            return yes;
        }

        internal void ReleaseHeld()
        {
            pending.Clear();
            remapper.ReleaseAll(pending);
            if (pending.Count > 0) injector.Enqueue(pending);
        }

        void Rehook()
        {
            // The new hook goes in before the old one comes out, so no click slips through.
            IntPtr fresh = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
            if (fresh == IntPtr.Zero) return;
            Native.UnhookWindowsHookEx(hook);
            hook = fresh;
            LastHookTick = Environment.TickCount;
        }

        void Fail(int reason)
        {
            if (Failed) return;
            Failed = true; // from here on the hook lets everything through
            try { ReleaseHeld(); } catch (Exception) { }
            if (Ui != IntPtr.Zero) Native.PostMessage(Ui, UiMsg.Stop, new IntPtr(reason), IntPtr.Zero);
        }

        void Note(string text)
        {
            Notes.Enqueue(text);
            if (Ui != IntPtr.Zero) Native.PostMessage(Ui, UiMsg.Note, IntPtr.Zero, IntPtr.Zero);
        }

        void NoteError(Exception ex)
        {
            if (++errors <= 5) Note("error, click passed through unchanged: " + ex.GetType().Name + ": " + ex.Message);
        }

        // ---- Raw Input --------------------------------------------------------------------------

        void OnRawMessage(IntPtr handle)
        {
            uint header = (uint)Native.RawHeaderSize, size = 256;
            if (Native.GetRawInputData(handle, Native.RID_INPUT, oneRaw, ref size, header) == uint.MaxValue) return;
            if (Marshal.ReadInt32(oneRaw) != Native.RIM_TYPEMOUSE) return;
            int o = (int)header;
            OnRaw(Marshal.ReadIntPtr(oneRaw, 8), (ushort)Marshal.ReadInt16(oneRaw, o + 4), Marshal.ReadInt32(oneRaw, o + 12), Marshal.ReadInt32(oneRaw, o + 16));
        }

        // Reads every report already waiting, without waiting for any.
        void Drain()
        {
            int header = Native.RawHeaderSize, align = IntPtr.Size;
            while (true)
            {
                uint size = 16384;
                uint n = Native.GetRawInputBuffer(rawBuffer, ref size, (uint)header);
                if (n == 0 || n == uint.MaxValue) return;
                IntPtr p = rawBuffer;
                for (uint i = 0; i < n; i++)
                {
                    int type = Marshal.ReadInt32(p), sz = Marshal.ReadInt32(p, 4);
                    if (type == Native.RIM_TYPEMOUSE)
                        OnRaw(Marshal.ReadIntPtr(p, 8), (ushort)Marshal.ReadInt16(p, header + 4), Marshal.ReadInt32(p, header + 12), Marshal.ReadInt32(p, header + 16));
                    p = new IntPtr((p.ToInt64() + sz + align - 1) & ~(long)(align - 1));
                }
            }
        }

        void OnRaw(IntPtr dev, ushort flags, int dx, int dy)
        {
            int now = Environment.TickCount;
            RawReportsSeen++;
            attribution.OnReport(dev, flags, now);
            if (Failed || LiveTest || dev == IntPtr.Zero || (flags == 0 && dx == 0 && dy == 0)) return;
            if (!IsChosen(dev)) return;
            if (injector.Stuck()) { Fail(StopStuck); return; }
            if (watch.OnChosenMouseUse(now, LastHookTick)) OnHookSilent(now);
        }

        void OnHookSilent(int now)
        {
            // An admin window in front hides the mouse from a hook without admin rights (Windows'
            // UIPI), and a UAC prompt or the lock screen takes input to another desktop. Silence then
            // is expected.
            if (!Desk.InputOnThisDesktop() || Desk.ForegroundOutranksUs() || Desk.PointerWindowOutranksUs()) return;
            if (budget.Allow(now))
            {
                ReleaseHeld(); // ups that came while the hook was gone were never seen
                Rehook();
                Note("the mouse hook looked dropped (the chosen mouse was in use, the hook heard nothing); re-attached it");
            }
            else Fail(StopHookDropped);
        }

        void OnDeviceChange(int change, IntPtr dev)
        {
            bool was;
            if (change == Native.GIDC_REMOVAL && chosen.TryGetValue(dev, out was))
            {
                chosen.Remove(dev);
                if (was) ReleaseHeld(); // its button-ups will never come, whatever else is still attached
            }
            RefreshPresence();
        }

        // Is the chosen mouse attached? Asked when Windows says a mouse came or went, never on a timer.
        void RefreshPresence()
        {
            List<MouseDevice> mice = Devices.Mice();
            if (mice == null) return; // could not read the list; keep the last answer
            bool any = false;
            foreach (MouseDevice d in mice) if (IsChosen(d.Handle)) any = true;
            if (any == Present) return;
            Present = any;
            if (!any) ReleaseHeld(); // its button-ups will never come
            if (Ui != IntPtr.Zero) Native.PostMessage(Ui, UiMsg.Presence, new IntPtr(any ? 1 : 0), IntPtr.Zero);
        }

        // .NET compiles each method on its first call. Do that for the hook's whole path before the
        // hook goes in, so the first real click does not wait for the compiler.
        static void WarmUp()
        {
            foreach (Type t in new Type[] { typeof(HookThread), typeof(Remapper), typeof(Attribution), typeof(HookWatch),
                typeof(Injector), typeof(Buttons), typeof(Config), typeof(Out), typeof(MatchRule), typeof(DevicePath), typeof(Native) })
            {
                foreach (MethodInfo mi in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (mi.IsAbstract || mi.ContainsGenericParameters || (mi.Attributes & MethodAttributes.PinvokeImpl) != 0) continue;
                    try { RuntimeHelpers.PrepareMethod(mi.MethodHandle); } catch (Exception) { }
                }
            }
            // Generic code for these types is compiled on first use too, so use it once.
            List<Out> a = new List<Out>(4), b = new List<Out>(4);
            a.Add(Out.Mouse(Btn.Left, true));
            b.AddRange(a);
            b.Clear();
            bool unused;
            new Dictionary<IntPtr, bool>().TryGetValue(IntPtr.Zero, out unused);
        }

        sealed class RawSink : NativeWindow
        {
            readonly HookThread owner;
            internal readonly bool HearsMouse;

            internal RawSink(HookThread owner)
            {
                this.owner = owner;
                CreateParams cp = new CreateParams();
                cp.Parent = Native.HWND_MESSAGE;
                CreateHandle(cp);
                Native.RAWINPUTDEVICE[] d = new Native.RAWINPUTDEVICE[1];
                d[0].usUsagePage = 1; d[0].usUsage = 2; // generic desktop: mouse
                d[0].dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY; // in the background too, plus arrivals and removals
                d[0].hwndTarget = Handle;
                HearsMouse = Native.RegisterRawInputDevices(d, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE)));
            }

            protected override void WndProc(ref Message m)
            {
                try
                {
                    if (m.Msg == Native.WM_INPUT) owner.OnRawMessage(m.LParam);
                    else if (m.Msg == Native.WM_INPUT_DEVICE_CHANGE) owner.OnDeviceChange(m.WParam.ToInt32(), m.LParam);
                }
                catch (Exception ex) { owner.NoteError(ex); }
                base.WndProc(ref m); // Windows still wants WM_INPUT passed on
            }
        }
    }

    // Questions about the desktop that decide whether silence from the hook is expected.
    internal static class Desk
    {
        static int ownIntegrity = -2;

        // The hook only hears input on this desktop; a UAC prompt or the lock screen uses another.
        internal static bool InputOnThisDesktop()
        {
            IntPtr d = Native.OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
            if (d == IntPtr.Zero) return false;
            try
            {
                StringBuilder name = new StringBuilder(64);
                uint needed;
                return Native.GetUserObjectInformation(d, 2, name, (uint)(name.Capacity * 2), out needed) && name.ToString() == "Default";
            }
            finally { Native.CloseDesktop(d); }
        }

        // True when the window in front belongs to a higher-integrity (admin) process, or to one this
        // process may not even inspect, which in practice means the same.
        internal static bool ForegroundOutranksUs()
        {
            if (ownIntegrity == -2) ownIntegrity = IntegrityOf(Native.GetCurrentProcess());
            uint pid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            if (pid == 0) return false;
            IntPtr p = Native.OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (p == IntPtr.Zero) return true;
            try { int fg = IntegrityOf(p); return fg < 0 || fg > ownIntegrity; }
            finally { Native.CloseHandle(p); }
        }

        // A process's integrity level (0x2000 medium, 0x3000 high/admin), or -1 if unreadable.
        internal static int IntegrityOf(IntPtr process)
        {
            IntPtr token;
            if (!Native.OpenProcessToken(process, 0x0008, out token)) return -1; // TOKEN_QUERY
            try
            {
                int needed;
                Native.GetTokenInformation(token, 25, IntPtr.Zero, 0, out needed); // TokenIntegrityLevel
                if (needed <= 0) return -1;
                IntPtr buf = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!Native.GetTokenInformation(token, 25, buf, needed, out needed)) return -1;
                    IntPtr sid = Marshal.ReadIntPtr(buf); // TOKEN_MANDATORY_LABEL.Label.Sid
                    int count = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
                    return Marshal.ReadInt32(Native.GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { Native.CloseHandle(token); }
        }

        // The same for the window under the pointer: input aimed at an admin window is hidden too.
        internal static bool PointerWindowOutranksUs()
        {
            Native.POINT pt;
            if (!Native.GetCursorPos(out pt)) return false;
            uint pid;
            Native.GetWindowThreadProcessId(Native.WindowFromPoint(pt), out pid);
            if (pid == 0) return false;
            if (ownIntegrity == -2) ownIntegrity = IntegrityOf(Native.GetCurrentProcess());
            IntPtr p = Native.OpenProcess(0x1000, false, pid);
            if (p == IntPtr.Zero) return true;
            try { int w = IntegrityOf(p); return w < 0 || w > ownIntegrity; }
            finally { Native.CloseHandle(p); }
        }

        // The name of the program in front, for log lines.
        internal static string ForegroundProgram()
        {
            try
            {
                uint pid;
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
                return pid == 0 ? "?" : Process.GetProcessById((int)pid).ProcessName;
            }
            catch (Exception) { return "?"; }
        }
    }
}
