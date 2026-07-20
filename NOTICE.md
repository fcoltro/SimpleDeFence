# Notice

SimpleDeFence is a fork of [TinyWall](https://github.com/pylorak/TinyWall), created and maintained
by Károly Pados. This repository was forked from TinyWall in July 2026 and renamed to comply with
the upstream project's request that forks distributing their own binaries use a name dissimilar to
"TinyWall" (see TinyWall's README, "Contributing" section).

At the point of forking, the codebase is a direct copy of upstream TinyWall — no functional rename
has been performed yet (see [ROADMAP.md](ROADMAP.md)). Internally the app still identifies as
TinyWall (service name, data folder, Windows Filtering Platform rule grouping, .NET namespaces)
until that work is done deliberately and verified.

## Licensing

Per upstream's [LICENSE.txt](LICENSE.txt):

| Contents in                     | Maintainer   | Origin                                                                                                                                | License                  |
|----------------------------------|--------------|-----------------------------------------------------------------------------------------------------------------------------------------|---------------------------|
| `Microsoft.Samples/TaskDialog/`  | KevinGre     | [CodeProject article](https://www.codeproject.com/Articles/17026/TaskDialog-for-WinForms)                                               | Public Domain             |
| `Microsoft.Samples/Privilege.cs` | Mark Novak   | [MSDN Magazine](https://learn.microsoft.com/en-us/archive/msdn-magazine/2005/march/using-net-making-privileges-reliable-secure-and-efficient) | see `Privilege.cs.LICENSE` |
| `DarkModeCS.cs`                  | BlueMystic   | [Dark-Mode-Forms](https://github.com/BlueMystical/Dark-Mode-Forms)                                                                       | MIT                       |
| Everything else                  | Károly Pados | [pylorak/TinyWall](https://github.com/pylorak/TinyWall)                                                                                  | GPLv3                     |

All changes made in this fork are licensed under GPLv3, consistent with upstream. See
[LICENSE.txt](LICENSE.txt) for the full text.
