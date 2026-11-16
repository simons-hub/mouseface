using System;
using System.Diagnostics;
using System.Threading;

namespace MouseFace
{
    // A Double click selects the whole name. Programs end a double-click word at - . / and the like,
    // so on release-notes-v2.md they select only "release". When the button lifts, this thread widens
    // the selection under the pointer out to the spaces on either side, through UI Automation
    // (Windows' accessibility interface). Only this thread waits on the program: a slow or hung one
    // never delays the hook or the injector, and its selection stays as it made it.
    internal sealed class WholeNameSelector
    {
        const int NameReach = 300; // characters read on each side of the selection
        const int TextPatternId = 10014, Character = 0, RangeStart = 0, RangeEnd = 1; // UI Automation's numbers

        readonly AutoResetEvent asked = new AutoResetEvent(false);
        readonly int ownPid = Process.GetCurrentProcess().Id;
        long askedAt;               // where the button lifted: x in the high half, y in the low
        volatile int askedTick;
        volatile bool running;
        int errors;
        IUIAutomation uia;

        internal int Asks;           // counted for the live test
        internal Action<string> Watch;

        internal void Start()
        {
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.Name = "select";
            t.SetApartmentState(ApartmentState.MTA); // UI Automation clients belong off any UI thread
            running = true;
            t.Start();
        }

        // Hook thread: note where, wake the select thread, return. Never waits.
        internal void Ask(int x, int y)
        {
            Asks++;
            if (!running) return;
            Interlocked.Exchange(ref askedAt, ((long)x << 32) | (uint)y);
            askedTick = Environment.TickCount;
            asked.Set();
        }

        void Run()
        {
            try { uia = (IUIAutomation)new CUIAutomation(); }
            catch (Exception ex) { running = false; Log.Write("whole-name selection off: UI Automation did not start: " + ex.Message); return; }
            while (true)
            {
                asked.WaitOne();
                Thread.Sleep(40); // let the program finish its own double-click selection first
                if (unchecked(Environment.TickCount - askedTick) > 1000) continue; // a program held us up; too late now
                long at = Interlocked.Read(ref askedAt);
                string result;
                try { result = SelectAt((int)(at >> 32), (int)at); }
                catch (Exception ex)
                {
                    result = "skipped in " + Desk.ForegroundProgram() + ", " + ex.GetType().Name + ": " + ex.Message;
                    if (errors++ < 5) Log.Write("whole-name selection " + result);
                }
                Action<string> w = Watch;
                if (w != null) w("whole name: " + result);
            }
        }

        // Widens the selection under (x, y) to the whole name. Returns what it did, for --watch and tests.
        internal string SelectAt(int x, int y)
        {
            if (uia == null) uia = (IUIAutomation)new CUIAutomation();
            // The program just double-clicked has the keyboard focus, so start there. UI Automation's
            // own point lookup returns only the desktop over some terminals, so it is the fallback.
            IUIAutomationTextPattern text = TextAbove(uia.GetFocusedElement());
            if (text == null)
            {
                UiaPoint pt; pt.x = x; pt.y = y;
                text = TextAbove(uia.ElementFromPoint(pt));
            }
            if (text == null) return "no text here";
            IUIAutomationTextRangeArray sel = text.GetSelection();
            if (sel == null || sel.Length != 1) return "no single selection";
            IUIAutomationTextRange range = sel.GetElement(0);
            string inner = range.GetText(4096) ?? "";
            if (inner.Length == 0 || inner.Length >= 4096) return "selection empty or too long";
            if (!Covers(range, x, y)) return "the selection is not under the pointer"; // only the one this click made

            IUIAutomationTextRange before = range.Clone();
            before.MoveEndpointByRange(RangeEnd, range, RangeStart);
            before.MoveEndpointByUnit(RangeStart, Character, -NameReach);
            IUIAutomationTextRange after = range.Clone();
            after.MoveEndpointByRange(RangeStart, range, RangeEnd);
            after.MoveEndpointByUnit(RangeEnd, Character, NameReach);
            string left = before.GetText(-1) ?? "", right = after.GetText(-1) ?? "";
            int addLeft, addRight;
            Grow(left, inner, right, out addLeft, out addRight);
            if (addLeft == 0 && addRight == 0) return "already whole: " + inner;

            string want = left.Substring(left.Length - addLeft) + inner + right.Substring(0, addRight);
            IUIAutomationTextRange whole = range.Clone();
            if (addLeft > 0) whole.MoveEndpointByUnit(RangeStart, Character, -addLeft);
            if (addRight > 0) whole.MoveEndpointByUnit(RangeEnd, Character, addRight);
            // Programs count characters their own way (wide glyphs, emoji). Select only on an exact match.
            string got = whole.GetText(want.Length + 1);
            if (got != want) return "read back as '" + got + "', not '" + want + "'; left as the program made it";
            whole.Select();
            return "selected " + want;
        }

        // The nearest element at or above e that exposes its text, unless it is one of this program's own.
        IUIAutomationTextPattern TextAbove(IUIAutomationElement e)
        {
            IUIAutomationTreeWalker up = uia.RawViewWalker;
            for (int i = 0; e != null && i < 10; i++)
            {
                if (e.CurrentProcessId == ownPid) return null;
                IUIAutomationTextPattern t = e.GetCurrentPattern(TextPatternId) as IUIAutomationTextPattern;
                if (t != null) return t;
                e = up.GetParentElement(e);
            }
            return null;
        }

        static bool Covers(IUIAutomationTextRange range, int x, int y)
        {
            double[] r = range.GetBoundingRectangles(); // left, top, width, height for each line
            for (int i = 0; r != null && i + 3 < r.Length; i += 4)
                if (x >= r[i] - 2 && x <= r[i] + r[i + 2] + 2 && y >= r[i + 1] - 2 && y <= r[i + 1] + r[i + 3] + 2) return true;
            return false;
        }

        // A name ends at a space or line break, a quote, a bracket, a | or *, or a box-drawing border
        // (terminal programs draw those around panels).
        internal static bool EndsName(char c)
        {
            return char.IsWhiteSpace(c) || "\"'`()[]{}<>|*‘’“”«»".IndexOf(c) >= 0
                || (c >= '─' && c <= '╿');
        }

        // How many characters to add on each side of a selection to make it the whole name: out to
        // where the name ends, minus sentence punctuation at the very end. In "see (release-notes.md)."
        // a double click on "notes" grows to "release-notes.md".
        internal static void Grow(string left, string inner, string right, out int addLeft, out int addRight)
        {
            addLeft = addRight = 0;
            if (inner.Length == 0) return;
            if (!EndsName(inner[0]))
                while (addLeft < left.Length && !EndsName(left[left.Length - 1 - addLeft])) addLeft++;
            if (!EndsName(inner[inner.Length - 1]))
            {
                while (addRight < right.Length && !EndsName(right[addRight])) addRight++;
                while (addRight > 0 && ".,;:!?".IndexOf(right[addRight - 1]) >= 0) addRight--;
            }
        }
    }
}
