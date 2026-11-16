using System;
using System.Collections.Generic;

namespace MouseFace
{
    // Decides what each button press becomes. Pure logic: no Windows calls, so the unit tests drive it
    // directly. Used only on the hook thread, except ReleaseAll in an emergency (see Program.OnCrash).
    internal sealed class Remapper
    {
        enum Held { Nothing, Passed, Remapped, Released }

        // What each button's last DOWN became, so its UP is handled the same way even if the mouse,
        // the pause or the settings changed in between.
        readonly Held[] state = new Held[5];
        readonly Act[] heldAct = new Act[5];
        bool clickLocked;
        volatile Config config = new Config();

        internal Config Config { get { return config; } set { config = value; } }
        internal bool ClickLocked { get { return clickLocked; } }

        // One button event. fromChosen: the click came from the mouse being remapped. Returns true to
        // swallow the event, and appends what to send instead. wholeName: a Double just lifted, so the
        // selection should grow to the whole name.
        internal bool Decide(Btn b, bool down, bool fromChosen, bool paused, List<Out> send, out bool wholeName)
        {
            wholeName = false;
            int p = (int)b;
            if (down)
            {
                // A down while this button is still held (its up was missed, or another device pressed
                // the same button): let the held one go first, so nothing stays down.
                if (state[p] == Held.Remapped) Release(p, send);
                Act a = config.For(b);
                if (paused || !fromChosen || a.IsPlain(b))
                {
                    state[p] = Held.Passed;
                    return false;
                }
                state[p] = Held.Remapped;
                heldAct[p] = a;
                switch (a.Kind)
                {
                    case ActKind.Button:
                        send.Add(Out.Mouse(a.Button, true));
                        break;
                    case ActKind.Double:
                        // Down, up, down now; the last up waits for the finger. Holding the button and
                        // moving is then a double-click drag, which selects word by word.
                        send.Add(Out.Mouse(Btn.Left, true)); send.Add(Out.Mouse(Btn.Left, false));
                        send.Add(Out.Mouse(Btn.Left, true));
                        clickLocked = false;
                        break;
                    case ActKind.ClickLock:
                        send.Add(Out.Mouse(Btn.Left, !clickLocked));
                        clickLocked = !clickLocked;
                        break;
                    case ActKind.Keys:
                        foreach (ushort vk in a.Keys) send.Add(Out.Key(vk, true));
                        break;
                }
                return true;
            }

            Held s = state[p];
            Act h = heldAct[p];
            state[p] = Held.Nothing;
            heldAct[p] = null;
            if (s == Held.Released) return true; // its up was already sent; this one is a leftover
            if (s != Held.Remapped)
            {
                if (b == Btn.Left) clickLocked = false; // a real left button coming up ends any lock
                return false;
            }
            switch (h.Kind)
            {
                case ActKind.Button:
                    send.Add(Out.Mouse(h.Button, false));
                    if (h.Button == Btn.Left) clickLocked = false;
                    break;
                case ActKind.Double:
                    send.Add(Out.Mouse(Btn.Left, false));
                    wholeName = config.WholeName;
                    break;
                case ActKind.Keys:
                    for (int i = h.Keys.Length - 1; i >= 0; i--) send.Add(Out.Key(h.Keys[i], false));
                    break;
            }
            return true;
        }

        // Lets go of everything this remapper is holding down: on a pause, when the mouse goes away
        // mid-press, and on the way out. The finger's own up, if it still comes, is then swallowed.
        internal void ReleaseAll(List<Out> send)
        {
            for (int p = 0; p < 5; p++)
            {
                if (state[p] == Held.Remapped) { Release(p, send); state[p] = Held.Released; }
                else if (state[p] != Held.Released) state[p] = Held.Nothing;
                heldAct[p] = null;
            }
            if (clickLocked) { send.Add(Out.Mouse(Btn.Left, false)); clickLocked = false; }
        }

        void Release(int p, List<Out> send)
        {
            Act h = heldAct[p];
            state[p] = Held.Nothing;
            heldAct[p] = null;
            if (h == null) return;
            if (h.Kind == ActKind.Button) send.Add(Out.Mouse(h.Button, false));
            else if (h.Kind == ActKind.Double) send.Add(Out.Mouse(Btn.Left, false));
            else if (h.Kind == ActKind.Keys) for (int i = h.Keys.Length - 1; i >= 0; i--) send.Add(Out.Key(h.Keys[i], false));
        }

        // After an error mid-decision the original event passes through, so its up must pass too.
        internal void MarkPassed(Btn b)
        {
            state[(int)b] = Held.Passed;
            heldAct[(int)b] = null;
        }
    }

    // Which device a click came from. Windows names the device in a Raw Input report, which (measured
    // on real hardware, 68 of 68 presses and releases) lands before the low-level hook has to decide.
    // Each report is kept briefly per button flag, and each click claims the oldest one for its flag.
    internal sealed class Attribution
    {
        const int Flags = 12, Depth = 8;
        internal const int MaxAgeMs = 200;
        readonly IntPtr[,] device = new IntPtr[Flags, Depth];
        readonly int[,] tick = new int[Flags, Depth];
        readonly int[] start = new int[Flags], count = new int[Flags];

        // Reports with no device (dev 0) come from programs, this one's own replacement clicks
        // included, or from a precision touchpad. They are not kept, so a click can never claim one.
        internal void OnReport(IntPtr dev, ushort buttonFlags, int now)
        {
            if (dev == IntPtr.Zero) return;
            for (int f = 0; f < Flags; f++)
            {
                if ((buttonFlags & (1 << f)) == 0) continue;
                if (count[f] == Depth) { start[f] = (start[f] + 1) % Depth; count[f]--; } // drop the oldest
                int slot = (start[f] + count[f]) % Depth;
                device[f, slot] = dev;
                tick[f, slot] = now;
                count[f]++;
            }
        }

        // Finds the report for one click: the oldest unclaimed one with this flag, or for the wheel the
        // newest. False if there is none, which is what a precision touchpad's click looks like; the
        // click then counts as not from the chosen mouse, so a touchpad is never remapped by mistake.
        internal bool Claim(ushort flag, bool newest, int now, out IntPtr dev)
        {
            dev = IntPtr.Zero;
            int f = 0;
            while (f < Flags && (flag & (1 << f)) == 0) f++;
            if (f == Flags) return false;
            while (count[f] > 0 && unchecked(now - tick[f, start[f]]) > MaxAgeMs) { start[f] = (start[f] + 1) % Depth; count[f]--; }
            if (count[f] == 0) return false;
            int slot = newest ? (start[f] + count[f] - 1) % Depth : start[f];
            dev = device[f, slot];
            if (newest) count[f] = 0;
            else { start[f] = (start[f] + 1) % Depth; count[f]--; }
            return true;
        }
    }

    // Windows silently drops a low-level hook whose callback is ever too slow. The tell: the chosen
    // mouse is in use (its Raw Input reports keep coming) while the hook hears nothing at all. Only
    // reports from the chosen mouse count: input that programs send, a touchpad or another mouse
    // never do, so another program swallowing input in front of the hook cannot fool it.
    internal sealed class HookWatch
    {
        internal const int SilentMs = 3000, SustainMs = 2000;
        bool silent;
        int silentSince;

        // Called for each report from the chosen mouse. True once the silence has lasted.
        internal bool OnChosenMouseUse(int now, int lastHookTick)
        {
            if (unchecked(now - lastHookTick) <= SilentMs) { silent = false; return false; }
            if (!silent) { silent = true; silentSince = now; return false; }
            if (unchecked(now - silentSince) < SustainMs) return false;
            silent = false;
            return true;
        }
    }

    // A dropped hook is re-attached, but only a few times: if it keeps going, a plain mouse is the
    // safe answer. Nothing ever restarts the program itself.
    internal sealed class RehookBudget
    {
        internal const int Limit = 3, WindowMs = 10 * 60 * 1000;
        readonly List<int> ticks = new List<int>();

        internal bool Allow(int now)
        {
            ticks.RemoveAll(delegate(int t) { return unchecked(now - t) > WindowMs; });
            if (ticks.Count >= Limit) return false;
            ticks.Add(now);
            return true;
        }
    }
}
