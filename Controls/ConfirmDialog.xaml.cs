using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using KillerShell.Shell;

namespace KillerShell
{
    // Grunge-themed confirm dialog (KillerScan's ConfirmDialog, generalized): a message,
    // optional bullet lines, and up to two optional checkboxes. Used by the portable
    // Install prompt and the smart-Esc quit prompt.
    public partial class ConfirmDialog : Window
    {
        public bool Confirmed     { get; private set; }

        /// <summary>The id of the extra button pressed (<see cref="AddChoice"/>), or null.</summary>
        public string? Choice { get; private set; }
        public bool Check1Checked => Check1.IsChecked == true;
        public bool Check2Checked => Check2.IsChecked == true;

        public ConfirmDialog(string message, string? bullets, string okText,
                             string? check1Label = null, bool check1Initial = false,
                             string? check2Label = null, bool check2Initial = false,
                             bool installer = false, bool notification = false)
        {
            if (installer) Resources.MergedDictionaries.Add(Services.ThemeManager.CreateInstallerTheme());
            InitializeComponent();
            TaskbarIdentity.Track(this);
            // Same icon frame the main title bar picks from the multi-size .ico.
            try
            {
                var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    new System.Uri("pack://application:,,,/Resources/ksh-icon.ico"),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                TitleIcon.Source = dec.Frames[0];
            }
            catch { TitleIcon.Visibility = Visibility.Collapsed; }
            if (installer)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ShowInTaskbar = true;
            }
            if (notification) CancelBtn.Visibility = Visibility.Collapsed;
            Loaded += (_, _) => Anim.FadeIn(RootBorder);
            Loaded += (_, _) => ApplyNotice();
            SourceInitialized += (_, _) => MainWindow.ApplyThemeBorder(this);

            MsgText.Text  = message;
            OkBtn.Content = okText;

            if (!string.IsNullOrEmpty(bullets))
            {
                BulletText.Text       = bullets;
                BulletText.Visibility = Visibility.Visible;
            }
            if (!string.IsNullOrEmpty(check1Label))
            {
                Check1.Content    = new System.Windows.Controls.TextBlock { Text = check1Label, TextWrapping = TextWrapping.Wrap };
                Check1.IsChecked  = check1Initial;
                Check1.Visibility = Visibility.Visible;
            }
            if (!string.IsNullOrEmpty(check2Label))
            {
                Check2.Content    = new System.Windows.Controls.TextBlock { Text = check2Label, TextWrapping = TextWrapping.Wrap };
                Check2.IsChecked  = check2Initial;
                Check2.Visibility = Visibility.Visible;
            }
        }

        /// <summary>Pre-ticked and locked, with an explanatory note underneath. Used when the
        /// choice is already made for the user: KillerShell is installed machine-wide, so the
        /// all-users box cannot be unticked without leaving two copies behind.</summary>
        public void LockCheck2(string? note = null)
        {
            Check2.IsChecked  = true;
            Check2.IsEnabled  = false;
            Check2.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(note))
            {
                Check2Note.Text       = note;
                Check2Note.Visibility = Visibility.Visible;
            }
        }

        /// <summary>An extra answer between Cancel and OK, styled like Cancel. Pressing it closes
        /// the dialog unconfirmed with <see cref="Choice"/> set to <paramref name="id"/>.</summary>
        public void AddChoice(string id, string label)
        {
            var button = new System.Windows.Controls.Button
            {
                Content  = label,
                Style    = (Style)FindResource("SurfaceButton"),
                Height   = 28,
                MinWidth = 80,
                Padding  = new Thickness(12, 2, 12, 2),
                Margin   = new Thickness(0, 0, 8, 0),
            };
            button.Click += (_, _) => { Choice = id; Confirmed = false; Close(); };
            ButtonRow.Children.Insert(ButtonRow.Children.IndexOf(OkBtn), button);
        }

        /// <summary>Enter and Esc both answer Cancel, for a prompt whose OK replaces something.</summary>
        public void DefaultToCancel()
        {
            CancelBtn.IsDefault = true;
            CancelBtn.IsCancel  = true;
            Loaded += (_, _) => CancelBtn.Focus();
        }

        // The family setup dialog's notice ring: an amber "!" on a confirmation and the accent "i" on a
        // notice with a single button. The amber darkens on a light card so it keeps its contrast.
        private void ApplyNotice()
        {
            FitHeading();
            FitCheckLabels();
            bool confirmation = CancelBtn.Visibility == Visibility.Visible;
            System.Windows.Media.Brush brush;
            if (confirmation)
            {
                bool light = TryFindResource("BackgroundBrush") is System.Windows.Media.SolidColorBrush card && card.Color.R * 0.299 + card.Color.G * 0.587 + card.Color.B * 0.114 > 140;
                brush = new System.Windows.Media.SolidColorBrush(light
                    ? System.Windows.Media.Color.FromRgb(176, 112, 0)
                    : System.Windows.Media.Color.FromRgb(255, 190, 80));
            }
            else
            {
                brush = TryFindResource("PrimaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
            }
            NoticeGlyph.Text = confirmation ? "!" : "i";
            NoticeGlyph.Foreground = brush;
            NoticeRing.BorderBrush = brush;
        }

        // Long checkbox labels wrap inside the dialog body instead of running past its edge.
        private void FitCheckLabels()
        {
            foreach (var box in new[] { Check1, Check2 })
                if (box.Content is System.Windows.Controls.TextBlock label && box.Parent is FrameworkElement body && body.ActualWidth > 0)
                    label.MaxWidth = System.Math.Max(80, body.ActualWidth - box.Margin.Left - box.Margin.Right - 40);
        }

        // A heading is one short line. Text after its first line break moves into the detail, and
        // a first line too long for a heading stays body-sized instead of shouting.
        private void FitHeading()
        {
            string text = MsgText.Text ?? "";
            int lineBreak = text.IndexOf('\n');
            if (lineBreak >= 0)
            {
                string rest = text[(lineBreak + 1)..].Trim();
                MsgText.Text = text[..lineBreak].TrimEnd('\r');
                if (rest.Length > 0)
                {
                    string existing = BulletText.Visibility == Visibility.Visible ? BulletText.Text : "";
                    BulletText.Text = string.IsNullOrEmpty(existing) ? rest : rest + "\n\n" + existing;
                    BulletText.Visibility = Visibility.Visible;
                }
            }
            if ((MsgText.Text ?? "").Length > 90)
            {
                MsgText.ClearValue(System.Windows.Controls.TextBlock.FontFamilyProperty);
                MsgText.ClearValue(System.Windows.Media.TextOptions.TextFormattingModeProperty);
                MsgText.FontSize = 13;
            }
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => DragMove();
    }
}
