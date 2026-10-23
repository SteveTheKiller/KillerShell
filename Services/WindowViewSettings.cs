using System;
using System.Globalization;
using System.Security.Principal;
using System.Threading;

namespace KillerShell.Services
{
    internal sealed class WindowViewSettings : IDisposable
    {
        private readonly Mutex _reservation;
        private readonly Func<string, string?> _read;
        private readonly Action<string, string> _write;
        private readonly bool _legacyFallback;
        private readonly string _prefix;
        internal int Slot { get; }

        internal WindowViewSettings(string role, Func<string, string?> read, Action<string, string> write)
        {
            _read = read;
            _write = write;
            string user;
            using (var identity = WindowsIdentity.GetCurrent()) user = identity.User?.Value ?? Environment.UserName;
            for (int slot = 0; ; slot++)
            {
                // Keeping the named handle alive reserves a slot across processes and windows.
                // No ownership is taken, so two windows on one UI thread cannot reenter it.
                var reservation = new Mutex(false, @"Local\KillerShell.WindowView." + user + "." + role + "." + slot,
                    out bool created);
                if (!created) { reservation.Dispose(); continue; }
                _reservation = reservation;
                Slot = slot;
                _prefix = "WindowView." + role + "." + slot.ToString(CultureInfo.InvariantCulture) + ".";
                _legacyFallback = slot == 0 && role == "normal";
                break;
            }
        }

        internal string Key(string key) => _prefix + key;
        internal string? Get(string key) => _read(Key(key)) ?? (_legacyFallback ? _read(key) : null);
        internal void Set(string key, string value) => _write(Key(key), value);

        internal void MigrateColumn(string scopedKey, string legacyKey)
        {
            if (_legacyFallback && _read(scopedKey) == null && _read(legacyKey) is { } value)
                _write(scopedKey, value);
        }

        public void Dispose() => _reservation.Dispose();
    }
}
