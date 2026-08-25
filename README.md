<div align="center">
  <img src="SimpleDeFence.Assets/simpledefence.png" alt="SimpleDeFence" width="128" height="128" />

  <h3>SimpleDeFence</h3>

  <p>
    A free, lightweight and non-intrusive firewall for Windows
    <br />
    Built on <a href="https://github.com/pylorak/TinyWall">TinyWall</a> as its original code base.
  </p>
</div>

## About

SimpleDeFence is a free, lightweight, secure-by-default firewall for Windows. It sits in the
notification area and quietly blocks any application you have not explicitly allowed onto the
network. It installs no kernel drivers, so it cannot destabilise the system, and it collects no
data about you or your machine.

Rather than replacing the Windows firewall, it drives it. Rules are applied through the Windows
Filtering Platform by a background service running as LocalSystem; the interface is a separate
process that talks to it over a named pipe. Nothing in the GUI has to run elevated for the
firewall to keep working, and closing the window does not stop protection.

### What it does

- **Five modes**, switchable from the tray or the window:
  - **Normal** — everything blocked except what you have allowed
  - **Block all** — nothing gets through
  - **Allow outgoing** — outbound traffic permitted, inbound still blocked
  - **Learning** — watches what runs and builds allow-rules as it goes
  - **Disabled** — the firewall stops filtering
- **Application rules** with a built-in database of known programs, so common software can be
  allowed without hunting for executables by hand.
- **A live connections view**, with a configurable auto-refresh interval and optional logging of
  connections to disk.
- **Password protection** — the running configuration can be locked so it cannot be changed
  without the password, including by anything running as you.
- **Hosts-file blocklists**, updated on request.
- **Global hotkeys** for allowing an executable, a running process or a visible window.
- **English and Brazilian Portuguese** interfaces.

### Current state

The project has diverged substantially from the code base it started out on:

| | |
|---|---|
| Interface | WinUI 3 on the Windows App SDK, replacing the original WinForms UI - light/dark aware, including theme-adaptive tray icons |
| Runtime | .NET 10, self-contained x64; no framework install required |
| Configuration at rest | AES-GCM with a per-installation key wrapped by DPAPI, replacing the previous CBC scheme |
| Password storage | PBKDF2-HMAC-SHA256, 600,000 iterations, in a versioned self-describing format that upgrades older records on next unlock |
| Packaging | A single self-contained MSI, built and published by CI on every push |

## How to build

### Necessary tools

- Microsoft Visual Studio 2026 (or 2022) with the .NET 10 SDK
- [WiX v3.14 Toolset](https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm) - only needed for the installer
- [Visual Studio extension for WiX v3 Toolset](https://marketplace.visualstudio.com/items?itemName=WixToolset.WixToolsetVisualStudio2022Extension)

### To build the application

1. Open the solution in Visual Studio and build the `SimpleDeFence` project. The other projects
   are compiled into it, so they need not be built separately.
1. Done.

### To build the installer

The MSI is built from a self-contained publish, and the file list is harvested from that publish
rather than maintained by hand.

1. Publish, from the repository root. The explicit `-o` matters: the project sets
   `AppendTargetFrameworkToOutputPath=false`, so a bare publish lands somewhere the installer does
   not look.
   ```
   dotnet publish SimpleDeFence/SimpleDeFence.csproj -c Release -r win-x64 -p:SelfContained=true ^
     -o SimpleDeFence/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish
   ```
1. Harvest that output into `Dependencies.wxs`, from the `MsiSetup` directory. This file is
   generated and deliberately not checked in; `MsiSetup.wixproj` fails with instructions if it is
   missing. The arguments are explained in `MsiSetup/HarvestPublishDir.xslt`.
   ```
   heat.exe dir "..\SimpleDeFence\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish" ^
     -platform x64 -cg PublishedDependencies -gg -scom -sreg -srd ^
     -dr INSTALLDIR -var var.PublishDir -t HarvestPublishDir.xslt -out Dependencies.wxs
   ```
1. Build `MsiSetup` for the **x64** platform. x86 also compiles, but produces a package that
   installs an x64-only payload into 32-bit Program Files.
1. Done - the result is `MsiSetup\bin\Release\SimpleDeFence_x64.msi`.

`.github/workflows/build.yml` runs exactly these steps, so it is the reference if any of the above
drifts.

### To update the database of known applications

1. Adjust the individual JSON files in the `SimpleDeFence\Database` folder.
1. Start the application with the `/develtool` flag.
1. Use the `Database creator` tab to produce one combined `profiles.json`.
1. To use it in debug builds, copy that file to `SimpleDeFence\bin\Debug`.
1. Done.

## Contributing

Feel free to open issues, feature- or pull-requests.
1. Fork the Project
1. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
1. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
1. Push to the Branch (`git push origin feature/AmazingFeature`)
1. Open a Pull Request on GitHub

Please do not report SimpleDeFence issues to TinyWall or its authors — they are the origin of this
code, not participants in this project.

## License

SimpleDeFence is licensed under the GPLv3; see [LICENSE.txt](LICENSE.txt). The table below records
where the code came from and under what terms — it is a statement of origin and copyright, not a
list of people who work on this project.

This fork diverged from TinyWall in 2023 and has merged upstream changes since, most recently in
June 2026. TinyWall's own repository and history remain at the link below.

| Contents in                     | Copyright / origin | Source                                                                                                                                | License                  |
|---------------------------------|--------------------|---------------------------------------------------------------------------------------------------------------------------------------|--------------------------|
| SimpleDeFence.Windows\Privilege.cs | Mark Novak   | [link](https://learn.microsoft.com/en-us/archive/msdn-magazine/2005/march/using-net-making-privileges-reliable-secure-and-efficient)  | see Privilege.cs.LICENSE |
| Original code base              | Károly Pados and the TinyWall contributors | [pylorak/TinyWall](https://github.com/pylorak/TinyWall)                                              | GPLv3                    |
| Changes made in this fork       | fcoltro      | [fcoltro/SimpleDeFence](https://github.com/fcoltro/SimpleDeFence)                                                                     | GPLv3                    |

## Contact

GitHub: <https://github.com/fcoltro/SimpleDeFence>

Original code base — TinyWall by Károly Pados: <https://github.com/pylorak/TinyWall>
