using System;
using System.Drawing;
using System.Windows.Forms;

namespace MouseFace
{
    // MouseFace's own message window, in place of Windows' standard message box, whose 9-point text is
    // small: an icon, the words in 11-point text, a tick box when there is a choice to make, and its
    // buttons. Notices that can come from any thread, or from a crash, still use Windows' box (Program.Box).
    internal sealed class MessageForm : ScaledForm
    {
        internal enum Kind { Information, Warning, Question }

        readonly CheckBox option;
        readonly PictureBox picture;
        readonly Label words;
        readonly Kind kind;

        // yes: the first button ("OK" for a message); no: the second, or null for one button.
        // optionText: a tick box under the words, not ticked, or null for none.
        internal MessageForm(string caption, string text, Kind kind, string yes, string no, string optionText)
        {
            this.kind = kind;
            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Icon = App.Icon;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None; // sizes are scaled by Ui.S
            Font = new Font("Segoe UI", 11f);

            TableLayoutPanel page = new TableLayoutPanel();
            page.AutoSize = true;
            page.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            page.ColumnCount = 2;
            page.Padding = Ui.Pad(20, 20, 24, 16);

            picture = new PictureBox();
            picture.Size = Ui.Sz(32, 32);
            picture.SizeMode = PictureBoxSizeMode.Zoom;
            picture.Image = KindImage(kind, Ui.S(32));
            picture.Margin = Ui.Pad(0, 0, 14, 0);
            page.Controls.Add(picture, 0, 0);

            words = new Label();
            words.AutoSize = true;
            words.MaximumSize = Ui.Sz(420, 0);
            words.Text = text;
            words.Margin = Ui.Pad(0, 4, 0, 0); // its first line level with the icon
            page.Controls.Add(words, 1, 0);

            if (optionText != null)
            {
                option = new CheckBox();
                option.AutoSize = true;
                option.Text = optionText;
                option.Margin = Ui.Pad(0, 14, 0, 0);
                page.Controls.Add(option, 1, 1);
            }

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.WrapContents = false;
            buttons.Anchor = AnchorStyles.Right;
            buttons.Margin = Ui.Pad(0, 20, 0, 0);
            Button first = SettingsForm.DialogButton(yes);
            first.DialogResult = DialogResult.OK;
            buttons.Controls.Add(first);
            Button second = null;
            if (no != null)
            {
                second = SettingsForm.DialogButton(no);
                second.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(second);
            }
            AcceptButton = first;
            CancelButton = second ?? first;
            // Focus on the first button, as in Windows' message box, never on the tick box, where a Space
            // out of habit would tick "delete my settings".
            Shown += delegate { ActiveControl = first; };
            page.Controls.Add(buttons, 0, 2);
            page.SetColumnSpan(buttons, 2);

            Controls.Add(page);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        internal bool Ticked { get { return option != null && option.Checked; } }
        internal Image IconImage { get { return picture.Image; } } // for the tests
        internal int IconSize { get { return picture.Width; } }

        // A picture box doesn't own its image, so the window lets go of it.
        protected override void Dispose(bool disposing)
        {
            if (disposing && picture != null && picture.Image != null)
            {
                Image image = picture.Image;
                picture.Image = null;
                image.Dispose();
            }
            base.Dispose(disposing);
        }

        // Moved to a screen scaled differently: the icon is drawn again at its new size, not stretched.
        protected override void AfterRescale()
        {
            Image old = picture.Image;
            picture.Image = KindImage(kind, picture.Width);
            if (old != null) old.Dispose();
        }

        // Ctrl+C copies the message, as in Windows' message box: for a bug report, or a path to delete.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.C))
            {
                try { Clipboard.SetText(Text + Environment.NewLine + Environment.NewLine + words.Text); } catch (Exception) { }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // A message with OK.
        internal static void Tell(IWin32Window owner, string caption, string text, bool warning)
        {
            using (MessageForm f = new MessageForm(caption, text, warning ? Kind.Warning : Kind.Information, "OK", null, null))
            {
                f.ShowInTaskbar = owner == null; // a box of setup's needs no taskbar button of its own
                f.ShowDialog(owner);
            }
        }

        // A question: true for the first button. ticked: whether its tick box was ticked (false when
        // there is none, or the answer was no).
        internal static bool Ask(IWin32Window owner, string caption, string text, string yes, string no, string optionText, out bool ticked)
        {
            using (MessageForm f = new MessageForm(caption, text, Kind.Question, yes, no, optionText))
            {
                f.ShowInTaskbar = owner == null;
                bool go = f.ShowDialog(owner) == DialogResult.OK;
                ticked = go && f.Ticked;
                return go;
            }
        }

        // Windows' own icon for the kind, the one its message boxes show, drawn at this size rather than
        // stretched; Windows' older icon if that can't be had.
        static Image KindImage(Kind kind, int size)
        {
            Bitmap image = new Bitmap(size, size);
            try
            {
                Native.SHSTOCKICONINFO info = new Native.SHSTOCKICONINFO();
                info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.SHSTOCKICONINFO));
                int id = kind == Kind.Warning ? Native.SIID_WARNING : kind == Kind.Question ? Native.SIID_HELP : Native.SIID_INFO;
                IntPtr large, small;
                if (Native.SHGetStockIconInfo(id, 0, ref info) == 0
                    && Native.SHDefExtractIcon(info.szPath, info.iIcon, 0, out large, out small, (uint)((16 << 16) | size)) == 0)
                {
                    try
                    {
                        using (Icon icon = Icon.FromHandle(large))
                        using (Graphics g = Graphics.FromImage(image))
                            g.DrawIcon(icon, new Rectangle(0, 0, size, size));
                        return image;
                    }
                    finally { Native.DestroyIcon(large); if (small != IntPtr.Zero) Native.DestroyIcon(small); }
                }
            }
            catch (Exception) { }
            Icon old = kind == Kind.Warning ? SystemIcons.Warning : kind == Kind.Question ? SystemIcons.Question : SystemIcons.Information;
            using (Graphics g = Graphics.FromImage(image)) g.DrawIcon(old, new Rectangle(0, 0, size, size));
            return image;
        }
    }
}
