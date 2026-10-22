using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Services;
using KillerShell.Tests.Safety;
using Microsoft.Win32;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class UninstallLocalizationTests
{
    [DisposableVmFact]
    [Trait("Category", "DisposableVm")]
    public Task InteractiveUninstallUsesSavedSlovenianBeforeShowingConfirmation()
    {
        DisposableVmGuard.RequireDisposableVm();
        return RuntimeComponentProcess.Run(nameof(UninstallLocalizationScenario));
    }
}

public static class UninstallLocalizationScenario
{
    public static void Run()
    {
        DisposableVmGuard.RequireDisposableVm();
        const string settingsPath = @"Software\KillerShell";
        bool keyExisted;
        bool localeExisted;
        object? oldLocale;
        RegistryValueKind oldKind;
        using (var existing = Registry.CurrentUser.OpenSubKey(settingsPath))
        {
            keyExisted = existing != null;
            localeExisted = existing != null && Array.IndexOf(existing.GetValueNames(), "Locale") >= 0;
            oldLocale = existing?.GetValue("Locale", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            oldKind = localeExisted ? existing!.GetValueKind("Locale") : RegistryValueKind.String;
        }

        App? app = null;
        bool confirmationSeen = false;
        Exception? confirmationFailure = null;
        try
        {
            using (var settings = Registry.CurrentUser.CreateSubKey(settingsPath))
                settings.SetValue("Locale", "SlSI", RegistryValueKind.String);

            Application.ResourceAssembly = typeof(App).Assembly;
            app = new App();
            app.InitializeComponent();
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) =>
                {
                    if (sender is not ConfirmDialog dialog) return;
                    try
                    {
                        Assert.False(confirmationSeen, "Uninstall unexpectedly opened another confirmation.");
                        confirmationSeen = true;
                        Assert.Equal("Odstranim KillerShell iz tega računalnika?",
                            Assert.IsType<TextBlock>(dialog.FindName("MsgText")).Text);
                        Assert.Equal("Odstrani", Assert.IsType<Button>(dialog.FindName("OkBtn")).Content);
                        Assert.Equal("Prekliči", Assert.IsType<Button>(dialog.FindName("CancelBtn")).Content);
                        Assert.Equal(Locale.SlSI, LocaleManager.Current);
                    }
                    catch (Exception error) { confirmationFailure = error; }
                    finally
                    {
                        // Closing without the affirmative button cancels before removal starts.
                        dialog.Close();
                    }
                }));

            var arguments = (StartupEventArgs)Activator.CreateInstance(typeof(StartupEventArgs), nonPublic: true)!;
            typeof(StartupEventArgs).GetField("_args", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(arguments, new[] { "/uninstall" });
            typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(app, new object[] { arguments });

            Assert.True(confirmationSeen, "The interactive uninstall route did not show its confirmation.");
            Assert.Null(confirmationFailure);
            using var remaining = Registry.CurrentUser.OpenSubKey(settingsPath);
            Assert.Equal("SlSI", remaining?.GetValue("Locale"));
        }
        finally
        {
            using (var settings = Registry.CurrentUser.OpenSubKey(settingsPath, writable: true))
            {
                if (localeExisted) settings!.SetValue("Locale", oldLocale!, oldKind);
                else settings?.DeleteValue("Locale", throwOnMissingValue: false);
            }
            if (!keyExisted)
            {
                using var settings = Registry.CurrentUser.OpenSubKey(settingsPath);
                bool empty = settings != null && settings.ValueCount == 0 && settings.SubKeyCount == 0;
                settings?.Close();
                if (empty) Registry.CurrentUser.DeleteSubKey(settingsPath, throwOnMissingSubKey: false);
            }
            app?.Shutdown();
        }
    }
}
