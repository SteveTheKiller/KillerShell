using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.ComponentModel;
using KillerShell.Services;

namespace KillerShell.Terminal
{
    internal sealed class TerminalProfileEntry : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        private string _name = string.Empty;
        public string Name { get => _name; set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Executable { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string StartFolder { get; set; } = string.Empty;
        public TerminalShellKind Kind { get; set; }
        public TerminalShellKind CustomKind { get; set; } = TerminalShellKind.Custom;
        public string Distribution { get; set; } = string.Empty;
        public bool Hidden { get; set; }
        public bool Elevated { get; set; }
        public override string ToString() => Name;
        public TerminalProfileEntry Copy() => (TerminalProfileEntry)MemberwiseClone();
        public TerminalProfile Resolve() => TerminalProfile.FromEntry(this);
    }

    internal static class TerminalProfileStore
    {
        internal static List<TerminalProfileEntry> Load()
        {
            var entries = Deserialize(ThemeManager.GetSetting("TerminalProfiles"));
            if (!entries.Any(p => p.Id == "pwsh")) entries.Insert(0, new() { Id = "pwsh", Name = "PowerShell", Kind = TerminalShellKind.PowerShell });
            if (!entries.Any(p => p.Id == "cmd")) entries.Add(new() { Id = "cmd", Name = "Command Prompt", Kind = TerminalShellKind.Cmd });
            var installed = WslDistributions.Installed();
            foreach (string distro in installed)
                if (!entries.Any(p => p.Id == "wsl:" + distro)) entries.Add(new() { Id = "wsl:" + distro, Name = distro, Kind = TerminalShellKind.Wsl, Distribution = distro, StartFolder = "~" });
            return [.. entries.Where(p => p.Kind != TerminalShellKind.Wsl || installed.Contains(p.Distribution, StringComparer.OrdinalIgnoreCase))];
        }

        internal static TerminalProfile Default()
        {
            var profiles = Load();
            string? id = ThemeManager.GetSetting("DefaultTerminalProfile");
            return (profiles.FirstOrDefault(p => p.Id == id && !p.Hidden) ?? profiles.First(p => p.Id == "pwsh")).Resolve();
        }

        internal static TerminalProfile ForFolder(string? folder)
            => folder != null && WslDistributions.TryParsePath(folder, out string distro, out _)
                ? TerminalProfile.Wsl(distro) : Default();

        internal static void Save(IEnumerable<TerminalProfileEntry> entries, string defaultId)
        {
            ThemeManager.SetSetting("TerminalProfiles", Serialize(entries));
            ThemeManager.SetSetting("DefaultTerminalProfile", defaultId);
        }

        internal static string Serialize(IEnumerable<TerminalProfileEntry> entries)
            => new XElement("profiles", entries.Select(p => new XElement("profile",
                new XAttribute("id", p.Id), new XAttribute("name", p.Name), new XAttribute("exe", p.Executable),
                new XAttribute("args", p.Arguments), new XAttribute("cwd", p.StartFolder), new XAttribute("kind", p.Kind),
                new XAttribute("distro", p.Distribution), new XAttribute("capability", p.CustomKind), new XAttribute("hidden", p.Hidden), new XAttribute("admin", p.Elevated)))).ToString(SaveOptions.DisableFormatting);

        internal static List<TerminalProfileEntry> Deserialize(string? xml)
        {
            var entries = new List<TerminalProfileEntry>();
            if (string.IsNullOrEmpty(xml)) return entries;
            try
            {
                foreach (var node in XElement.Parse(xml!).Elements("profile"))
                {
                    if (!Enum.TryParse((string?)node.Attribute("kind"), out TerminalShellKind kind) || !Enum.IsDefined(typeof(TerminalShellKind), kind)) continue;
                    string id = (string?)node.Attribute("id") ?? string.Empty;
                    if (id.Length == 0 || entries.Any(p => p.Id == id)) continue;
                    if (!Enum.TryParse((string?)node.Attribute("capability"), out TerminalShellKind capability)
                        || !Enum.IsDefined(typeof(TerminalShellKind), capability) || capability == TerminalShellKind.Wsl) capability = TerminalShellKind.Custom;
                    entries.Add(new() { Id = id, Name = (string?)node.Attribute("name") ?? string.Empty,
                        Executable = (string?)node.Attribute("exe") ?? string.Empty, Arguments = (string?)node.Attribute("args") ?? string.Empty,
                        StartFolder = (string?)node.Attribute("cwd") ?? string.Empty, Kind = kind, CustomKind = capability,
                        Distribution = (string?)node.Attribute("distro") ?? string.Empty,
                        Hidden = (bool?)node.Attribute("hidden") ?? false, Elevated = (bool?)node.Attribute("admin") ?? false });
                }
            }
            catch (System.Xml.XmlException) { }
            catch (FormatException) { }
            return entries;
        }
    }
}
