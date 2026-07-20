<br />
<div align="center">
  <h3 align="center">SimpleDeFence</h3>

  <p align="center">
    A free, lightweight and non-intrusive firewall
    <br />
    Fork of <a href="https://tinywall.pados.hu">TinyWall</a>, being modernized with a Rust core and a Tauri/Tailwind GUI.
  </p>
</div>

## About

SimpleDeFence is a free, lightweight, and non-intrusive, secure by default firewall for Windows. Built to just simply sit in your system tray, quietly blocking any application you did not explicitly allow network access. It installs no kernel drivers, so it cannot negatively influence system stability. It also respects your privacy and collects absolutely no data about the user or their computer.

This project is a fork of [TinyWall](https://github.com/pylorak/TinyWall) by Károly Pados, currently a straight rebuild of the original C#/.NET codebase under a new name. See [NOTICE.md](NOTICE.md) for attribution details and [ROADMAP.md](ROADMAP.md) for where the project is headed, including a planned Tauri + Tailwind CSS GUI that talks to the existing C# service.

## How to build

### Necessary tools

- Microsoft Visual Studio 2026 (or 2022)
- [Wix v3.14 Toolset](https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm)
- [Visual Studio extension for Wix v3 Toolset](https://marketplace.visualstudio.com/items?itemName=WixToolset.WixToolsetVisualStudio2022Extension)

Alternatively, see [Dockerfile](Dockerfile) for a containerized build environment (Windows containers required) that only needs Docker installed.

### To build the application

1. Open the solution file in Visual Studio and compile the `SimpleDeFence` project. The other projects referenced inside the solution need not be built separately as they will be statically compiled into the application.
1. Done.

### To update/build build the database of known applications

1. Adjust the individual JSON files in the `SimpleDeFence\Database` folder.
1. Start the application with the `/develtool` flag.
1. Use the `Database creator` tab to create one combined database file in JSON format. The output file will be called `profiles.json`.
1. To use the new database in debug builds, copy the output file to the `SimpleDeFence\bin\Debug` folder.
1. Done.

### To build the installer

1. Copy the compiled application files and all dependencies into the `MsiSetup\Sources\ProgramFiles\SimpleDeFence` folder.
1. Update the files as necessary inside the `MsiSetup\Sources\CommonAppData\SimpleDeFence` folder. See instructions above about creating the database.
1. Open the solution file in Visual Studio and compile the `MsiSetup` project.
1. Done.

## Contributing

Feel free to open issues, feature- or pull-requests.
1. Fork the Project
1. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
1. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
1. Push to the Branch (`git push origin feature/AmazingFeature`)
1. Open a Pull Request on GitHub

## License

| Contents in                     | Maintainer   | Origin                                                                                                                                | License                  |
|---------------------------------|--------------|---------------------------------------------------------------------------------------------------------------------------------------|--------------------------|
| Microsoft.Samples\TaskDialog\   | KevinGre     | [link](https://www.codeproject.com/Articles/17026/TaskDialog-for-WinForms)  ([archive.org](https://web.archive.org/web/20250211033156/https://www.codeproject.com/Articles/17026/TaskDialog-for-WinForms))                                                          | Public Domain            |
| Microsoft.Samples\Privilege.cs  | Mark Novak   | [link](https://learn.microsoft.com/en-us/archive/msdn-magazine/2005/march/using-net-making-privileges-reliable-secure-and-efficient)  | see Privilege.cs.LICENSE |
| DarkModeCS.cs                   | BlueMystic   | [link](https://github.com/BlueMystical/Dark-Mode-Forms)                                                                               | MIT                      |
| Everything else                 | Károly Pados | [this repo](https://github.com/pylorak/TinyWall)                                                                                      | GPLv3                    |

## Contact

GitHub: <https://github.com/fcoltro/SimpleDeFence>

Upstream project (TinyWall) by Károly Pados: <https://github.com/pylorak/TinyWall>
