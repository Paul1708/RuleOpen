# Privacy Policy for RuleOpen

**Last updated:** 2026-11-23  
**Developer:** Paul Brinkmann  
**Repository:** https://github.com/Paul1708/RuleOpen

## Summary

**RuleOpen does not collect, store, transmit, or share any personal data.**

The application operates 100% locally and offline on your Windows device.

## Information Stored Locally on Your Device

All data used by RuleOpen remains exclusively on your computer under `%LocalAppData%\RuleOpen\`:

- **Configuration (`config.json`)**: Your opening rules (folder scopes, extensions, priorities, and target applications), fallback preferences, registered extension list, and UI settings.
- **Icon Cache (`Icons\`) & Explorer Route Table (`icon-routes.tsv`)**: Cached extension and application icons and the local folder-to-icon lookup table used by Windows File Explorer.
- **Diagnostic Logs (`Logs\`)** *(optional)*: Local rolling text logs for troubleshooting dispatch and launch failures (can be turned off at any time in **Settings → Diagnostics**).

## Network Access & Telemetry

RuleOpen contains **no telemetry, analytics, crash reporting SDKs, advertisements, accounts, or background cloud sync**. Outbound network connections are only initiated by Windows if you explicitly click an external link in **About** (such as opening the public GitHub repository in your web browser).

## Data Deletion

You remain in full control of your local files:
- Uninstalling the unpackaged installer asks whether to remove all local RuleOpen user data under `%LocalAppData%\RuleOpen\` or keep your rules for future reinstalls.
- You may also delete `%LocalAppData%\RuleOpen\` manually at any time.

## Contact

For privacy questions or security concerns, please open an issue or security advisory on GitHub at https://github.com/Paul1708/RuleOpen.
