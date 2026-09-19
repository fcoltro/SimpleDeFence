# Getting SimpleDeFence in front of people

A working plan for the 1.0 launch and after. Nothing here has been posted — every draft below is
yours to send, edit or discard.

Two things worth deciding before any of it. **What SimpleDeFence is for** is easy to say badly and
hard to say well; the line that has tested best is *"blocks every application that you have not
allowed, without a kernel driver and without telemetry."* That is concrete, it is true, and it
distinguishes the product in one sentence. **Who it is for** is narrower than "Windows users": it is
people who already believe outbound filtering is worth having, and who currently either pay for a
suite they do not want or run nothing. Aim at them, not at everyone.

---

## Order of operations

Discoverability compounds, so the order matters more than the volume.

1. **Ship the release properly first.** A link to an empty Releases page converts nobody, and you
   only get one launch. Installer attached, notes written, hash published.
2. **Package managers, immediately after.** These keep paying out for years with no further effort,
   and they are where Windows users who like this category actually look. `winget` is the single
   highest-value item on this whole page.
3. **Directories.** AlternativeTo especially — it is how people find replacements for the thing
   they already resent paying for.
4. **Communities, last and slowly.** One post at a time, spaced out, each written for the specific
   room. A launch blitz across ten subreddits in one evening reads as spam and gets removed as
   spam.

---

## 1. Package managers

### winget — do this one

Microsoft's own package manager, on every Windows 11 machine. `winget install SimpleDeFence` is a
meaningfully lower barrier than "download an MSI from GitHub and click through SmartScreen".

Submission is a pull request to
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). Easiest route:

```powershell
winget install wingetcreate
wingetcreate new https://github.com/fcoltro/SimpleDeFence/releases/download/v1.0/SimpleDeFence_x64.msi
```

It reads the MSI, builds the manifest, and opens the PR for you. Expect a review round; the common
requests are a `ShortDescription` under 100 characters and an accurate `License` field (`GPL-3.0`).

Note the moderators do look closely at anything that touches networking or installs a service.
Having SECURITY.md, a real README and a signed-looking release history all help. Expect to be asked
what the service does.

### Chocolatey

Long-established, and popular with exactly the sysadmin-adjacent audience this appeals to.
Packaging is a `.nuspec` plus an install script. More moderation friction than winget, but the
audience overlap is good.

### Scoop

Lowest effort of the three — a JSON manifest in a bucket. Smaller audience, but it costs an hour.

**A note on SmartScreen.** Unsigned MSIs get a scary blue warning, and for a *firewall* that
warning costs more trust than for most software. An OV code-signing certificate is roughly
US$200–400/year; an EV one clears SmartScreen faster but costs more and needs a hardware token.
This is the single biggest conversion problem the project has, and worth revisiting if donations
ever cover it. Mentioning it openly on the release page ("unsigned, here is the SHA-256, here is
why") is the honest interim answer and is better received than silence.

---

## 2. Directories and listings

| Where | Why | Effort |
|---|---|---|
| [AlternativeTo](https://alternativeto.net) | List as an alternative to TinyWall, GlassWire, NetLimiter, ZoneAlarm, Comodo Firewall, Windows Defender Firewall. This is how people find you. | 30 min |
| [Awesome Windows](https://github.com/Awesome-Windows/Awesome) | PR to the Security section. High-traffic list. | 20 min |
| [awesome-privacy](https://github.com/pluja/awesome-privacy) | Fits the no-telemetry angle precisely. | 20 min |
| [Privacy Guides forum](https://discuss.privacyguides.net) | Their tools discussion, not a drive-by submission. Read the room first. | — |
| [Product Hunt](https://producthunt.com) | Worth one shot. Weekday launch, ~12:01am PT. Developer tools do moderately well; be present all day to answer. | 2 h |
| [Softpedia](https://softpedia.com) / MajorGeeks | Old-fashioned, still ranks in search for "free windows firewall". | 30 min |

---

## 3. Community posts

**Read each community's self-promotion rule before posting.** Several of these remove
first-person project posts outright, and a removal costs you that community permanently.

Space them out — one per few days, not all at once.

### Hacker News (Show HN)

Best single shot at technical reach. Post Tuesday–Thursday, around 9–11am ET. Title exactly:

```
Show HN: SimpleDeFence – a Windows firewall that blocks by default, no kernel driver
```

First comment, posted by you immediately after submitting:

> I maintain SimpleDeFence, a free GPLv3 firewall for Windows. It started as a fork of TinyWall and
> has since been rebuilt on .NET 10 with a WinUI 3 interface.
>
> The premise is whitelist-by-default outbound filtering: nothing an application does on the
> network is permitted unless you allowed that application. It drives the Windows Filtering
> Platform rather than replacing the Windows firewall, so there is no kernel driver and no way for
> it to blue-screen your machine. No telemetry — the only outbound request it makes is an update
> check that you can switch off.
>
> The thing I have spent most of the last few releases on is not filtering, it is honesty: making
> sure the interface can never report a state the service is not actually in. A whitelist firewall
> that silently stops enforcing, or that shows an empty "blocked" list because the log failed to
> load rather than because nothing was blocked, is worse than no firewall, because you trust it.
> Several releases went into tracking exactly that class of bug down.
>
> Happy to answer anything about the WFP side, the IPC design, or why it is a fork.

HN responds well to the specific technical claim and badly to marketing language. The "honesty"
angle above is your genuinely interesting story — lead with it.

### Reddit

Check each subreddit's rules; most require a flair and some ban self-promotion.

- **r/Windows11**, **r/Windows10** — largest general audience
- **r/privacy**, **r/privacytoolsIO** — lead with no-telemetry, no-driver
- **r/opensource** — lead with GPLv3 and the fork story
- **r/sysadmin** — only if you have something operational to say (the winget package, say)
- **r/selfhosted**, **r/software**, **r/pcmasterrace** — secondary

Draft, adjust per sub:

```
Title: SimpleDeFence 1.0 — a free, open source Windows firewall that blocks everything
       you haven't explicitly allowed

I've been working on SimpleDeFence, a free and open source (GPLv3) firewall for Windows, and
1.0 is out today.

It's a whitelist firewall: by default no application gets to the network, and you allow the
ones you want — from the tray, or from a live list showing what's connected, what's listening
and what just got blocked. One click to allow anything in that list.

- No kernel driver. It drives the Windows Filtering Platform, so it can't destabilise your system.
- No telemetry. The only thing it sends is an update check, which you can turn off.
- Password-lockable, so the config can't be changed without it.
- Sixteen languages.
- ~55 MB self-contained installer, Windows 10 2004+ / Windows 11.

It began as a fork of TinyWall and has been substantially rebuilt since — .NET 10, WinUI 3
interface, AES-GCM config encryption, PBKDF2 password storage.

Free, no paid tier, no upsell. GitHub: https://github.com/fcoltro/SimpleDeFence

Happy to answer questions.
```

### Elsewhere

- **Lobste.rs** — needs an invite; only if you have one. Tag `security`, `windows`.
- **Mastodon / Fediverse** — `#OpenSource #Windows #Privacy #InfoSec`. Small but friendly.
- **YouTube / tech press** — The Hated One, Techlore, and similar privacy channels cover tools like
  this. A short, non-pushy email with the one-line pitch and a link is reasonable outreach.

---

## 4. The repository itself

These are what convert a visitor into a user once a post sends them your way.

- [ ] **Set the homepage URL** to the latest release, so the repo sidebar has a download link
- [ ] **Add a social preview image** (Settings → General → Social preview, 1280×640) — this is what
      renders when the link is shared anywhere, and its absence costs real clicks
- [ ] **Screenshots in the README.** Currently there are none, and for a GUI application that is the
      biggest single omission. Connections screen and tray menu, light and dark.
- [ ] **Enable Discussions** for questions that are not bugs, so issues stay a bug tracker
- [ ] **Pin** a "Start here / FAQ" discussion
- [ ] Consider a short **GIF** of allowing a blocked app in one click — that is the product's best
      moment and it is invisible in text

---

## 5. Donations

All three channels are live via `.github/FUNDING.yml`: Ko-fi (`ko-fi.com/fcoltro`), Buy Me a
Coffee (`buymeacoffee.com/fcoltro`) and the GitHub Sponsor button.

One thing still needs you: **enrol in GitHub Sponsors** at <https://github.com/sponsors>. The
`github: fcoltro` entry does nothing until that account exists — the other two already resolve.

What actually works for a project like this, in rough order:

- **Ask once, quietly, where the value already landed.** The README's Support section and the
  release notes. Not a banner, not a nag in the application — an application that nags is one people
  uninstall, and for a firewall that is an actively worse security outcome.
- **Name what money buys.** "Donations go toward a code-signing certificate so Windows stops
  warning about the installer" is a concrete, sympathetic ask that people fund. "Support
  development" is not.
- **Say what stays free.** "GPLv3, no paid tier, no upsell" is worth repeating; it is why people
  give to projects like this.
- **Thank publicly** (with permission) in release notes.

Realistic expectation: a free Windows utility with a few thousand users might see US$20–100/month.
That is normal, and it is roughly certificate money — which is a genuinely useful thing to aim at
and worth saying out loud.

---

## 6. After launch

- **Answer every issue**, even briefly. Responsiveness is the main thing that distinguishes a
  project people adopt from one they try once.
- **Release on a rhythm.** A repo whose last commit is eight months old reads as abandoned, which
  for a security tool is disqualifying.
- **Write up the interesting bugs.** The pipe-truncation bug — where the firewall's blocked list was
  empty because a 176 KB reply silently lost its tail — is a genuinely good blog post, and posts
  like that do more durable good than any launch thread.
