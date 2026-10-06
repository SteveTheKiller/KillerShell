using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests;

public sealed class PickerNavigationTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void PinsAndSuccessfulFoldersSurviveReopeningWithoutChangingUserSettings()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var originalRead = ThemeManager.GetSetting;
            var originalWrite = ThemeManager.SetSetting;
            var settings = new Dictionary<string, string>();
            string root = Path.Combine(Path.GetTempPath(), "KillerShell-picker-test-" + Guid.NewGuid().ToString("N"));
            Application? app = null;
            try
            {
                ThemeManager.GetSetting = key => settings.TryGetValue(key, out string value) ? value : null;
                ThemeManager.SetSetting = (key, value) => settings[key] = value;
                Application.ResourceAssembly = typeof(FileDialog).Assembly;
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/KillerShell;component/" + resource)
                    });
                typeof(ThemeManager).GetMethod("LoadDict", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object?[] { ThemeManager.Current });

                string first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
                string second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
                string missing = Path.Combine(root, "missing");
                string openFile = Path.Combine(first, "open.txt");
                File.WriteAllText(openFile, "picker fixture");

                var defaults = Pins();
                Assert.Equal(new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                }.Where(path => path.Length > 0), defaults);
                Assert.False(settings.ContainsKey("FileDlgPinned"));
                settings["FileDlgPinned"] = "";
                Assert.Empty(Pins());

                var picker = new FileDialog();
                Call(picker, "PinPlace", first);
                Call(picker, "PinPlace", first.ToUpperInvariant() + "\\");
                Assert.Single(Pins());
                Call(picker, "PinPlace", second);
                Field(picker, "_placesMenuPlace").SetValue(picker, picker.Places.First(place => place.Pinned && place.Path == first));
                Call(picker, "UnpinPlace_Click", picker, new RoutedEventArgs());
                Assert.Equal(new[] { second }, Pins());
                Field(picker, "_placesMenuPlace").SetValue(picker, picker.Places.Single(place => place.Pinned));
                Call(picker, "UnpinPlace_Click", picker, new RoutedEventArgs());
                Assert.Empty(Pins());
                Assert.Equal("", settings["FileDlgPinned"]);

                settings["FileDlgPinned"] = string.Join("|", first, missing, second);
                Call(picker, "BuildPlaces");
                Assert.Equal(new[] { first, second }, picker.Places.Where(place => place.Pinned).Select(place => place.Path));
                picker.Places.Move(picker.Places.IndexOf(picker.Places.First(place => place.Path == second)), 0);
                Call(picker, "SavePinOrder");
                Assert.Equal(new[] { second, first, missing }, Pins());
                Call(picker, "BuildPlaces");
                Assert.Equal(new[] { second, first }, picker.Places.Where(place => place.Pinned).Select(place => place.Path));
                picker.Close();
                var reopened = new FileDialog();
                Call(reopened, "BuildPlaces");
                Assert.Equal(new[] { second, first }, reopened.Places.Where(place => place.Pinned).Select(place => place.Path));
                reopened.Close();

                var open = new FileDialog(FileDialogMode.Open) { InitialDirectory = first, FileName = "open.txt", Opacity = 0 };
                Assert.True(Show(open, first, dialog => Call(dialog, "OK_Click", dialog, new RoutedEventArgs())));
                Assert.Equal(openFile, open.FileName);
                Assert.Equal(first, settings["FileDlgLastOpenDir"]);
                Assert.False(settings.ContainsKey("FileDlgLastSaveDir"));
                var save = new FileDialog(FileDialogMode.Save) { InitialDirectory = second, FileName = "new.txt", Opacity = 0 };
                Assert.True(Show(save, second, dialog => Call(dialog, "OK_Click", dialog, new RoutedEventArgs())));
                Assert.Equal(second, settings["FileDlgLastSaveDir"]);
                Assert.Equal(first, settings["FileDlgLastOpenDir"]);
                Assert.False(File.Exists(save.FileName));

                Assert.False(Show(new FileDialog(FileDialogMode.Open) { Opacity = 0 }, first, dialog => dialog.DialogResult = false));
                Assert.False(Show(new FileDialog(FileDialogMode.Save) { Opacity = 0 }, second, dialog => dialog.DialogResult = false));
                Assert.False(Show(new FileDialog { InitialDirectory = second, Opacity = 0 }, second, dialog => dialog.DialogResult = false));
                Assert.Equal(first, settings["FileDlgLastOpenDir"]);
                Assert.Equal(second, settings["FileDlgLastSaveDir"]);

                var invalid = new FileDialog { InitialDirectory = second, FileName = "absent.txt", Opacity = 0 };
                Assert.False(Show(invalid, second, dialog =>
                {
                    Call(dialog, "OK_Click", dialog, new RoutedEventArgs());
                    Assert.True(dialog.IsVisible);
                    Assert.Equal(first, settings["FileDlgLastOpenDir"]);
                    dialog.DialogResult = false;
                }));
                var invalidSave = new FileDialog(FileDialogMode.Save) { FileName = Path.Combine(missing, "new.txt"), Opacity = 0 };
                Assert.False(Show(invalidSave, second, dialog =>
                {
                    ((TextBox)dialog.FindName("FileNameBox")).Text = Path.Combine(missing, "new.txt");
                    Call(dialog, "OK_Click", dialog, new RoutedEventArgs());
                    Assert.True(dialog.IsVisible);
                    Assert.Equal(second, settings["FileDlgLastSaveDir"]);
                    dialog.DialogResult = false;
                }));
                settings["FileDlgLastOpenDir"] = missing;
                Assert.False(Show(new FileDialog { Opacity = 0 }, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dialog => dialog.DialogResult = false));
                Assert.Equal(missing, settings["FileDlgLastOpenDir"]);

            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (app != null)
                {
                    foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
                    app.Shutdown();
                }
                ThemeManager.GetSetting = originalRead;
                ThemeManager.SetSetting = originalWrite;
                string fixtureRoot = Path.GetFullPath(root);
                string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!fixtureRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The fixture path escaped the temporary directory.");
                if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The picker test did not finish.");
        if (failure != null) throw failure;
    }

    private static List<string> Pins() => (List<string>)typeof(FileDialog)
        .GetMethod("PinnedPaths", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

    private static FieldInfo Field(FileDialog dialog, string name) => dialog.GetType().GetField(name, Private)!;

    private static void Call(FileDialog dialog, string method, params object[] args) =>
        typeof(FileDialog).GetMethod(method, Private)!.Invoke(dialog, args);

    private static bool? Show(FileDialog dialog, string expectedDirectory, Action<FileDialog> action)
    {
        Exception? failure = null;
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                Assert.Equal(expectedDirectory, Field(dialog, "_currentDir").GetValue(dialog));
                action(dialog);
            }
            catch (Exception error)
            {
                failure = error;
                dialog.Close();
            }
        }));
        bool? result = dialog.ShowDialog(null);
        if (failure != null) throw failure;
        return result;
    }
}
