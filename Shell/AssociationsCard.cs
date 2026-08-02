using System;
using System.Windows;
using System.Windows.Input;

// The Associations card (MainWindow.xaml, AssocOverlay). Same overlay shape as the Fonts and
// Pattern cards: dim backdrop, centered surface, click the backdrop to dismiss.
//
// The card's whole job is to be honest about a distinction Windows makes and most apps blur:
// REGISTERING puts KillerShell in the Open With list and in Settings' Default apps list, and we
// can do that. DEFAULTING is the user's call, made in Settings, and nothing here can or should
// do it for them. See Associations.cs.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private void AssocRow_Click(object sender, RoutedEventArgs e)
        {
            ThemePopup.IsOpen = false;
            SyncAssocCard();
            AssocOverlay.Visibility = Visibility.Visible;
            Anim.FadeIn(AssocOverlay);
        }

        private void AssocClose_Click(object sender, RoutedEventArgs e) => HideAssoc();
        private void AssocOverlay_Click(object sender, MouseButtonEventArgs e) => HideAssoc();
        private void AssocCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void HideAssoc() => AssocOverlay.Visibility = Visibility.Collapsed;

        /// <summary>
        /// Fills the extension list and the status line, and decides which of the two action
        /// buttons make sense right now.
        /// </summary>
        private void SyncAssocCard()
        {
            AssocExtList.Text = string.Join("  ", App.TextExtensions);

            bool user    = App.AssociationsRegistered(machine: false);
            bool machine = App.AssociationsRegistered(machine: true);

            // The all-users box is only meaningful from an elevated window, because HKLM is
            // not writable otherwise. Rather than let the click fail silently, say why.
            AssocAllUsers.IsEnabled = IsElevated;
            AssocAllUsers.IsChecked = machine;
            AssocAllUsers.ToolTip   = IsElevated ? null : Loc("Str_Assoc_NeedsAdmin");

            AssocRemoveBtn.IsEnabled = user || machine;

            AssocStatus.Text =
                machine ? Loc("Str_Assoc_StateMachine")
              : user    ? Loc("Str_Assoc_StateUser")
              :           Loc("Str_Assoc_StateNone");

            // A portable copy registering itself points every association at an exe the user
            // may well delete or move tomorrow. Not blocked - a portable copy on a stick is a
            // legitimate thing to want associations for - but it is worth saying out loud.
            if (App.IsPortable()) AssocStatus.Text += "\n" + Loc("Str_Assoc_Portable");
        }

        private void AssocRegister_Click(object sender, RoutedEventArgs e)
        {
            bool machine = AssocAllUsers.IsChecked == true && IsElevated;
            bool ok = App.RegisterAssociations(machine);

            SyncAssocCard();
            if (!ok) AssocStatus.Text = Loc("Str_Assoc_Failed");
            SetStatus(ok ? Loc("Str_Assoc_Registered") : Loc("Str_Assoc_Failed"));
        }

        private void AssocRemove_Click(object sender, RoutedEventArgs e)
        {
            // Both scopes, always. Registering per-user and later elevating to all-users
            // leaves two sets, and a Remove that only took one back would look like it failed.
            bool ok = App.UnregisterAssociations(machine: false);
            if (IsElevated) ok &= App.UnregisterAssociations(machine: true);

            SyncAssocCard();
            SetStatus(ok ? Loc("Str_Assoc_Removed") : Loc("Str_Assoc_Failed"));
        }

        /// <summary>
        /// Opens Settings on our own entry where Windows supports it, and on the plain Default
        /// apps page where it does not.
        /// </summary>
        /// <remarks>
        /// The registeredApp query parameter arrived in Windows 11 21H2 with the 2023-04
        /// cumulative update. On anything older the parameter is ignored rather than rejected,
        /// but the scope has to match where the app was actually registered - pointing at
        /// registeredAppUser when only HKLM has the key lands on an empty page.
        /// </remarks>
        private void AssocSettings_Click(object sender, RoutedEventArgs e)
        {
            string uri = "ms-settings:defaultapps";
            if (App.AssociationsRegistered(machine: true))
                uri += "?registeredAppMachine=" + Uri.EscapeDataString(App.AssocAppName);
            else if (App.AssociationsRegistered(machine: false))
                uri += "?registeredAppUser=" + Uri.EscapeDataString(App.AssocAppName);

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri)
                {
                    UseShellExecute = true,
                });
            }
            catch { SetStatus(Loc("Str_Assoc_Failed")); }
        }
    }
}
