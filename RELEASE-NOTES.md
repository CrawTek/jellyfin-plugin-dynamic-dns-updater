# Dynamic DNS Updater 1.4.0.1

Developed by CrawTek for Jellyfin 12.1.0 / .NET 10.

- Labels the Dyn credential field as Dyn password, with guidance for accounts using an Updater Client Key.
- Restores the default scheduled check and minimum cooldown to 10 minutes.
- Supports Dyn, No-IP, DuckDNS, Dynu and FreeDNS (afraid.org).
- Includes provider tiles, separate provider credentials, and a status panel with green success and red failure indicators.
- Skips unchanged addresses and preserves cooldowns across restarts and settings changes.
- Saving settings with Dynamic DNS enabled resumes paused updates after an issue is resolved. No separate retry control is required.

Upgrading from a testing build: check Dashboard > Scheduled Tasks > Dynamic DNS Updater and change any saved one-minute trigger to 10 minutes. The plugin enforces a 10-minute minimum even if an older trigger remains.

Provider live testing was reported successful on previous builds. This release is validated with the offline test suite and a Release build; live installation of 1.4.0.1 and public catalog installation remain unverified.

IPv4 only; one hostname at a time. Settings and credentials are preserved on upgrade. Jellyfin must remain running for updates to occur.
