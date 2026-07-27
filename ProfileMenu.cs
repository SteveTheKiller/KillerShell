using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Terminal;

// Editing your PowerShell $PROFILE. Partial of MainWindow.
//
// Three ways in, because this is a thing a field tech does on a machine they have just sat down
// at and does not want to go looking for:
//
//   rail button, right-click     the shell menu already there, under the four prompts
//   inside a shell, right-click  the terminal's own menu (Terminal/TerminalMenu.cs)
//   Ctrl+comma                   the settings chord, from anywhere in the window
//
// The rows are built from the hosts actually INSTALLED rather than being a fixed pair. On a
// machine with no PowerShell 7 a "PowerShell 7" row would either open the wrong file or do
// nothing, and both are worse than the row not being there.
//
// The path itself is never guessed - see Terminal/ShellProfiles.cs for why that matters more
// than it sounds like it should.
namespace KillerShell
{
    public partial class MainWindow
    {
        /// <summary>
        /// Fill the profile submenu as it opens, from the hosts on this machine.
        /// </summary>
        /// <remarks>
        /// Rebuilt every time rather than once: PowerShell 7 getting installed while the window
        /// is open is a normal afternoon, and a submenu built at launch would go on hiding the
        /// row for the rest of the session.
        /// </remarks>
        internal void BuildProfileMenu(MenuItem parent)
        {
            parent.Items.Clear();

            var hosts = ShellProfiles.Installed();
            if (hosts.Count == 0)
            {
                // Should not happen on Windows - 5.1 is in the box - but a row that says so
                // beats an empty submenu that reads as a broken menu.
                var none = new MenuItem { IsEnabled = false };
                none.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Prof_None");
                parent.Items.Add(none);
                return;
            }

            foreach (var host in hosts)
            {
                // Header is the host's own name and is NOT localized: "PowerShell 7" and
                // "Windows PowerShell" are product names, and translating them would make the
                // row stop matching what the user sees everywhere else.
                var item = new MenuItem { Header = host.Name };

                var icon = new TextBlock { Text = ((char)0xE756).ToString() };
                icon.SetResourceReference(FrameworkElement.StyleProperty, "MenuGlyph");
                item.Icon = icon;

                var captured = host;
                item.Click += (_, _) => OpenProfile(captured);
                parent.Items.Add(item);
            }
        }

        /// <summary>
        /// Open one host's profile in an editor tab, creating it if it is not there.
        /// </summary>
        /// <remarks>
        /// Asking the shell costs a process start, so the status line says what is happening
        /// first. It is well under a second on a warm machine and this is the only place in the
        /// app that blocks the UI thread on another process - worth it, because the alternative
        /// is editing a path that might not be the one the shell loads.
        /// </remarks>
        private void OpenProfile(ShellHost host)
        {
            SetTabStatusKey(_active, "Str_Prof_Asking", host.Name);

            string path = ShellProfiles.PathFor(host);
            if (path.Length == 0)
            {
                SetTabStatusKey(_active, "Str_Prof_Failed", host.Name);
                return;
            }

            // OpenForEditing creates a missing file, which is the normal case here: on a machine
            // nobody has customized PowerShell on, $PROFILE does not exist at all (EditorTabs.cs).
            OpenForEditing(path);
        }

        /// <summary>
        /// Ctrl+comma. Opens the preferred host's profile without asking which.
        /// </summary>
        /// <remarks>
        /// Preferred, not "all of them": a hotkey that opens a menu is a hotkey that still needs
        /// a second decision, and the answer is PowerShell 7 whenever it is installed - the same
        /// order a new shell tab picks (TerminalProfile.ResolvePowerShell), so the chord and the
        /// F8 key agree about which PowerShell this machine means. The submenu is there for the
        /// times you want the other one.
        ///
        /// Ctrl+comma rather than an F-key even though single keys are the house style: F1
        /// through F12 are all spoken for, and Ctrl+comma is the settings chord in most things
        /// that have one.
        /// </remarks>
        internal void EditPreferredProfile()
        {
            var hosts = ShellProfiles.Installed();
            if (hosts.Count == 0) { SetTabStatusKey(_active, "Str_Prof_None"); return; }

            OpenProfile(hosts[0]);
        }

        // ═══════════════════════════════════════════════════════════
        //  THE RAIL MENU
        // ═══════════════════════════════════════════════════════════
        /// <summary>Fill the rail button's profile submenu as it opens (MainWindow.xaml).</summary>
        internal void RailProfileMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem parent) BuildProfileMenu(parent);
        }
    }
}
