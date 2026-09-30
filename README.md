# Dynamic DNS Updater for Jellyfin

Developed by **CrawTek**.

Unofficial Dynamic DNS plugin for Jellyfin. Requires Jellyfin 12.1.0 and .NET 10; other versions are not verified. Supports Dyn, No-IP, DuckDNS, Dynu and FreeDNS; arbitrary update servers are not supported.

## Install

1. Verify the release ZIP against SHA256SUMS and stop Jellyfin.
2. Extract all ZIP contents, including icon.png, into a new DynamicDNSUpdater_1.4.0.1 subdirectory of your Jellyfin plugins directory. For Docker use the persistent configuration volume. Give Jellyfin's service account read access to these files and write access to its configuration directory.
3. Start Jellyfin and open Dashboard > Plugins > Dynamic DNS Updater.
4. Select Dyn, No-IP, DuckDNS, Dynu or FreeDNS, then enter the hostname and credentials described below. Disable other updaters for that same hostname. Enable Dynamic DNS and save.
5. Select Check now and inspect the status for confirmation from your provider.

New installations start disabled with a blank hostname. This package does not import credentials or change OS services. Rotating a Dyn key may affect other updaters on that account.

See https://jellyfin.org/docs/general/server/plugins/ for plugin directory locations.

## Provider setup

**Dyn:** Enter your Dyn hostname, username and Dyn password. If your account uses an Updater Client Key for DDNS updates, enter that in the password field. Existing installations default to Dyn without changing their settings or credential file.

**No-IP:** Create a DDNS Key in your No-IP account, scoped to the one hostname you want to update. Select No-IP in the plugin, enter `all.ddnskey.com` as the hostname, and enter the generated DDNS Key username and password. That special target updates every hostname associated with the key, so check the key's scope carefully. Your public Jellyfin address remains your actual hostname, not `all.ddnskey.com`. An actual hostname with supported account credentials can also be entered, but a dedicated DDNS Key is recommended.

Free No-IP hostnames must still be confirmed every 30 days through No-IP. IP updates do not perform that confirmation. The plugin stops on credential, hostname, abuse, and unexpected errors until you resolve the issue and save settings with Dynamic DNS enabled. Saving resumes paused updates, including provider blocks and unexpected responses, while preserving the cooldown. Changing providers also retains cooldowns.

Current protocol references (checked September 30, 2026):
- [No-IP update requests](https://www.noip.com/integrate/request)
- [No-IP response codes](https://www.noip.com/integrate/response)
- [No-IP DDNS Keys](https://www.noip.com/support/knowledgebase/how-to-setup-and-use-a-ddns-key)
- [Free hostname confirmation](https://www.noip.com/support/knowledgebase/why-is-my-hostname-pending-deletion)

**DuckDNS:** Select its tile, enter yourname.duckdns.org (or just yourname), then enter the account token from DuckDNS. No username is required. Only one DuckDNS hostname is supported. The plugin sends IPv4 only and does not request an IPv6 or TXT change. DuckDNS requires the token in the HTTPS update URL; do not log outbound request URLs or enable tracing that captures them.

See [DuckDNS update protocol](https://www.duckdns.org/spec.jsp), checked September 30, 2026.

**Dynu:** Select Dynu, enter a primary hostname from DDNS Services, your Dynu username, and the separate IP update password from Manage Credentials. This release supports one primary hostname, not alias records or groups. Authentication uses an HTTPS Basic Authorization header. IPv6 updates are explicitly disabled. Dynu success responses may omit the address; a bare success acknowledges the IPv4 submitted in the request. Temporary failures retain the plugin's conservative 30-minute backoff and honor longer Retry-After values.

[Dynu protocol](https://www.dynu.com/DynamicDNS/IP-Update-Protocol), checked September 30, 2026.

**Live validation:** Dyn, No-IP, DuckDNS, Dynu and FreeDNS have been reported working on the test Jellyfin server.

## Behavior

Checks run every 10 minutes through Jellyfin Scheduled Tasks. The minimum interval between checks is also 10 minutes, including manual checks. Existing installations may retain a saved schedule: set Dashboard > Scheduled Tasks > Dynamic DNS Updater to every 10 minutes after upgrading from a testing build. The last confirmed address is saved to avoid repeated updates after restarts. Authentication, hostname, abuse and unexpected provider errors pause updates until resolved. Save settings with Dynamic DNS enabled to resume; this preserves cooldowns. Temporary failures and rate limits cause at least 30 minutes of backoff, extended when requested by the provider.

IPv4 only, one update target. Requires Jellyfin to remain running. Does not manage router forwarding, TLS certificates or bypass CGNAT. HTTPS connections use api.ipify.org for detection and members.dyndns.org (Dyn) or dynupdate.no-ip.com (No-IP), www.duckdns.org (DuckDNS), api.dynu.com (Dynu), or freedns.afraid.org (FreeDNS) for updates.

## Credentials

Settings and API require a Jellyfin administrator. The API never returns the saved key. Credentials are plaintext at rest in plugins/configurations/dyndns-updater/: updater.key for Dyn and noip-updater.key for No-IP, and duckdns-updater.key for DuckDNS, dynu-updater.key for Dynu, and freedns-updater.key for FreeDNS. Switching providers requires fresh credentials, including when switching back; saved files are never reused across providers. Unused provider credential files remain until removed. Unix directories use mode 0700 and files mode 0600. Protect backups; use HTTPS for remote administration. On Windows restrict the directory to Jellyfin's service account and administrators using ACLs. Windows installation is not verified.

Uninstall through Jellyfin or stop it and remove the plugin version directory. Settings and key remain: remove the dyndns-updater configuration directory separately to erase them. Re-enable a previous updater if needed.

## Build and repository packaging

Requires .NET SDK 10 and PowerShell 7. Run:

```powershell
./package.ps1
```

Builds the plugin, runs the offline engine and settings checks and creates a ZIP plus SHA256SUMS under release/. GitHub Actions does the same and uploads build artifacts without publishing them.

To generate a Jellyfin manifest, provide the actual public HTTPS ZIP URL:

```powershell
./package.ps1 -DownloadUrl 'https://YOUR-HOST/Dynamic-DNS-Updater-1.4.0.1.zip'
```

Upload that exact ZIP and generated manifest.json. Do not rebuild or rezip afterward: the manifest checksum must match. MD5 is used for Jellyfin compatibility, with SHA256SUMS supplied separately. Users add the hosted manifest URL in Dashboard > Plugins > Repositories, then install from Catalog and restart. Retain previous manifest versions when releasing updates.

Private migration scripts, credentials and development caches are excluded from the public source bundle.

## Official catalog and release status

https://repo.jellyfin.org/files/plugin/manifest.json is controlled by Jellyfin. This plugin has not been submitted, accepted or published there. Public source hosting and maintainer review remain necessary. A third-party repository is a supported alternative, not official approval.

Version 1.4.0.1 restores the production 10-minute interval. Provider live testing was completed on version 1.4.0.0 or earlier; this maintenance release is locally validated. Public catalog installation remains to be verified. No official Jellyfin or provider endorsement is claimed.

## License and references

GPL-3.0; see LICENSE.

- https://github.com/jellyfin/jellyfin-plugin-template
- https://jellyfin.org/posts/plugin-updates/
- https://help.dyn.com/perform-update.html
- https://help.dyn.com/return-codes.html

For the final publishing workflow and catalog icon URL, see PUBLISHING.md and publish-manifest.ps1.

## FreeDNS (afraid.org)

Create an IPv4 (A) record, then copy its standard **Direct URL** from the FreeDNS Dynamic DNS page. Enter the matching hostname and only the private key after `?` in the FreeDNS update key field. Full URLs, v2 tokens, and account passwords are not supported. No username is required. The key selects the record being updated; the hostname does not override it. An unchanged-address response confirms the keyed record, so verify the key/hostname pairing in your account. Credentials are stored separately in `freedns-updater.key`.

Uses HTTPS and the documented `address` override. Unchanged IPs are skipped; provider failures keep the existing cooldown/backoff rules. FreeDNS live updates were reported successful.

[FreeDNS update documentation](https://freedns.afraid.org/faq/help.php?help_id=1) · [Direct URL key instructions](https://dns.afraid.org/guide/dd-wrt/)
