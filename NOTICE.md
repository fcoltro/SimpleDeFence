# Notice

SimpleDeFence is a fork of [TinyWall](https://github.com/pylorak/TinyWall), created and maintained
by Károly Pados. This repository was forked from TinyWall in July 2026 and renamed to comply with
the upstream project's request that forks distributing their own binaries use a name dissimilar to
"TinyWall" (see TinyWall's README, "Contributing" section).

That is the origin of the code, not a description of its present state. The functional rename has
since been completed — the service, the `%ProgramData%` folder, the WFP rule grouping, the named
pipe and the .NET namespaces all identify as SimpleDeFence — and the code has diverged further
since (WinUI 3 in place of WinForms, .NET 10, reworked configuration and password protection). See
[ROADMAP.md](ROADMAP.md).

Károly Pados and TinyWall's other contributors are credited here as the authors of the original
work. They do not maintain or contribute to SimpleDeFence, and issues found here should not be
reported to them.

## Licensing

SimpleDeFence is licensed under the GPLv3, as TinyWall is. This table records where the code
came from and under what terms - a statement of origin and copyright, not a list of people who
work on this project.

| Contents in                     | Copyright / origin | Source                                                                                                                                | License                  |
|----------------------------------|--------------------|-----------------------------------------------------------------------------------------------------------------------------------------|---------------------------|
| `Microsoft.Samples/TaskDialog/`  | KevinGre     | [CodeProject article](https://www.codeproject.com/Articles/17026/TaskDialog-for-WinForms)                                               | Public Domain             |
| `Microsoft.Samples/Privilege.cs` | Mark Novak   | [MSDN Magazine](https://learn.microsoft.com/en-us/archive/msdn-magazine/2005/march/using-net-making-privileges-reliable-secure-and-efficient) | see `Privilege.cs.LICENSE` |
| Original code base               | Károly Pados | [pylorak/TinyWall](https://github.com/pylorak/TinyWall)                                                                                  | GPLv3                     |
| Changes made in this fork        | fcoltro      | [fcoltro/SimpleDeFence](https://github.com/fcoltro/SimpleDeFence)                                        | GPLv3                     |

All changes made in this fork are licensed under GPLv3, consistent with upstream. See
[LICENSE.txt](LICENSE.txt) for the full text.
