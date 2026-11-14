# KillerPivot Changelog

## 1.0.2 (Unreleased)

- Validate the exact new Exchange connection, preserve older connections with KeepExisting, and report failed or unverified disconnects and cleanup.
- Preserve damaged tenant configuration, reject conflicting updates, and save atomically with recoverable previous copies.
- Keep one previous configuration backup and remove old per-save backups after a successful update.
- Normalize tenant keys, reject blank tenant names, and preserve existing TLS settings when enabling TLS 1.2.
