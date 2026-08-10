using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

// ═══════════════════════════════════════════════════════════════════
//  WINDOWS SHELL INTEGRATION  -  associations, Open With, folder verbs
// ═══════════════════════════════════════════════════════════════════
// Three things this deliberately does NOT do.
//
// It never writes UserChoice. Since Windows 8 that key is protected by a signed hash and an
// app that forges it is either broken or about to be. The honest flow is: REGISTER the app,
// then hand the user to Settings and let them pick. Anything that claims to set itself as
// the default without asking is lying to you.
//
// It never takes the `open` verb on a type whose default action RUNS or MERGES something.
// .bat, .cmd and .reg get an Edit verb instead. Silently turning "double-click merges this
// into the registry" into "double-click opens a text editor" ends in a support call, and the
// reverse ends in something worse. .ps1 IS in the text list, because Windows already opens
// those in an editor on double-click by design - owning it changes nothing about what a
// double-click means, which is the whole test.
//
// And it never registers anything on its own. A portable exe that scribbles into HKCR is a
// portable exe that lies about being portable, so this runs only when the user asks for it,
// from the Associations card.
namespace KillerShell
{
    public partial class App
    {
        internal const string AssocProgId  = "KillerShell.Document";
        internal const string AssocAppName = "KillerShell";

        /// <summary>HKCU\Software\Classes, or HKLM\Software\Classes for a machine-wide install.</summary>
        private static RegistryKey RootFor(bool machine) =>
            machine ? Registry.LocalMachine : Registry.CurrentUser;

        // Types whose default action already IS "show me the text", so taking the open verb
        // changes what OPENS them, never what they DO.
        internal static readonly string[] TextExtensions =
        [
            ".txt", ".log", ".err", ".out", ".trace",
            ".ini", ".conf", ".cfg", ".inf", ".properties", ".env",
            ".yml", ".yaml", ".csv", ".tsv", ".md",
            ".ps1", ".psm1", ".psd1",
        ];

        // Types whose default action runs or merges. We only ever ADD a verb to these, hung
        // off the shell's own ProgID for the extension rather than off one of ours.
        internal static readonly (string Ext, string ShellProgId)[] EditOnlyExtensions =
        [
            (".bat", "batfile"),
            (".cmd", "cmdfile"),
            (".reg", "regfile"),
        ];

        /// <summary>Our own verb key name, used everywhere so removal can find them all.</summary>
        private const string VerbOpen = "KillerShell";
        private const string VerbTerm = "KillerShellTerminal";
        private const string VerbEdit = "KillerShellEdit";

        private static string ExePath =>
            System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;

        // The icon Explorer draws for our text types. It has to be a real file on disk:
        // DefaultIcon can only address a resource inside an exe BY INDEX, and index 0 is the
        // app icon, so a distinct file icon cannot be served from the exe itself. Written
        // beside the exe rather than into LocalAppData so an all-users install points every
        // account at one copy. Same arrangement as KillerPDF's pdf-file.ico.
        private static string FileIconPath =>
            Path.Combine(Path.GetDirectoryName(ExePath)!, "text-file.ico");

        /// <summary>
        /// Drops text-file.ico beside the exe. Called only from RegisterAssociations, i.e. only
        /// once the user has explicitly opted in - a portable copy writes nothing until then.
        /// Failure is not fatal: WriteProgId falls back to the app icon if the file is absent,
        /// which is what happens on a dev build with no embedded resource, or if the exe sits
        /// somewhere unwritable.
        /// </summary>
        private static void EnsureFileIcon()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var rn = Array.Find(asm.GetManifestResourceNames(),
                    n => n.IndexOf("text-file", StringComparison.OrdinalIgnoreCase) >= 0
                         && n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
                if (rn == null) return;

                using var rs = asm.GetManifestResourceStream(rn)!;
                using var ms = new MemoryStream();
                rs.CopyTo(ms);
                byte[] embedded = ms.ToArray();

                // NOT "if it already exists, leave it" (the bug: an icon dropped by an OLDER
                // build never got refreshed - RegisterAssociations only calls this once per
                // install/re-register, so upgrading past a rebranded icon left every text
                // association pointing at the file on disk from whenever it was first written,
                // however many versions ago that was, until the user unregistered and
                // re-registered by hand). Compare bytes and only rewrite when the embedded icon
                // actually differs, so a repeat call after this fix stays a no-op once the file
                // on disk matches what THIS build carries.
                if (File.Exists(FileIconPath))
                {
                    try
                    {
                        byte[] onDisk = File.ReadAllBytes(FileIconPath);
                        if (onDisk.Length == embedded.Length)
                        {
                            bool same = true;
                            for (int i = 0; i < onDisk.Length; i++)
                                if (onDisk[i] != embedded[i]) { same = false; break; }
                            if (same) return;
                        }
                    }
                    catch { /* fall through and try to overwrite */ }
                }

                File.WriteAllBytes(FileIconPath, embedded);
            }
            catch { }
        }

        /// <summary>
        /// True when our ProgID is present, which is the one key everything else hangs off.
        /// Cheap enough to call whenever the card opens.
        /// </summary>
        internal static bool AssociationsRegistered(bool machine)
        {
            try
            {
                using var k = RootFor(machine).OpenSubKey(@"Software\Classes\" + AssocProgId);
                return k != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Writes the ProgID, the OpenWithProgids hints, the Edit verbs, the folder verbs and
        /// the Capabilities block that puts KillerShell in Settings' Default apps list.
        /// Returns false if anything threw - which for HKLM means "not elevated".
        /// </summary>
        internal static bool RegisterAssociations(bool machine)
        {
            try
            {
                string exe = ExePath;
                var root = RootFor(machine);

                EnsureFileIcon();   // must land before WriteProgId, which tests for the file
                WriteProgId(root, exe);
                WriteOpenWithHints(root);
                WriteEditVerbs(root, exe);
                WriteFolderVerbs(root, exe);
                WriteCapabilities(root, exe);

                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            catch { return false; }
        }

        // One ProgID for every text type rather than one per extension. Windows 11 lists
        // defaults per extension anyway, so a ProgID each would buy nothing but 19 near
        // identical keys to keep in step.
        private static void WriteProgId(RegistryKey root, string exe)
        {
            using var p = root.CreateSubKey(@"Software\Classes\" + AssocProgId);
            p.SetValue("", "Text document");
            p.SetValue("FriendlyTypeName", "Text document");
            // Prefer the dedicated text-file icon; fall back to the app icon if it is not there.
            using (var i = p.CreateSubKey("DefaultIcon"))
                i.SetValue("", File.Exists(FileIconPath) ? "\"" + FileIconPath + "\",0" : exe + ",0");
            using (var c = p.CreateSubKey(@"shell\open\command")) c.SetValue("", "\"" + exe + "\" \"%1\"");
            using var s = p.CreateSubKey(@"shell\open"); s.SetValue("FriendlyAppName", AssocAppName);
        }

        // OpenWithProgids is the non-destructive half of this: it puts KillerShell in the
        // Open With list for each type WITHOUT touching whatever is currently the default.
        // The user promotes it in Settings if they want to; nothing here decides for them.
        private static void WriteOpenWithHints(RegistryKey root)
        {
            foreach (string ext in TextExtensions)
            {
                using var k = root.CreateSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids");
                k.SetValue(AssocProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
        }

        // The run-or-merge types. The verb hangs off the shell's OWN ProgID (batfile, regfile)
        // so the default action is untouched and this is purely an extra menu row.
        private static void WriteEditVerbs(RegistryKey root, string exe)
        {
            foreach (var (_, shellProgId) in EditOnlyExtensions)
            {
                using var v = root.CreateSubKey(@"Software\Classes\" + shellProgId + @"\shell\" + VerbEdit);
                v.SetValue("", "Edit with KillerShell");
                v.SetValue("Icon", exe + ",0");
                using var c = v.CreateSubKey("command");
                c.SetValue("", "\"" + exe + "\" \"%1\"");
            }
        }

        // Folder verbs. Windows has no "default file manager" setting and Explorer cannot be
        // displaced, so this is the whole of what a third-party browser can honestly claim:
        // a row on the right-click menu of a folder, of a drive, and of the empty space
        // inside one. Background takes %V rather than %1 - there is no item under the cursor
        // there, and %1 would hand us the literal string.
        private static void WriteFolderVerbs(RegistryKey root, string exe)
        {
            foreach (string owner in new[] { "Directory", "Drive" })
            {
                using var v = root.CreateSubKey(@"Software\Classes\" + owner + @"\shell\" + VerbOpen);
                v.SetValue("", "Open in KillerShell");
                v.SetValue("Icon", exe + ",0");
                using var c = v.CreateSubKey("command");
                c.SetValue("", "\"" + exe + "\" \"%1\"");
            }

            using (var v = root.CreateSubKey(@"Software\Classes\Directory\Background\shell\" + VerbOpen))
            {
                v.SetValue("", "Open in KillerShell");
                v.SetValue("Icon", exe + ",0");
                using var c = v.CreateSubKey("command");
                c.SetValue("", "\"" + exe + "\" \"%V\"");
            }

            // The shell verb is the cheap 90% of "default terminal". The real thing on
            // Windows 11 is a COM handoff server that receives every console session the OS
            // starts, which is a different product decision, not a bigger registry write.
            foreach (var (owner, token) in new[] { ("Directory", "%1"), ("Directory\\Background", "%V") })
            {
                using var v = root.CreateSubKey(@"Software\Classes\" + owner + @"\shell\" + VerbTerm);
                v.SetValue("", "Open KillerShell terminal here");
                v.SetValue("Icon", exe + ",0");
                using var c = v.CreateSubKey("command");
                c.SetValue("", "\"" + exe + "\" --shell pwsh --cwd \"" + token + "\"");
            }
        }

        // Capabilities + RegisteredApplications is what actually makes KillerShell appear in
        // Settings > Default apps as a thing you can pick. Without it the ProgID above is
        // reachable only through Open With.
        private static void WriteCapabilities(RegistryKey root, string exe)
        {
            using (var cap = root.CreateSubKey(@"Software\KillerShell\Capabilities"))
            {
                cap.SetValue("ApplicationName", AssocAppName);
                cap.SetValue("ApplicationDescription",
                    "A shell for power users: file browser, terminal and text editor.");
                cap.SetValue("ApplicationIcon", exe + ",0");

                using var fa = cap.CreateSubKey("FileAssociations");
                foreach (string ext in TextExtensions) fa.SetValue(ext, AssocProgId);
            }

            using var reg = root.CreateSubKey(@"Software\RegisteredApplications");
            reg.SetValue(AssocAppName, @"Software\KillerShell\Capabilities");
        }

        /// <summary>
        /// Takes it all back out. Called from the Associations card and from both uninstall
        /// paths, because a ProgID pointing at a deleted exe is how you get a broken Open With
        /// list that nothing in the UI will ever offer to clean up.
        /// </summary>
        internal static bool UnregisterAssociations(bool machine)
        {
            var root = RootFor(machine);
            bool ok = true;

            ok &= Kill(root, @"Software\Classes\" + AssocProgId);

            foreach (string ext in TextExtensions)
            {
                try
                {
                    using var k = root.OpenSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids", writable: true);
                    k?.DeleteValue(AssocProgId, throwOnMissingValue: false);
                }
                catch { ok = false; }
            }

            foreach (var (_, shellProgId) in EditOnlyExtensions)
                ok &= Kill(root, @"Software\Classes\" + shellProgId + @"\shell\" + VerbEdit);

            foreach (string owner in new[] { "Directory", "Drive", @"Directory\Background" })
            {
                ok &= Kill(root, @"Software\Classes\" + owner + @"\shell\" + VerbOpen);
                ok &= Kill(root, @"Software\Classes\" + owner + @"\shell\" + VerbTerm);
            }

            ok &= Kill(root, @"Software\KillerShell\Capabilities");

            try
            {
                using var reg = root.OpenSubKey(@"Software\RegisteredApplications", writable: true);
                reg?.DeleteValue(AssocAppName, throwOnMissingValue: false);
            }
            catch { ok = false; }

            try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); } catch { }
            return ok;
        }

        /// <summary>Delete a subtree, treating "was not there" as success.</summary>
        private static bool Kill(RegistryKey root, string path)
        {
            try { root.DeleteSubKeyTree(path, throwOnMissingSubKey: false); return true; }
            catch { return false; }
        }
    }
}
