using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using KillerShell.Models;
using KillerShell.Terminal;

namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private TextBlock? _activeTitleText;

        private void UpdateTitleContext()
        {
            if (TitleContextHost == null || Pane.Active == null) return;
            if (_activeTitleText == null)
            {
                _activeTitleText = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false
                };
                _activeTitleText.SetResourceReference(TextBlock.FontFamilyProperty, "FileNameFont");
                _activeTitleText.SetResourceReference(TextBlock.FontSizeProperty, "FileNameFontSize");
                _activeTitleText.SetResourceReference(TextBlock.ForegroundProperty, "ChromeTextBrush");
                TitleContextHost.Content = _activeTitleText;
            }
            // The title bar names what KIND of tab is active, never its location: the tab and the
            // location row already show where it is. Bound through Title, which the Processes and
            // Services toggle rewrites, and IsBrowsing, which flips when a folder tab runs a search.
            var tab = Pane.Active;
            var binding = new MultiBinding { Converter = new TabKindLabel(tab) };
            binding.Bindings.Add(new Binding(nameof(SearchTab.Title)) { Source = tab });
            binding.Bindings.Add(new Binding(nameof(SearchTab.IsBrowsing)) { Source = tab });
            _activeTitleText.SetBinding(TextBlock.TextProperty, binding);
        }

        internal static string TabKindTitle(SearchTab tab)
        {
            if (tab.Term != null)
                return Loc(tab.Term.ShellKind switch
                {
                    TerminalShellKind.PowerShell => "Str_TitleKind_PowerShell",
                    TerminalShellKind.Cmd => "Str_TitleKind_Cmd",
                    TerminalShellKind.Wsl => "Str_TitleKind_Wsl",
                    _ => "Str_Ks_ScopeTerminal",
                });
            if (tab.Editor != null) return Loc("Str_TitleKind_Editor");
            if (tab.Procs != null) return Loc(tab.Procs.IsProcessesMode ? "Str_TabTitle_Processes" : "Str_TabTitle_Services");
            if (tab.Events != null) return Loc("Str_TabTitle_EventViewer");
            if (tab.Perf != null) return Loc("Str_TitleKind_Performance");
            if (tab.Registry != null) return Loc("Str_TabTitle_RegistryEditor");
            if (tab.Storage != null) return Loc("Str_TabTitle_Storage");
            return Loc(tab.IsBrowsing ? "Str_TitleKind_FileBrowser" : "Str_TitleKind_Search");
        }

        private sealed class TabKindLabel(SearchTab tab) : IMultiValueConverter
        {
            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
                => TabKindTitle(tab);

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
                => throw new NotSupportedException();
        }
    }
}
