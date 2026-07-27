using System.IO;
using System.Windows;
using System.Windows.Input;

namespace KillerShell
{
    // Rename prompt. See RenameDialog.xaml for why this is a window rather than an inline editor.
    public partial class RenameDialog : Window
    {
        public bool   Confirmed { get; private set; }
        public string NewName   => NameBox.Text.Trim();

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
                string stem = Path.GetFileNameWithoutExtension(currentName);
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
            if (NewName.Length == 0) return;   // nothing to rename to; leave the box open
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
