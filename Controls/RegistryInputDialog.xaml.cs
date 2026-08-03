using System;
using System.Windows;
using System.Windows.Input;

using KillerShell.Shell;

namespace KillerShell
{
    // A single-line themed text prompt: message + one TextBox + Cancel/OK. ConfirmDialog's
    // sibling for the one shape it does not carry (typed input rather than a yes/no choice).
    // Used for renaming a registry key/value and for naming a freshly created one
    // (RegistryEditorControl.cs) - no stock Win32 InputBox anywhere in this app.
    public partial class RegistryInputDialog : Window
    {
        public bool Confirmed { get; private set; }
        public string Value => InputBox.Text;

        // Validates the typed name before OK is allowed to close - a blank name or one containing
        // a backslash (which would be read as a path separator, silently creating/renaming into a
        // different key than the one asked for) is refused inline rather than accepted and failing
        // later against the registry API.
        private readonly Func<string, string?>? _validate;

        public RegistryInputDialog(string message, string initialValue, string okText,
                                   Func<string, string?>? validate = null)
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                Anim.FadeIn(RootBorder);
                InputBox.Text = initialValue;
                InputBox.SelectAll();
                InputBox.Focus();
            };
            SourceInitialized += (_, _) => MainWindow.ApplyThemeBorder(this);

            MsgText.Text  = message;
            OkBtn.Content = okText;
            _validate     = validate;
        }

        private bool TryCommit()
        {
            string v = InputBox.Text.Trim();
            string? error = _validate?.Invoke(v);
            if (error != null)
            {
                ErrorText.Text       = error;
                ErrorText.Visibility = Visibility.Visible;
                return false;
            }
            Confirmed = true;
            return true;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (TryCommit()) Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; if (TryCommit()) Close(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Confirmed = false; Close(); }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => DragMove();
    }
}
