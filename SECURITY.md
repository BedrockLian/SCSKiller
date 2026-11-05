# Security policy

## Reporting a vulnerability

This is the community HoYo fork. Report fork-specific vulnerabilities to this repository's maintainer, separately from
upstream. Do not publish exploit details, credentials or game content in Issues.

Use **Security → Report a vulnerability** if private reporting is enabled. If that entry is absent, open an issue
requesting a private reporting channel without describing the vulnerability. Include the package version and source
commit from `build-info.json` when a private channel is available. This community fork does not promise an upstream
response time. Problems reproduced on unmodified upstream can also be reported through
[upstream's security policy](https://github.com/BlueHeisenberg/SCSKiller/security/policy).

## Scope

- The app, the `scskiller` command line and the native tools (`scskiller_warm.exe`, the recorder `d3d12.dll`).
- The recorder: anything it does in a game's process, and anything written outside SCSKiller's own folders or the folder
  of a game it's installed in.
- The updater and the update feed signature: a tampered feed or package the app accepts.
- The sign-in flow and the tokens the app stores.

The maintained fork target is the latest HoYo prerelease on `main`. HoYo readers remain experimental; see
[support and verification limits](docs/hoyo-readers.md). Upstream's stable support policy is maintained by upstream.
