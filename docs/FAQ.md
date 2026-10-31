# Questions and troubleshooting

## Everyday use

### An app stopped connecting after I installed SimpleDeFence. What do I do?

That is SimpleDeFence doing its job: anything you have not allowed is blocked. Open the window,
go to **Connections**, find the app under *Blocked*, and click **Allow**. The dialog lets you
choose how much to allow and for how long.

If you would rather not see this, turn on **Settings > General > Notify when an app is blocked**.
A tray notification then tells you the first time each app is blocked, and clicking it opens
Connections at that app.

### I am setting up a new PC and do not want to allow apps one at a time.

Switch to **Learning** mode from the tray or the mode menu, and pick a time limit. While it runs,
every program that connects gets a rule. When the time is up, SimpleDeFence goes back to
**Normal** by itself and shows how many rules were added, with a button to review them.

### An app updates itself into a new folder each time and I keep having to allow it again.

Allow it by publisher. In the Allow dialog, choose **Every program from <publisher>**. This
appears when the program is signed and Windows verifies the signature. Every program signed by
that publisher can then connect, including after updates. You can remove trusted publishers at
the bottom of the **Rules** page.

### I want to allow something only for a few minutes.

The Allow dialog and the Rules page both ask how long a rule should last: from 5 minutes up to
24 hours, until the next restart, or permanently. A timed rule removes itself.

### How do I block everything right now?

Press **Ctrl+Alt+B**, or choose **Block all** from the tray. Press Ctrl+Alt+B again to go back
to Normal. Global hotkeys can be turned off in Settings.

### Can I script it?

Yes. From an elevated prompt:

```
SimpleDeFence.exe /mode normal|blockall|allowoutgoing|disabled
SimpleDeFence.exe /mode learning /minutes 30
SimpleDeFence.exe /allow "C:\path\to\app.exe"
```

The exit code is 0 on success, 1 when the service refused or could not be reached, and 2 for a
command line it did not understand. A password-locked configuration refuses these commands.

### What does "Lock down on public networks" do?

When Windows says you are on a **Public** network (the choice it offers the first time you join a
network, such as café or hotel Wi-Fi), SimpleDeFence ignores "Allow local network" and turns off
file and printer sharing, network discovery, ping, Remote Assistance and Remote Desktop. When you
are back on a Private network, they come back by themselves. Your rules are not changed.

### Where is an app trying to connect?

On the **Connections** page, right-click the app and choose **Look up addresses** (or click
**Show all**, then **Look up addresses**). Each address gets its name, the company that owns it
and the country where its network is registered. That country is where the network is registered,
not necessarily where the server is.

## Troubleshooting

### Nothing can connect at all.

1. Check the mode in the tray menu. **Block all** blocks everything by design.
2. If you set a password, the configuration may be locked; that does not change the mode, but it
   stops changes until you unlock it.
3. Open **Connections** and look under *Blocked*: if the program is listed, allow it.
4. If the window says it cannot reach the service, restart the *SimpleDeFence* service in
   `services.msc`, or restart the PC.

### Windows Update, the Microsoft Store or the clock stopped working.

Open **Rules** and check the *Special exceptions* section. Windows Update, Store updates, time
sync, DNS and DHCP are there and are on by default; turn back on any you turned off.

### Network shares, printers or Remote Desktop do not work at home.

Turn on **Settings > Protection > Allow local network**, or the matching special exception on the
Rules page. If **Lock down on public networks** is on, also check that Windows has your home
network set to **Private** (Settings > Network & internet > your connection > Network profile type).

### Where are the logs?

In `C:\ProgramData\SimpleDeFence`. `service.log` is the service, `client.log` the window. Attach
them to an issue if you report a problem; they contain program paths but no passwords.

### I forgot the password.

The password protects the configuration from anything running on the PC, including programs
running as you, so there is no recovery from inside the app. Uninstalling SimpleDeFence,
deleting `C:\ProgramData\SimpleDeFence`, and installing it again resets it, along with your rules.

### How do I remove SimpleDeFence completely?

Uninstall it from **Settings > Apps**. The service removes its filters from the Windows Filtering
Platform when it stops, so nothing is left blocking traffic afterwards.

## About the project

### How is this different from the Windows firewall on its own?

Out of the box, Windows Defender Firewall lets every program make outgoing connections and only
filters incoming ones. SimpleDeFence blocks outgoing connections from anything you have not
allowed, and gives you one place to see what was blocked and allow it. It uses the same Windows
Filtering Platform underneath, with no driver of its own.

### Does it send anything about me anywhere?

No. The only connection it makes on its own is the update check (to this GitHub repository), and
that can be turned off in **Settings > Updates**. **Look up addresses** on the Connections page
runs only when you click it, and sends just those addresses to your DNS server and to
[rdap.org](https://rdap.org), the public registry lookup.

### Why does it need administrator rights?

Programming the Windows Filtering Platform requires them. The window asks for elevation when it
starts; the firewall itself runs in a background service and keeps working with the window
closed.
