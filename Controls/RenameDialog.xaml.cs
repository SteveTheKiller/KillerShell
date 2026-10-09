using System.IO;
using System.Windows;
using System.Windows.Input;

using KillerShell.Shell;

namespace KillerShell
{
    // Rename prompt. See RenameDialog.xaml for why this is a window rather than an inline editor.
    public partial class RenameDialog : Window
    {
        public bool   Confirmed { get; private set; }
        public string NewName   => NameBox.Text.Trim();

        // Nickname mode (ForNickname): an empty box is a valid answer, it clears the nickname,
        // and the whole text is selected because a nickname has no extension to protect.
        private bool _allowEmpty;
        private bool _selectAll;

        /// <summary>
        /// The same prompt, asking for a nickname instead of a new name. Nothing on disk changes;
        /// the caller stores the answer (Services/Nicknames.cs). Confirming an empty box removes it.
        /// </summary>
        internal static RenameDialog ForNickname(string currentNickname)
        {
            var dlg = new RenameDialog(currentNickname) { _allowEmpty = true, _selectAll = true };
            dlg.PromptText.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, "Str_Fo_NicknameTitle");
            dlg.OkButton.SetResourceReference(ContentProperty, "Str_Btn_Save");
            return dlg;
        }

        public RenameDialog(string currentName)
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                Anim.FadeIn(RootBorder);
                NameBox.Text = currentName;
                NameBox.Focus();

                // Select the STEM, not the whole name: renaming almost never means changing the
                // extension, and selecting everything means typing one character silently drops
                // the ".psd" the file needed. Explorer settled this years ago.
                // Never for a nickname: it can hold characters Path rejects, and has no extension.
                string stem = _selectAll ? string.Empty : Path.GetFileNameWithoutExtension(currentName);
                NameBox.Select(0, stem.Length > 0 ? stem.Length : currentName.Length);
            };
            SourceInitialized += (_, _) => MainWindow.ApplyThemeBorder(this);
        }

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)      { OK_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancel_Click(this, new RoutedEventArgs()); e.Handled = true; }
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (NewName.Length == 0 && !_allowEmpty) return;   // nothing to rename to; leave the box open
            Confirmed = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    }
}
