using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Services;

// KillerUI / Grunge - title-bar language picker. Partial of MainWindow.
// MainWindow.xaml provides a LangButton whose Button.ContextMenu is x:Name="LangMenu".
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // English pinned first; the rest alphabetical by locale code. Native name left, code right.
        private static readonly (Locale Loc, string Name, string Code)[] Languages =
        [
            (Locale.EnUS, "English",     "en-US"),
            (Locale.Bn,   "বাংলা",        "bn"),
            (Locale.Cs,   "Čeština",     "cs-CZ"),
            (Locale.De,   "Deutsch",     "de-DE"),
            (Locale.Es,   "Español",     "es"),
            (Locale.Fr,   "Français",    "fr-FR"),
            (Locale.Ja,   "日本語",       "ja-JP"),
            (Locale.TrTR, "Türkçe",      "tr-TR"),
            (Locale.ZhCN, "中文 (简体)",  "zh-CN"),
            (Locale.ZhTW, "中文 (繁體)",  "zh-TW"),
        ];

        private void LangButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.ContextMenu != null)
            {
                BuildLanguageMenu(b.ContextMenu);
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.IsOpen = true;
                Anim.FadeIn(b.ContextMenu);
            }
        }

        private void BuildLanguageMenu(ContextMenu menu)
        {
            menu.Items.Clear();
            var current = LocaleManager.Current;

            foreach (var (loc, name, code) in Languages)
            {
                var grid = new Grid { MinWidth = 160 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameBlock = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
                var codeBlock = new TextBlock
                {
                    Text = "(" + code + ")",
                    Opacity = 0.5,
                    Margin = new Thickness(22, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(codeBlock, 1);
                grid.Children.Add(nameBlock);
                grid.Children.Add(codeBlock);

                var item = new MenuItem
                {
                    Header = grid,
                    Tag = loc.ToString(),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    IsChecked = loc == current,
                };
                if (loc == current && TryFindResource("PrimaryBrush") is Brush accent)
                {
                    nameBlock.Foreground = accent;
                    nameBlock.FontWeight = FontWeights.SemiBold;
                    codeBlock.Foreground = accent;
                    codeBlock.Opacity = 0.85;
                }
                item.Click += Lang_Click;
                menu.Items.Add(item);
            }
        }

        private void Lang_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is string tag && Enum.TryParse<Locale>(tag, out var loc))
            {
                LocaleManager.Apply(loc);
                RelocalizeDynamicUi();
            }
        }

        /// <summary>Look up a localized string; falls back to the key name if missing.</summary>
        private string Loc(string key) => LocStatic(key);

        /// <summary>
        /// The same lookup, reachable from a model. Bookmark needs it to name the This PC entry
        /// (Bookmarks.cs), and a model has no window to ask.
        /// </summary>
        internal static string LocStatic(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

        /// <summary>Re-applies strings set in code so a live language switch updates them.
        /// Static {DynamicResource Str_*} XAML refreshes itself; everything below is the
        /// dynamic remainder: model-computed chips/tooltips, and per-tab status lines
        /// re-rendered from their stored resource key + raw args.</summary>
        private void RelocalizeDynamicUi()
        {
            SearchButton.Content = Loc(_active.IsSearching ? "Str_Btn_Stop" : "Str_Btn_Search");

            foreach (var tab in _tabs)
            {
                // ANY/ALL chips, name/content chips, and their tooltips (INPC re-raise).
                foreach (var g in tab.Groups)
                {
                    g.RefreshLocalized();
                    foreach (var t in g.Terms) t.RefreshLocalized();
                }

                // Status line: rebuilt from key + args when we have them; transient
                // text (mid-search progress) is left alone.
                if (!string.IsNullOrEmpty(tab.StatusKey))
                    tab.StatusMessage = tab.StatusArgs is { Length: > 0 }
                        ? string.Format(Loc(tab.StatusKey!), tab.StatusArgs)
                        : Loc(tab.StatusKey!);
                if (tab.ScannedCount >= 0)
                    tab.ScannedLabel = string.Format(Loc("Str_Status_Scanned"), tab.ScannedCount.ToString("N0"));
                if (tab.Results.Count > 0)
                    tab.StatsLabel = string.Format(Loc("Str_Count_Matches"), tab.Results.Count.ToString("N0"));
                if (!string.IsNullOrEmpty(tab.QueryLabel))
                    tab.QueryLabel = BuildQueryLabel(
                        [.. tab.Groups.Where(g => g.Terms.Any(t => !string.IsNullOrWhiteSpace(t.Pattern)))],
                        [.. tab.Filters.Where(f => f.IsActive)]);
                if (tab.PipeArgs is { Length: 3 })
                    tab.PipeLabel = string.Format(Loc("Str_Pipe_Scope"), tab.PipeArgs);
            }

            // Push the active tab's re-rendered lines into the visible controls.
            SetFooterStatus(_active.StatusMessage);          // window footer - the live line
            Pane.QueryText.Text   = _active.QueryLabel;
            Pane.StatsText.Text   = _active.StatsLabel;
            Pane.ScannedText.Text = _active.ScannedLabel;
            UpdatePaneStatusBar();
            if (!_active.IsSearching)
            {
                int c = _active.Results.Count;
                Pane.ResultsHeader.Text = c > 0 ? string.Format(Loc("Str_Lbl_ResultsCount"), c) : Loc("Str_Lbl_Results");
            }
            if (_active.PipeFiles != null)
                Pane.ScopePathLabel.Text = _active.PipeLabel;
            else if (string.IsNullOrWhiteSpace(Pane.RootPathBox.Text))
                Pane.ScopePathLabel.Text = Loc("Str_Scope_Empty");
        }
    }
}
