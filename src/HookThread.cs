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
        readonly object startGate = new object();
        RawSink sink;
        IntPtr hook;
        int threadId;
        Thread thread;
        bool started, abandoned; // under startGate
        int errors, unlabelled, lateNotes, settledNotes;
        int deciding = -1; // the button being decided, for the error path; -1 when none
        int keysCheckedAt;
        bool keysBlocked;
        IntPtr foregroundHook;
        readonly Native.WinEventProc onForeground;
        // A press that took longer than this to decide is let through unchanged (see Handle).
        internal const int LateMs = 200;

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
            onForeground = OnForeground;
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
            ready.WaitOne(10000); // a busy sign-in can be slow
            lock (startGate)
            {
                // Too late, the program is about to say it isn't running: the hook must not go in
                // after all and remap behind that message.
                if (!started) abandoned = true;
                return started && hook != IntPtr.Zero;
            }
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
            lock (startGate)
            {
                if (!abandoned) hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
                started = true;
            }
            ready.Set();
            if (hook == IntPtr.Zero) { if (sink != null) sink.DestroyHandle(); return; }
            // Windows calls OnMouse from inside GetMessage; the loop itself only waits.
            while (Native.GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
            {
                if (m.hwnd != IntPtr.Zero) { Native.DispatchMessage(ref m); continue; }
                if (m.message == WM_APP_REHOOK) Rehook();
                else if (m.message == WM_APP_RECONFIG) { chosen.Clear(); RefreshPresence(); }
                else if (m.message == WM_APP_RELEASE) ReleaseHeld(false);
                else if (m.message == WM_APP_QUIT) { ReleaseHeld(true); break; }
            }
            WatchForeground(false);
            Native.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
            if (sink != null) sink.DestroyHandle();
        }

        // ---- the hook ---------------------------------------------------------------------------

        IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            LastHookTick = Environment.TickCount;
            bool swallow = false;
            if (nCode >= 0 && !Failed)
            {
                int msg = wParam.ToInt32();
                if (msg != Native.WM_MOUSEMOVE) // plain movement: nothing to decide
                {
                    long begun = Stopwatch.GetTimestamp();
                    try { swallow = Handle(msg, lParam, begun); }
                    catch (Exception ex)
                    {
                        // A bug here must never take the mouse down: let go of anything held, let this
                        // event through unchanged (and later its up), and keep going.
                        try { ReleaseHeld(false); if (deciding >= 0) remapper.MarkPassed((Btn)deciding); } catch (Exception) { }
                        NoteError(ex);
                        swallow = false;
                    }
                    deciding = -1;
                    long took = Stopwatch.GetTimestamp() - begun;
                    if (took > MaxHookTicks) MaxHookTicks = took;
                }
                // The hook hears input again, so what presses that ended unheard still hold can go now.
                if (remapper.Owes) { try { Settle(); } catch (Exception ex) { NoteError(ex); } }
            }
            return swallow ? new IntPtr(1) : Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        void Settle()
        {
            pending.Clear();
            remapper.Settle(pending, KeysBlocked(false));
            if (pending.Count > 0)
            {
                injector.Enqueue(pending);
                if (settledNotes++ < 5)
                    Note("a button was let go where the hook could not hear it (an admin window had it); let go of " + Out.Describe(pending));
            }
            WatchForeground(remapper.OwesKeys);
        }

        // Held keys only, whenever no admin window is in front (Remapper.SettleKeys); early: a new window
        // came to the front, so also those whose release is in the latest report.
        void SettleKeys(bool early, bool fresh)
        {
            pending.Clear();
            bool unheard = remapper.SettleKeys(pending, KeysBlocked(fresh), early);
            if (pending.Count > 0)
            {
                injector.Enqueue(pending);
                if (unheard && settledNotes++ < 5)
                    Note("held keys were let go where the hook could not hear their button (an admin window had it): " + Out.Describe(pending));
            }
            WatchForeground(remapper.OwesKeys);
        }

        // Whether key releases would be dropped now because an admin window is in front. Asked only
        // while keys are held or owed. A "blocked" answer is kept for 100 ms, as a stale one only delays
        // a release; "not blocked" is asked afresh every time, as a stale one could send a release just
        // as an admin window came to the front, where Windows drops it and the key stays down.
        bool KeysBlocked(bool fresh)
        {
            if (!remapper.HoldsAnyKeys) return keysBlocked = false;
            int now = Environment.TickCount;
            if (fresh || !keysBlocked || unchecked(now - keysCheckedAt) >= 100) { keysBlocked = Desk.ForegroundOutranksUs(); keysCheckedAt = now; }
            return keysBlocked;
        }

        // While held keys wait for an admin window to go, Windows says when another window comes to the
        // front, so they are let go then, even if the mouse doesn't move (Alt+Tab, Esc).
        void WatchForeground(bool on)
        {
            if (on && foregroundHook == IntPtr.Zero)
                foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                    onForeground, 0, 0, Native.WINEVENT_OUTOFCONTEXT); // its own windows too: Settings, a message box
            else if (!on && foregroundHook != IntPtr.Zero) { Native.UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
        }

        // Keys only: a mouse button's release goes to the window under the pointer, which may still be
        // the admin window, so those wait for an event the hook hears.
        void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try { if (!Failed) SettleKeys(true, true); }
            catch (Exception ex) { NoteError(ex); }
        }

        bool Handle(int msg, IntPtr lParam, long begun)
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
            bool swapped = Buttons.Swapped();
            if (!test)
            {
                bool labelled = Identify(Buttons.RawFlag(Buttons.Physical(b, swapped), down), out dev);
                fromChosen = labelled && IsChosen(dev);
                if (!labelled && down && Present && unlabelled++ < 5)
                    Note("a " + b + " press came with no device named (a precision touchpad does this); passed it through unchanged");
            }

            // Windows waits only so long for this hook, then passes the click on itself. A press that
            // took this long to decide (the program stalled: memory paged out, a garbage collection)
            // may have happened already, so it goes through as it is, and its release with it, rather
            // than happen twice. Only time spent here counts: other programs' hooks, before this one,
            // have their own allowance.
            long tookMs = (Stopwatch.GetTimestamp() - begun) * 1000 / Stopwatch.Frequency;
            if (down && fromChosen && tookMs > LateMs)
            {
                fromChosen = false;
                if (lateNotes++ < 5) Note("deciding a " + b + " press took " + tookMs + " ms, so Windows may have passed it on already; let it through unchanged");
            }

            // Something else let the left button go while it was locked down, so this press locks again.
            if (down && fromChosen && remapper.ClickLocked && remapper.Config.For(b).Kind == ActKind.ClickLock
                && !Native.KeyDown(Buttons.Physical(Btn.Left, swapped) == Btn.Left ? 0x01 : 0x02)) // VK_LBUTTON, VK_RBUTTON: physical buttons
                remapper.LockLost();

            // A held key let go while an admin window is in front: Windows would drop the key release,
            // so it waits (Remapper.Settle).
            if (!down && remapper.HoldsKeys(b) && Desk.ForegroundOutranksUs())
            {
                keysBlocked = true;
                keysCheckedAt = Environment.TickCount;
                remapper.Defer(b);
                WatchForeground(true);
                return true;
            }

            pending.Clear();
            bool wholeName;
            deciding = down ? (int)b : -1;
            remapper.KeysBlocked = KeysBlocked(false); // owed key releases wait while an admin window is in front
            bool swallow = remapper.Decide(b, down, fromChosen, Paused, pending, out wholeName);
            if (pending.Count > 0) injector.Enqueue(pending);
            if (wholeName && Selector != null) Selector.Ask(Marshal.ReadInt32(lParam, 0), Marshal.ReadInt32(lParam, 4)); // MSLLHOOKSTRUCT.pt
            if (ReportButtons && Ui != IntPtr.Zero)
                Native.PostMessage(Ui, UiMsg.Button, new IntPtr((int)b | (down ? 0x10 : 0) | (fromChosen ? 0x20 : 0)), dev);
            Action<string> w = Watch;
            if (w != null)
                w(string.Format("{0,-7} {1,-4} {2,-16} ->  {3}", b, down ? "down" : "up", fromChosen ? "(chosen mouse)" : "(other device)",
                    swallow ? (pending.Count > 0 ? Out.Describe(pending) : "nothing") : "passed through"));
            if (foregroundHook != IntPtr.Zero && !remapper.OwesKeys) WatchForeground(false); // nothing waits any more
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
            if (!Identify(Buttons.RawWheel, out dev) || !IsChosen(dev)) return false;
            Native.PostMessage(Ui, UiMsg.Dim, new IntPtr((int)(short)(data >> 16)), IntPtr.Zero);
            return true;
        }

        bool Identify(ushort flag, out IntPtr dev)
        {
            Drain(); // reports already queued but not yet read
            return attribution.Claim(flag, Environment.TickCount, out dev);
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

        // Lets go of everything held. final: on the way out, everything goes, whatever is in front;
        // otherwise held keys an admin window would drop wait, owed, until it has gone.
        internal void ReleaseHeld(bool final)
        {
            remapper.KeysBlocked = !final && KeysBlocked(true);
            pending.Clear();
            remapper.ReleaseAll(pending);
            if (pending.Count > 0) injector.Enqueue(pending);
            if (threadId == Native.GetCurrentThreadId()) WatchForeground(!final && remapper.OwesKeys);
        }

        bool Rehook()
        {
            // The new hook goes in before the old one comes out, so no click slips through.
            IntPtr fresh = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
            if (fresh == IntPtr.Zero) return false;
            Native.UnhookWindowsHookEx(hook);
            hook = fresh;
            LastHookTick = Environment.TickCount;
            return true;
        }

        void Fail(int reason)
        {
            if (Failed) return;
            Failed = true; // from here on the hook lets everything through
            try { ReleaseHeld(true); } catch (Exception) { }
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
            remapper.NextReport(); // the hook has heard all it will of the reports before this one
            if (Failed || LiveTest || dev == IntPtr.Zero || (flags == 0 && dx == 0 && dy == 0)) return;
            if (!IsChosen(dev)) return;
            if ((flags & Buttons.RawButtons) != 0) Track(flags);
            // A key release goes to the window in front, so a held key whose button Raw Input saw let go
            // is let go now, unless an admin window is in front; the hook's own hearing of that release,
            // if it comes, is then swallowed. Only a key that has to wait turns the window watch on.
            if (remapper.OwesKeys) SettleKeys(true, false);
            if (injector.Stuck()) { Fail(StopStuck); return; }
            int x = 0, y = 0;
            Native.POINT pt;
            if (unchecked(now - LastHookTick) > HookWatch.SilentMs && Native.GetCursorPos(out pt)) { x = pt.x; y = pt.y; }
            if (watch.OnChosenMouseUse(now, LastHookTick, x, y)) OnHookSilent(now);
        }

        // The chosen mouse's own presses and releases as Raw Input saw them, which Windows reports even
        // when an admin window keeps them from the hook (Remapper.RawUp). Releases first: one report can
        // hold both.
        void Track(ushort flags)
        {
            bool swapped = Buttons.Swapped();
            for (int i = 0; i < 5; i++)
                if ((flags & Buttons.RawFlag(Buttons.Physical((Btn)i, swapped), false)) != 0) remapper.RawUp((Btn)i);
            for (int i = 0; i < 5; i++)
                if ((flags & Buttons.RawFlag(Buttons.Physical((Btn)i, swapped), true)) != 0) remapper.RawDown((Btn)i);
        }

        void OnHookSilent(int now)
        {
            // An admin window in front hides the mouse from a hook without admin rights (Windows'
            // UIPI), and a UAC prompt or the lock screen takes input to another desktop. Silence then
            // is expected.
            if (!Desk.InputOnThisDesktop() || Desk.ForegroundOutranksUs() || Desk.PointerWindowOutranksUs()) return;
            if (budget.Allow(now))
            {
                ReleaseHeld(false); // ups that came while the hook was gone were never seen
                Note("the mouse hook looked dropped (the chosen mouse was in use, the hook heard nothing); "
                    + (Rehook() ? "re-attached it" : "re-attaching it failed"));
            }
            else Fail(StopHookDropped);
        }

        void OnDeviceChange(int change, IntPtr dev)
        {
            bool was;
            if (change == Native.GIDC_REMOVAL && chosen.TryGetValue(dev, out was))
            {
                chosen.Remove(dev);
                if (was) ReleaseHeld(false); // its button-ups will never come, whatever else is still attached
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
            if (!any) ReleaseHeld(false); // its button-ups will never come
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
            // And a press whose release goes unheard, through every step of letting it go later.
            string error;
            Remapper warm = new Remapper();
            warm.Config = Config.Parse("[Buttons]\nMiddle = Keys Ctrl\n", out error);
            warm.Decide(Btn.Middle, true, true, false, b, out unused);
            warm.RawUp(Btn.Middle); warm.NextReport(); warm.RawDown(Btn.Middle);
            unused = warm.OwesKeys && warm.Owes && warm.HoldsAnyKeys;
            warm.Settle(b, true); warm.SettleKeys(b, false, true); warm.Settle(b, false); warm.ReleaseAll(b);
            // The check a held key's release makes: the first call sets up its calls into Windows.
            try { unused = Desk.ForegroundOutranksUs(); } catch (Exception) { }
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

        // True when this process was started with "Run as administrator" (UAC's full admin token). On a
        // PC with UAC turned off there is no such token, so it is false there and MouseFace still runs.
        internal static bool ElevatedByUac()
        {
            IntPtr token;
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x0008, out token)) return false; // TOKEN_QUERY
            try
            {
                IntPtr buf = Marshal.AllocHGlobal(4);
                try
                {
                    int needed;
                    if (!Native.GetTokenInformation(token, 18, buf, 4, out needed)) return false; // TokenElevationType
                    return Marshal.ReadInt32(buf) == 2; // TokenElevationTypeFull
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
