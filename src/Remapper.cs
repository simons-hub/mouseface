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
        // Raw Input sees the chosen mouse's presses and releases even when Windows keeps them from the
        // hook because an admin window has them. One report can hold several changes, which the hook
        // then hears one by one, all before the next report. So a release in the latest report is only
        // pending; once a later report has come and the hook still hasn't heard it, it is owed. owedActs:
        // what earlier presses that ended unheard still hold, with their button, until it can be let go.
        int pending, owed;
        readonly List<KeyValuePair<int, Act>> owedActs = new List<KeyValuePair<int, Act>>();

        // Set by the hook thread while keys are owed: an admin window is in front, where Windows would
        // drop the key releases, so they wait.
        internal bool KeysBlocked;

        internal Config Config { get { return config; } set { config = value; } }
        internal bool ClickLocked { get { return clickLocked; } }

        // One button event. fromChosen: the click came from the mouse being remapped. Returns true to
        // swallow the event, and appends what to send instead. wholeName: a Double just lifted, so the
        // selection should grow to the whole name.
        internal bool Decide(Btn b, bool down, bool fromChosen, bool paused, List<Out> send, out bool wholeName)
        {
            wholeName = false;
            int p = (int)b;
            owed &= ~(1 << p); pending &= ~(1 << p); // the hook heard this button, so its state is current
            PayOwed(p, send); // an earlier press of it that ended unheard is let go first
            if (down)
            {
                // A down while this button is still held (its up was missed, or another device pressed
                // the same button): let the held one go first, so nothing stays down.
                if (state[p] == Held.Remapped) LetGo(p, send);
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
                        if (!a.Hold)
                        {
                            // A modifier and a key: let go at once, not when the finger lifts. By then
                            // an admin window or the lock screen may have taken over (Alt+F4, Win+L) and
                            // kept the release from arriving, leaving Alt or Win down.
                            for (int i = a.Keys.Length - 1; i >= 0; i--) send.Add(Out.Key(a.Keys[i], false));
                            state[p] = Held.Released; // the finger's own release is swallowed
                            heldAct[p] = null;
                        }
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
        // While KeysBlocked (an admin window is in front) held keys are owed instead, since their releases
        // would be dropped; on the way out the caller clears it first, so everything goes.
        internal void ReleaseAll(List<Out> send)
        {
            for (int p = 0; p < 5; p++) PayOwed(p, send);
            for (int p = 0; p < 5; p++)
            {
                bool gone = (owed & (1 << p)) != 0; // its finger already lifted, unheard: no up to swallow
                if (state[p] == Held.Remapped) { LetGo(p, send); state[p] = gone ? Held.Nothing : Held.Released; }
                else if (state[p] != Held.Released || gone) state[p] = Held.Nothing;
                heldAct[p] = null;
            }
            owed = 0;
            if (clickLocked) { send.Add(Out.Mouse(Btn.Left, false)); clickLocked = false; }
        }

        // ---- presses and releases the hook did not hear ------------------------------------------

        // A new Raw Input report has come, so the hook has heard all it will of the ones before: their
        // releases it still hasn't heard are owed.
        internal void NextReport() { owed |= pending; pending = 0; }

        // Raw Input says the chosen mouse let go of b (in the latest report).
        internal void RawUp(Btn b)
        {
            int p = (int)b;
            if (state[p] == Held.Remapped || state[p] == Held.Released) pending |= 1 << p;
        }

        // Raw Input says the chosen mouse pressed b. If its last release went unheard, that press is
        // over, and what it held is owed until it can be let go; this press starts afresh.
        internal void RawDown(Btn b)
        {
            int p = (int)b;
            if ((owed & (1 << p)) == 0) return;
            owed &= ~(1 << p);
            if (state[p] == Held.Remapped && heldAct[p] != null) owedActs.Add(new KeyValuePair<int, Act>(p, heldAct[p]));
            state[p] = Held.Nothing;
            heldAct[p] = null;
        }

        // A held key's release that would be dropped now (an admin window is in front): swallowed, and
        // let go by Settle once it can arrive.
        internal void Defer(Btn b) { owed |= 1 << (int)b; }

        internal bool Owes { get { return owed != 0 || owedActs.Count > 0; } }

        // Whether b holds keys, so the caller checks what is in front before letting them go.
        internal bool HoldsKeys(Btn b)
        {
            int p = (int)b;
            return state[p] == Held.Remapped && heldAct[p] != null && heldAct[p].Kind == ActKind.Keys;
        }

        // Held keys whose button Raw Input saw let go, the hook not (yet) hearing it: the caller watches
        // what is in front while there are any, and lets them go as soon as no admin window is.
        internal bool OwesKeys
        {
            get
            {
                foreach (KeyValuePair<int, Act> o in owedActs) if (o.Value.Kind == ActKind.Keys) return true;
                for (int p = 0; p < 5; p++) if (((owed | pending) & (1 << p)) != 0 && HoldsKeys((Btn)p)) return true;
                return false;
            }
        }

        // Any keys held down, or owed: then the caller keeps track of what is in front.
        internal bool HoldsAnyKeys
        {
            get { if (OwesKeys) return true; for (int p = 0; p < 5; p++) if (HoldsKeys((Btn)p)) return true; return false; }
        }

        // Lets go of held keys only, whenever no admin window is in front: a key release goes to the window
        // in front, wherever the pointer is, so it needn't wait for an event the hook hears (unlike a
        // mouse button's, which goes to the window under the pointer). early: also keys whose release is
        // in the latest report, which the hook may yet hear; that release is then swallowed. Returns
        // whether it let go of any the hook is known not to have heard.
        internal bool SettleKeys(List<Out> send, bool keysBlocked, bool early)
        {
            KeysBlocked = keysBlocked;
            if (keysBlocked) return false;
            bool unheard = false;
            for (int i = 0; i < owedActs.Count; i++)
            {
                if (owedActs[i].Value.Kind != ActKind.Keys) continue;
                ReleaseOf(owedActs[i].Value, send);
                owedActs.RemoveAt(i--);
                unheard = true;
            }
            for (int p = 0; p < 5; p++)
            {
                if (!HoldsKeys((Btn)p)) continue;
                if ((owed & (1 << p)) != 0) { owed &= ~(1 << p); Release(p, send); unheard = true; }
                else if (early && (pending & (1 << p)) != 0) { Release(p, send); state[p] = Held.Released; }
            }
            return unheard;
        }

        // After each event the hook hears: lets go of what presses that ended unheard still hold. While
        // keysBlocked (an admin window is in front, and Windows would drop them) held keys wait.
        internal void Settle(List<Out> send, bool keysBlocked)
        {
            KeysBlocked = keysBlocked;
            for (int p = 0; p < 5; p++) PayOwed(p, send);
            for (int p = 0; p < 5; p++)
            {
                if ((owed & (1 << p)) == 0 || (keysBlocked && HoldsKeys((Btn)p))) continue;
                owed &= ~(1 << p);
                if (state[p] == Held.Remapped) Release(p, send);
                state[p] = Held.Nothing;
            }
        }

        // Lets go of what earlier presses of button p, ended unheard, still hold; keys wait while blocked.
        void PayOwed(int p, List<Out> send)
        {
            for (int i = 0; i < owedActs.Count; i++)
            {
                if (owedActs[i].Key != p || (KeysBlocked && owedActs[i].Value.Kind == ActKind.Keys)) continue;
                ReleaseOf(owedActs[i].Value, send);
                owedActs.RemoveAt(i--);
            }
        }

        // Something else let the left button go while it was locked down: the lock is over.
        internal void LockLost() { clickLocked = false; }

        void Release(int p, List<Out> send)
        {
            Act h = heldAct[p];
            state[p] = Held.Nothing;
            heldAct[p] = null;
            ReleaseOf(h, send);
        }

        // The same, except that held keys are owed while an admin window is in front.
        void LetGo(int p, List<Out> send)
        {
            Act h = heldAct[p];
            if (h != null && h.Kind == ActKind.Keys && KeysBlocked)
            {
                owedActs.Add(new KeyValuePair<int, Act>(p, h));
                state[p] = Held.Nothing;
                heldAct[p] = null;
            }
            else Release(p, send);
        }

        static void ReleaseOf(Act h, List<Out> send)
        {
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

    // Which device a click came from. Windows names the device in a Raw Input report, written just
    // before the low-level hook hears the same click (measured on real hardware: 68 of 68 presses and
    // releases). So a click's own report is the newest one for its button: an older one belongs to a
    // click the hook never heard (an admin window had it) and must not be taken for this one.
    internal sealed class Attribution
    {
        const int Flags = 12;
        internal const int MaxAgeMs = 200;
        readonly IntPtr[] device = new IntPtr[Flags];
        readonly int[] tick = new int[Flags];
        readonly bool[] open = new bool[Flags];

        // Every report is kept, those with no device (dev 0) too: input that programs send, this one's
        // own replacement clicks included, and a precision touchpad's. A touchpad click then finds its
        // own report, not an older one from the mouse.
        internal void OnReport(IntPtr dev, ushort buttonFlags, int now)
        {
            for (int f = 0; f < Flags; f++)
            {
                if ((buttonFlags & (1 << f)) == 0) continue;
                device[f] = dev;
                tick[f] = now;
                open[f] = true;
            }
        }

        // The device behind one click: the newest report with its flag, if not already used. False if
        // there is none or it names no device; the click then counts as not from the chosen mouse, so a
        // touchpad is never remapped by mistake.
        internal bool Claim(ushort flag, int now, out IntPtr dev)
        {
            dev = IntPtr.Zero;
            int f = 0;
            while (f < Flags && (flag & (1 << f)) == 0) f++;
            if (f == Flags || !open[f] || unchecked(now - tick[f]) > MaxAgeMs) return false;
            open[f] = false;
            dev = device[f];
            return dev != IntPtr.Zero;
        }
    }

    // Windows silently drops a low-level hook whose callback is ever too slow. The tell: the chosen
    // mouse is in use (its Raw Input reports keep coming) while the hook hears nothing at all. Only
    // reports from the chosen mouse count: input that programs send, a touchpad or another mouse never
    // do. A tool that takes the mouse to another PC (Mouse Without Borders, Barrier, Input Leap) makes
    // the hook fall silent too, but it holds the pointer still, while with a dropped hook it moves.
    internal sealed class HookWatch
    {
        internal const int SilentMs = 3000, SustainMs = 2000;
        bool silent;
        int silentSince, x0, y0;

        // Called for each report from the chosen mouse, with where the pointer is. True once the hook
        // has heard nothing since the silence began, for long enough, while the pointer moved.
        internal bool OnChosenMouseUse(int now, int lastHookTick, int x, int y)
        {
            if (unchecked(now - lastHookTick) <= SilentMs) { silent = false; return false; }
            if (silent && unchecked(lastHookTick - silentSince) > 0) silent = false; // it spoke since: a new silence
            if (!silent) { silent = true; silentSince = now; x0 = x; y0 = y; return false; }
            if (unchecked(now - silentSince) < SustainMs || (x == x0 && y == y0)) return false;
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
