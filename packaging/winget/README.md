# winget manifests

Ready to submit to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). They are kept
here so the package definition is versioned alongside the release it describes, rather than existing
only inside somebody else's pull request.

`InstallerSha256` is the SHA-256 of the exact MSI attached to the v1.0 release, and matches the
`DownloadHash` in `updates/update.json`. MSI builds are not reproducible, so if the release asset is
ever replaced, both have to be regenerated from the new file.

## Validating before submitting

```powershell
winget install wingetcreate
winget validate --manifest packaging\winget\manifests\f\fcoltro\SimpleDeFence\1.0.0
```

An end-to-end install test, which is what the reviewers will run:

```powershell
winget install --manifest packaging\winget\manifests\f\fcoltro\SimpleDeFence\1.0.0
```

## Submitting

```powershell
wingetcreate submit --token <github-pat> packaging\winget\manifests\f\fcoltro\SimpleDeFence\1.0.0
```

Or open the pull request by hand against `manifests/f/fcoltro/SimpleDeFence/1.0.0/` in
microsoft/winget-pkgs.

Two things the reviewers reliably ask about for this package. It installs a **service that filters
network traffic**, which gets a closer read than most submissions — SECURITY.md and the README are
what answer that. And the installer is **unsigned**, so expect a question about publisher identity;
the honest answer is that the hash is published in two places in this repository and verified by the
application's own updater before anything runs.

## For the next release

Once 1.0.0 is in winget, `.github/workflows/winget-release.yml` opens the "New version" pull request
on its own whenever a release is published, reading the hash and `ProductCode` from the release's
`SimpleDeFence_x64.msi`. It needs the `WINGET_TOKEN` secret (see the workflow's header), and the tag
must have all three version parts (`v1.2.0`, not `v1.2`).

To do it by hand instead, copy the latest version folder to the new version number, then update in
all three files: `PackageVersion`, `InstallerUrl`, `InstallerSha256`, `ReleaseDate`,
`ReleaseNotesUrl`, and `ProductCode` (the MSI mints a new one on every build).

## Version 1.0.1

`manifests/.../1.0.1/` is prepared but must **not** be submitted while the 1.0.0 pull request
(microsoft/winget-pkgs#437457) is still open — submit it once 1.0.0 has merged, as a separate
"New version" PR. `ProductCode` differs from 1.0.0 because the MSI mints a new one on every build; it
and `InstallerSha256` were read from the released v1.0.1 asset, and the hash matches
`updates/update.json`.
