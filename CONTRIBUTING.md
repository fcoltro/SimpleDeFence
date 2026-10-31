# Contributing to SimpleDeFence

Thank you for helping. Issues, ideas and pull requests are all welcome.

## Reporting a problem

Open an issue with:

- what you did, what you expected, and what happened instead;
- your Windows version and the SimpleDeFence version (Settings > About);
- `service.log` and `client.log` from `C:\ProgramData\SimpleDeFence`, if the problem involves the
  firewall itself.

Security problems go through [SECURITY.md](SECURITY.md) instead of a public issue.

Please do not report SimpleDeFence issues to TinyWall or its authors; they are the origin of this
code, not participants in this project.

## Code changes

1. Fork the repository and create a branch from `main`.
2. Build and test as described in [README.md](README.md#how-to-build). CI builds with Visual
   Studio 2022 and 2026, in Debug and Release, and runs the tests; a pull request needs all of
   them green.
3. Keep a pull request to one change, and say in its description how you tested it. Anything
   that changes the window needs to have been run on Windows, not only built.
4. Match the code around you. This codebase explains *why* in its comments, especially where a
   choice protects the firewall from failing open; please keep that up in what you add.

Things worth knowing before you start:

- The GUI and the service are one executable, `SimpleDeFence/SimpleDeFence.csproj`, which compiles
  the library projects' sources directly. A new `.cs` file in `SimpleDeFence.Core`,
  `SimpleDeFence.Windows` or the others is picked up by a wildcard; no project edit is needed.
- Logic that can be tested without Windows (rule decisions, parsing, text) belongs in
  `SimpleDeFence.Core`, with tests in `SimpleDeFence.Tests`.
- Messages between the window and the service are records in `SimpleDeFence.Core/Message.cs`.
  A new field must have a default that means "as before", so an older window still works with a
  newer service.

## Translations

The interface is in sixteen languages. Strings live in
`SimpleDeFence.Core/Localization/Strings.<culture>.json`; English (`Strings.en.json`) is the
reference.

To fix or improve a translation, edit that language's file and open a pull request. To add a
string:

1. Add it to `Strings.en.json` under the right section.
2. Add a constant for its key to `SimpleDeFence.Core/Localization/LocKeys.cs`.
3. Add the same key to every other `Strings.*.json`. If you cannot translate it, copy the English
   text and say so in the pull request, so a speaker can fix it later.

`LocTests` fails the build when a key is missing from any language or has no constant. Keep
`{0}`-style placeholders exactly as they are in English; their order in the sentence may change.
The files are UTF-8 with a BOM, indented by two spaces.

To add a language, copy `Strings.en.json` to `Strings.<culture>.json` (for example
`Strings.nl.json`) and translate it. The file is embedded by a wildcard and the language list is
read from the embedded files, so nothing else needs registering. `LocTests` picks the new
language up the same way and holds it to the same checks.

## The database of known applications

`SimpleDeFence/Database/Applications/*.json` describes well-known apps, so that allowing one also
allows the helper programs it needs. Each entry matches a program by file name and by the exact
subject of its signing certificate; an entry with no certificate subject matches nothing.

`tools/Get-InstallerSigners.ps1` prints the certificate subject of each vendor's current
installer. After editing the files, rebuild `profiles.json` with the Database creator, as
described in [README.md](README.md#to-update-the-database-of-known-applications).
