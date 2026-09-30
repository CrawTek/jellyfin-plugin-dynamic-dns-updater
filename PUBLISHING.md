# Publishing 1.4.0.1

Developer: CrawTek. Suggested release tag: v1.4.0.1.

1. Put the contents of the source ZIP in a public source repository. It includes the GPL license, icon, tests and automated build workflow. Do not upload the private development folder wholesale.
2. Create a release and upload the plugin ZIP, corresponding source ZIP, SHA256SUMS, and icon.png from the publishing bundle. Include RELEASE-NOTES.md as the release description.
3. Once the final public HTTPS download and icon URLs are known, run publish-manifest.ps1 from the source project. Point ReleaseDirectory to the folder containing the exact plugin ZIP being published:

```powershell
./publish-manifest.ps1 -DownloadUrl 'https://YOUR-HOST/Dynamic-DNS-Updater-1.4.0.1.zip' -ImageUrl 'https://YOUR-HOST/icon.png' -ReleaseDirectory './release'
```

4. Host the generated manifest.json at a stable public HTTPS URL. It must return JSON directly without a login. Do not change the ZIP after generating this manifest.
5. Test that manifest in Jellyfin under Dashboard > Plugins > Repositories, install from Catalog and restart. Confirm version, icon, developer credit, saved settings, and a successful status for the selected provider. Test with only one updater for the hostname.

There is no live manifest in this bundle because hosting URLs have not been selected. The official Jellyfin catalog is maintained by Jellyfin; publishing your own manifest does not add the plugin to that catalog. Official inclusion remains subject to maintainer review.

For later releases, preserve previous compatible version entries instead of replacing the complete catalog with only the newest entry.
