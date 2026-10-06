# How to install Tether, and how it works

This guide takes you from nothing to two PCs that keep one folder in sync, step by step. It then
explains in plain language what Tether does and what to do when something needs your attention.
Allow about 30 minutes.

> Prefer to have it done for you? [Set Tether up with Claude](SETUP-WITH-CLAUDE.md) has two
> copy-and-paste prompts: one for the server and one for each computer.

**Contents**

1. [What you need](#1-what-you-need)
2. [Put all three machines on Tailscale](#2-put-all-three-machines-on-tailscale)
3. [Install the server](#3-install-the-server)
4. [Lock the server down to your two PCs](#4-lock-the-server-down-to-your-two-pcs)
5. [Set up the first PC](#5-set-up-the-first-pc-for-example-the-desktop)
6. [Set up the second PC](#6-set-up-the-second-pc-for-example-the-laptop)
7. [Everyday use](#7-everyday-use)
8. [How it works](#8-how-it-works)
9. [When something needs your attention](#9-when-something-needs-your-attention)
10. [Updating and uninstalling](#10-updating-and-uninstalling)

---

## 1. What you need

| | |
|---|---|
| **A server** | Any always-on Ubuntu machine (22.04 or newer): a mini PC, an old laptop, a Raspberry Pi-class x64 box or a VM. It needs enough disk for your folder **plus history** (old versions are kept 30 days). Rule of thumb: twice the size of your folder. |
| **Your computers** | Windows 10/11 (64-bit), macOS 11 or newer (Apple Silicon or Intel), or a Linux desktop (64-bit, e.g. Ubuntu). Tether is built for using one at a time. |
| **A Tailscale account** | Free for personal use: <https://tailscale.com>. This is what connects the three machines privately, wherever they are. |
| **The Tether release** | From <https://github.com/MRnigth/Tether/releases/latest>, or let the one-line commands below fetch it for you. |

Nothing else: no cloud storage, no extra accounts, no .NET installation.

---

## 2. Put all three machines on Tailscale

Tailscale gives each machine a private address (it starts with `100.`) that only your own
devices can reach. Tether's server will listen **only** on that address, so it is never visible
on the internet or even on your home network.

**On the Ubuntu server**

```bash
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up            # opens a login link; sign in with your Tailscale account
tailscale ip -4              # prints the server's Tailscale address, e.g. 100.x.y.z
```

Write that address down; this guide calls it **`100.x.y.z`**.

**On each computer**

1. Install Tailscale from <https://tailscale.com/download>: the Windows installer, the Mac app
   (Mac App Store or the standalone download), or for Linux
   `curl -fsSL https://tailscale.com/install.sh | sh` followed by `sudo tailscale up`.
2. Log in with the same account (Tailscale icon → **Log in**).
3. Check that the computer can reach the server: run `ping 100.x.y.z` in *Command Prompt* (Windows)
   or *Terminal* (Mac/Linux).

> Tip: also enable Tailscale's "Run unattended" option on the PCs (Tailscale menu → Preferences)
> so it connects before you sign in to Windows.

---

## 3. Install the server

**The quick way: one command.** On the Ubuntu server:

```bash
curl -fsSL https://raw.githubusercontent.com/MRnigth/Tether/main/deploy/get.sh | sudo bash
```

It downloads the latest release, checks its SHA-256 checksum (it refuses a damaged or altered
download), and runs `install.sh`, which is described below. To pick the address yourself, add
options after `-s --`, e.g. `... | sudo bash -s -- --bind 100.x.y.z`.

**Or step by step**, if you prefer to see each file:

```bash
# 1. Download the server package and its checksums
cd ~
curl -LO https://github.com/MRnigth/Tether/releases/latest/download/tether-server-linux-x64.tar.gz
curl -LO https://github.com/MRnigth/Tether/releases/latest/download/SHA256SUMS.txt

# 2. Check the download is intact (must print "tether-server-linux-x64.tar.gz: OK")
sha256sum --check --ignore-missing SHA256SUMS.txt

# 3. Unpack and install
tar xzf tether-server-linux-x64.tar.gz
cd tether-server-linux-x64
sudo ./install.sh
```

`install.sh` does everything for you:

* finds the Tailscale address and makes the server listen **only** there (port 5075);
* creates a dedicated system user `tether` and the folders
  `/opt/tether` (program), `/var/lib/tether` (your data) and `/etc/tether` (settings);
* creates a random **token**, a long password that the PCs must present;
* installs and starts the background service `tether-server`, which also starts at boot.

At the end it prints something like:

```
Enter these settings in Tether on both PCs:
  Server URL:  http://100.x.y.z:5075/
  Token:       3f9c…(64 characters)…a1

The token is shown only this once. Store it in your password manager.
```

**Copy both into your password manager now.** You need them on both PCs. If you lose the token
you can read it again on the server with `sudo grep SYNC_TOKEN /etc/tether/tether.env`.

Check that the server is running:

```bash
sudo systemctl status tether-server           # should say "active (running)"
curl http://100.x.y.z:5075/api/health         # should print: ok
```

> If `install.sh` warns that it could not find the Tailscale address, Tailscale was not up yet.
> Run `sudo tailscale up`, then run `sudo ./install.sh` again (it is safe to repeat).

---

## 4. Lock the server down to your two PCs

Tailscale already keeps strangers out. This step also keeps *your other devices* (phone, TV,
work laptop…) away from Tether.

1. On any machine, run `tailscale status` and note the **names** of your two PCs (first column).
2. Open the Tailscale admin console → **Access controls**.
3. Add this rule, replacing the names and the address:

```hujson
{
  "hosts": { "tether-server": "100.x.y.z" },
  "acls": [
    { "action": "accept", "src": ["my-desktop", "my-laptop"], "dst": ["tether-server:5075"] }
    // keep your existing rules below
  ]
}
```

4. Save. From a device that is *not* one of the two PCs,
   `curl http://100.x.y.z:5075/api/health` should now time out.

> If your policy file still contains the default "allow everything" rule
> (`"src": ["*"], "dst": ["*:*"]`), that rule also lets every device reach Tether.
> Narrow it or remove it if you want this lock-down to apply.

---

## 5. Set up the first computer (for example the desktop)

### Install

**Windows**: download one of these from the
[latest release](https://github.com/MRnigth/Tether/releases/latest):

* [`TetherSetup.exe`](https://github.com/MRnigth/Tether/releases/download/latest/TetherSetup.exe): the installer. It installs for your user only (no admin rights) and adds
  Tether to the Start menu.
* [`tether-client-win-x64.zip`](https://github.com/MRnigth/Tether/releases/download/latest/tether-client-win-x64.zip): extract `Tether.exe` to a permanent place, for example
  `%LocalAppData%\Programs\Tether\`.

Windows may say "Windows protected your PC" because the program is not code-signed. Click
**More info → Run anyway**.

**Mac**: either run this in *Terminal* (it picks the right version for Apple Silicon or Intel,
puts Tether in Applications and starts it):

```bash
curl -fsSL https://raw.githubusercontent.com/MRnigth/Tether/main/deploy/get.sh | bash -s -- --mac
```

or download `Tether-macos-arm64.dmg` (Apple Silicon: M1/M2/M3/M4) or `Tether-macos-x64.dmg`
(Intel) from the release, open it and drag **Tether** to **Applications**. The app is not
notarized by Apple, so the first time macOS refuses to open it. Open it once with
**right-click → Open → Open**. On macOS 15 and later, go to **System Settings → Privacy & Security**
and click **Open Anyway**. The Terminal command above avoids this. On a Mac, Tether lives in the
**menu bar** (top right), not the Dock.

**Linux desktop** (Ubuntu and similar, 64-bit): run as your normal user, without sudo:

```bash
curl -fsSL https://raw.githubusercontent.com/MRnigth/Tether/main/deploy/get.sh | bash -s -- --desktop
```

It installs into your home folder, adds Tether to the app menu and starts it. For the best
experience also run `sudo apt install libsecret-tools libnotify-bin`. Tether then keeps the token
in your keyring and can show notifications. On GNOME the tray icon needs the *AppIndicator*
extension, which Ubuntu enables by default.

### First-run setup

When Tether starts for the first time, the **first-time setup** window opens. It looks the same on
Windows, Mac and Linux, and follows your system's light or dark mode:

![First-time setup window](images/settings-first-run-light.png)


| Field | What to enter |
|-------|---------------|
| **Server URL** | `http://100.x.y.z:5075/` exactly as printed by `install.sh` |
| **Token** | the token printed by `install.sh` |
| **Folder to sync** | click **Browse…** and pick the folder, e.g. `D:\Work`. It may already contain your files. |
| **Device name** | pre-filled with the computer name. Leave it, but **each PC needs a different name**. It appears in conflict file names. |
| **Extra ignore patterns** | optional, one per line, e.g. `*.bak` or `node_modules/`. Common junk is ignored already. |
| **Start Tether when I sign in / log in** | tick it so syncing starts automatically. |

1. Click **Test connection**. You should see *"Connected. Server and token are OK."*
   If not, see [section 9](#9-when-something-needs-your-attention).
2. Click **Start syncing**.

The first sync uploads everything in the folder. A 20 GB folder takes a while; you can keep
working meanwhile.

### The Tether window

After setup the Tether window opens:

![Tether window while syncing](images/main-window-syncing-light.png)

* **Update banner** (only when there is one): a new Tether version is available. See
  [Updates](#updates) below.
* **Status card** (top): a coloured badge with a symbol (✓ up to date, ↻ syncing, ⏸ paused,
  ! needs a decision, ✕ error, crossed-out cloud for offline), one line for the overall state,
  when it last synced, which folder it syncs, and how much space is left on the server (orange when
  less than 5 GB or 5 % is left).
* **Transfer card** (only while transferring; the arrow travels up while uploading and down while
  downloading, and the bars glide): how many files are being sent, the speed and the
  time left ("4.9 MB/s · about 4 min left"), one bar for the whole batch ("37 of 120 files · 412 MB
  of 1.30 GB"), and the files in progress right now, each with its own small bar. Tether sends up
  to four files at the same time. If new files appear while it syncs, the total grows straight away.
  "Limited to 5 MB/s" shows when you set a speed limit.
* **Activity**: everything that happened recently, newest first: green ↑ uploaded, blue ↓
  downloaded, red ✕ deleted, orange ⚠ conflict, red ! problem. Hover a row for the exact time.
* **Needs attention** (the number shows how many): conflict copies and files that cannot be
  synced, each with a **Show in folder** button.
* **Buttons**: Sync now, Pause/Resume, Open folder, Settings, Log.

**Waiting for the other computer.** When your other computer uploads a big batch (more than 100
files), this one waits and then downloads everything in one go instead of a few files at a time, so
the two don't fight over the connection. The window shows how far the other computer is:

![Tether waiting for the other computer (dark mode)](images/main-window-waiting-dark.png)

Your own changes still upload while it waits. It stops waiting by itself when the other computer is
done, goes quiet for 2 minutes, or after 30 minutes. **Download now anyway** stops waiting at once.

When Tether needs your decision (for example before deleting many files), the badge turns orange and
a button in the status card says what to do:

![Tether window waiting for a decision (dark mode)](images/main-window-blocked-dark.png)

Closing the window does **not** stop Tether. It keeps syncing in the background. Open the window
again from its icon:

* **Windows**: in the notification area next to the clock (click **^** if it is hidden). Left-click
  opens the window, right-click shows the menu.
* **Mac**: in the menu bar at the top right of the screen. Click it for the menu.
* **Linux**: in the system tray or top bar, depending on your desktop.

The icon colour always shows the state:

| Icon | Meaning |
|------|---------|
| 🟢 green | up to date |
| 🔵 blue | syncing (hover to see which file and how far) |
| ⚪ grey | offline: cannot reach the server, retrying by itself |
| 🟠 orange | waiting for your decision (see section 9) |
| 🔴 red | an error, or some files need attention |
| 🟡 yellow | paused by you |

## 6. Set up the second computer (for example the laptop)

Install Tether the same way and fill in the setup window with the **same server URL and token**,
a **different device name**, and the folder you want on this PC.

* **Empty folder** (most common): Tether downloads everything from the server.
* **A folder that already holds a copy** (for example you copied it with a USB drive): Tether
  explains before starting that it will **merge**:
  * identical files are left alone (nothing is transferred);
  * files present on only one side are copied to the other;
  * files with the same name but different content are both kept (yours becomes a
    *conflict copy*, see below);
  * **nothing is deleted** on either side.

Click **OK** to continue. When the icon turns green on both PCs, you are done.

---

## 7. Everyday use

You do not have to do anything. Just work in the folder on whichever PC you are using.

* **While you work on the desktop**, every change (new file, edit, rename, delete) reaches the
  server about 2 seconds after you save.
* **When you switch to the laptop**, Tether catches up as soon as the laptop starts (or wakes
  from sleep): new files, edits and deletions from the desktop appear. If both PCs are on, a
  change on one shows up on the other within a few seconds.
* **When you go back to the desktop**, it picks up what you did on the laptop. And so on.
* **Offline?** Keep working. Tether retries by itself and syncs everything when the connection is
  back. It never loses local changes while offline.

> Good habit: before shutting down a PC, glance at the icon. Green means everything reached the
> server. Blue means wait a moment.

### The icon menu (right-click on Windows, click on Mac)

| Item | What it does |
|------|--------------|
| **Open Tether** | opens the Tether window |
| **Sync now** | runs a complete check immediately |
| **Open folder** | opens the synced folder in Explorer |
| **Allow these deletions (N)…** | only shown when Tether blocked a large deletion (see section 9) |
| **Locate the sync folder… / Confirm this folder… / Re-link to this server…** | only shown when Tether stopped to ask you something (see section 9) |
| **Files needing attention…** | lists files that cannot be synced until renamed |
| **Settings…** | change server, token, folder, device name, ignore patterns |
| **View log** | opens today's log (kept 14 days in `%LocalAppData%\Tether\logs`) |
| **Pause syncing / Resume syncing** | temporarily stop syncing on this PC |
| **Start with Windows / Start at login** | start Tether automatically when you sign in |
| **Exit / Quit Tether** | quit Tether. Nothing syncs until you start it again. |

---

## 8. How it works

### The big picture

```
     DESKTOP                         SERVER (Ubuntu)                        LAPTOP
  ┌────────────┐                  ┌───────────────────┐                 ┌────────────┐
  │  D:\Work   │  ── upload ───▶  │ files/   (latest) │  ── download ─▶ │  D:\Work   │
  │            │  ◀── download ── │ history/ (old and │  ◀── upload ──  │            │
  │ Tether.exe │                  │   deleted, 30 d)  │                 │ Tether.exe │
  └────────────┘   "something     │ manifest (list of │   "something    └────────────┘
                    changed" ◀──  │  every file)      │  ──▶ changed"
                                  └───────────────────┘
          all traffic goes through Tailscale (encrypted) and needs the token
```

The two PCs never talk to each other directly. Each one only compares itself with the server.
The server is the meeting point and the safety net.

### Three fingerprints per file

For every file Tether computes a **fingerprint** of its content (a SHA-256 hash: two files have
the same fingerprint only if their bytes are identical). For each file it then looks at three
fingerprints:

* **Here**: the file on this PC now.
* **Server**: the file on the server now.
* **Last agreed**: what this PC and the server both had the last time this file was synced.

Comparing the three tells Tether exactly *who* changed the file, without trusting dates and
clocks:

| Situation | What Tether does |
|-----------|-------------------|
| Here = Server | nothing to do |
| Only **here** differs from "last agreed" | you changed it on this PC → **upload** |
| Only the **server** differs from "last agreed" | the other PC changed it → **download** |
| **Both** differ from "last agreed", and from each other | both PCs changed it → **conflict: keep both** |
| File deleted here, server unchanged | you deleted it → **delete it on the server** (kept in history) |
| File deleted on the server, unchanged here | the other PC deleted it → **move it to this PC's Recycle Bin** |
| Deleted on one side but **edited** on the other | the edit wins → the file comes back |

### Worked examples

**You edit `report.docx` on the desktop, then switch to the laptop.**
The desktop's "here" differs from "last agreed", so it uploads. The laptop sees that only the
server changed and downloads the new version. The old version is kept in the server's history.

**You edited `plan.xlsx` on the laptop while it was offline, and also on the desktop.**
When the laptop reconnects, both sides differ from "last agreed": a conflict. Tether does not
pick a winner. The server's version (the desktop's edit) keeps the name `plan.xlsx`, and the
laptop's version is saved next to it as

```
plan (conflict LAPTOP 2026-10-02 141509).xlsx
```

That copy is uploaded too, so both PCs end up with both files, and a notification tells you.
Open the two, combine or choose, and delete the one you no longer want.

**You delete `old-notes.txt` on the desktop.**
The server moves it into its history and the laptop moves its copy to the Recycle Bin. You can get
it back from either place for 30 days.

**You deleted a folder on the desktop, but on the laptop you had edited a file inside it.**
Edits beat deletions. The edited file is uploaded again, so it reappears on the desktop.

### How a change travels

1. You save a file. Windows tells Tether something changed; Tether waits about 2 seconds until the
   file is quiet (no half-written uploads).
2. Tether uploads it. The server stores it next to the previous version, which goes into
   `history/`, and records it in its list of files.
3. The server tells the other PC, if it is on, that something changed. It syncs within about a
   second. If it is off, it catches up the next time it starts.

Every few minutes Tether also does a full check anyway, in case Windows missed a change.

### Safety rules

Tether is built so that losing or silently overwriting your files should be impossible:

* **Nothing is overwritten blindly.** Every upload tells the server which version it is replacing.
  If the other PC changed the file in the meantime, the server refuses, and Tether makes a
  conflict copy instead.
* **Downloads are all-or-nothing.** A file is downloaded to a hidden `.tether-tmp` folder, checked,
  and only then swapped in. A broken connection never leaves a half-file behind.
* **Files still being written are left alone** (modified in the last 2 seconds, or locked by
  another program such as Word). Tether tries again shortly.
* **The server never really deletes anything.** Every replaced or deleted file goes to history:
  30 days, and always at least the last 5 versions of each file.
* **Big deletions need your OK.** If one sync would delete more than 20 % of your files, or more
  than 50 files, Tether stops and asks first. With few files in the folder, even one or two
  deletions can trigger this; that is deliberate.
* **The hidden `.tether-marker` file** in your folder proves it is the right folder. If it is
  missing, or the folder is suddenly empty (an unplugged drive, a wrong path), Tether stops and
  asks. It never reads that as "the user deleted everything".
* **A restored or replaced server is detected**, and Tether asks before syncing with it.

### What is not synced

* temporary and lock files: `~$*`, `*.tmp`, `*.temp`, `*.swp`, `*.part`, `*.crdownload`,
  `Thumbs.db`, `desktop.ini`, `.DS_Store`, `.git/` folders, plus your own extra patterns;
* empty folders (folders are created when a file needs them);
* shortcuts that are symbolic links or junctions;
* file permissions.

A renamed file is synced as "delete the old name, create the new name". The result is the same.

---

## 9. When something needs your attention

| You see | What it means and what to do |
|---------|------------------------------|
| **Grey icon, "Offline"** | This PC cannot reach the server. Check that Tailscale is connected (its icon) and that the server is on. On the server: `sudo systemctl status tether-server`. Tether keeps retrying; your changes are safe and sync when it is back. |
| **"The server rejected the token"** | The token in Settings is wrong, or was changed on the server. Open **Settings…**, paste the token, and click **Test connection**. |
| **"Deletions blocked"** (orange) | A sync would delete many files. Right-click → **Allow these deletions…** lists them, showing which PC loses what. If that is what you did, click **Yes** (it applies once). If not (wrong folder, unplugged drive), click **No** and investigate. Nothing has been deleted. |
| **"The folder is missing" / "no .tether-marker"** (orange) | The drive is unplugged, or you moved or renamed the folder. Plug the drive in and choose **Sync now**. If you moved the folder, choose **Locate the sync folder…** and point to its new place; Tether recognizes it by its marker and continues without resyncing. |
| **"This folder was already synced… Confirm"** | Tether finds a marker but no record of it on this PC (for example after reinstalling Windows). Choose **Confirm this folder…**. Tether then merges and deletes nothing. |
| **"The server went back in time" / "not the one this folder was synced with"** | The server was restored from a backup or reinstalled. Choose **Re-link to this server…**. Tether merges your folder with the server: nothing is deleted or overwritten, and differences become conflict copies. |
| **A file named `… (conflict PC date time) …`** | Both PCs changed the same file. Compare the two versions, keep what you want under the original name, and delete the conflict copy. |
| **"Name collision" / "File name not allowed"** | Two names differ only in upper/lower case (`Report.txt` and `report.txt`), a file and a folder share a name, or the name is not allowed on Windows. **Files needing attention…** lists them. Rename them and they sync. |

### Getting an old or deleted version back

On the server (the service must be stopped briefly):

```bash
sudo systemctl stop tether-server
# list stored versions of a file (path as inside your synced folder, with / separators)
sudo -u tether /opt/tether/tether-server history list "Projects/report.docx" --data-dir /var/lib/tether
# restore one of them, using the id from the first column
sudo -u tether /opt/tether/tether-server history restore "Projects/report.docx" 20261002T101500123Z-1a2b3c4d --data-dir /var/lib/tether
sudo systemctl start tether-server
```

The restored version becomes the current version, and both PCs download it on their next sync.
The version it replaced also goes to history, so a restore can be undone too.

Files deleted on the *other* PC are also in this PC's **Recycle Bin**.

### Changing the token

Do this if a PC is lost, or you think the token leaked:

```bash
NEW=$(openssl rand -hex 32)
sudo sed -i "s/^SYNC_TOKEN=.*/SYNC_TOKEN=$NEW/" /etc/tether/tether.env
sudo systemctl restart tether-server
echo "$NEW"
```

Enter the new token in **Settings…** on both PCs. If a PC was lost, also remove it from Tailscale
(admin console → Machines).

### Logs

* PC: right-click → **View log**.
* Server: `sudo journalctl -u tether-server -f`.

Neither ever contains your token.

---

## 10. Updating and uninstalling

### Updates

Tether checks for a new version 15 seconds after it starts and then once a day (turn this off in
**Settings → Updates and speed**; **Check now** checks right away). This needs the GitHub
repository to be public.

**Update a computer.** When a new version is out, a banner appears at the top of the window and you
get a notification.

* **Windows** (installed with `TetherSetup.exe`): click **Update now**. Tether downloads the new
  installer, checks it against `SHA256SUMS.txt`, installs it and starts again by itself, in about
  10 seconds. If the check fails, nothing is installed and you keep your current version. A copy
  run from the zip opens the download page instead.
* **Mac and Linux**: click **Download**; the download page opens. Replace the app (Mac) or run the
  one-line `--desktop` command again (Linux). The app is not signed, so it does not replace itself.

**Update the server.** When the server runs an older version than your app, Tether asks:

![Your server should be updated](images/server-update-light.png)

Click **Update server**. The server downloads the newest release, checks it, installs it and
restarts (about 30 seconds; syncing pauses briefly and your files stay as they are). **Don't ask
for this version** hides the question until the next version. Tick **From now on, update the server
automatically** (also in **Settings → Updates and speed**) and Tether updates the server by itself
whenever it finds it out of date, with a line in the Activity list and a notification.

**See the server's version.** The main window shows a **Server 1.0.58** pill next to the free space. It turns
orange when the server is older than your app. A server too old to say which version it runs shows
**Server: old version**. The same line is in **Settings → Updates and speed**. The button beside it opens the same window by hand: **Update
server…** when the server is older, **Check for update** when it is up to date. It works even if you chose
*Don't ask for this version*.

**Debug mode.** If updating the server does not work, turn on **Settings → Updates and speed → Debug
mode**. Tether then writes more detail to its log, and the update window gets a **Details** box: every
step the app takes and, at the end, the server's own update log and settings. **Copy details** puts it
on the clipboard so you can send it to whoever helps you. Turn it off again when you are done.

This works because the server installer (`install.sh`) also installs a small root-owned updater
the first time, so no password is needed later. How this stays safe is explained in
[DEPLOY.md](DEPLOY.md#4b-updating-the-server).

**One-time setup for older servers.** A server installed before this feature has no updater yet,
and nothing the app sends can give it one (the old server program has no way to get root rights).
Tether then shows "One-time setup" with this command; run it once on the server and every later
update is one click (or automatic):

```bash
curl -fsSL https://raw.githubusercontent.com/MRnigth/Tether/main/deploy/get.sh | sudo bash
```

Updates keep your token, address, settings and files.

### Speed settings

In **Settings → Updates and speed**:

* **Files at the same time** (1, 2, 4 or 8): 4 is fastest for most connections. Use 1 on a very slow
  or metered connection.
* **Speed limits**: limit uploads to the server and downloads from it, in MB/s. Files from your
  other computers always come through the server, so the download limit covers them too. Each
  computer has its own limits; all files in progress together stay under the limit.
* **Wait while my other computer uploads many files**: the batch wait described in section 5.

### Uninstalling

**Uninstall from a Mac**: menu-bar icon → **Quit Tether**, untick "Start at login" first if you
had it on, then delete **Tether** from Applications. The token is in the *Keychain Access* app
under "Tether".

**Uninstall from Linux**: quit Tether, then
`rm -rf ~/.local/opt/tether ~/.local/bin/tether ~/.local/share/applications/tether.desktop ~/.config/autostart/tether.desktop`.

**Uninstall from Windows**: right-click → **Exit**. Untick "Start with Windows" first if you had it
on. Then delete `Tether.exe`, or uninstall through *Settings → Apps* if you used the installer.
Your synced folder and its files stay where they are. To also remove Tether's settings and
state, delete `%AppData%\Tether` and `%LocalAppData%\Tether`.

**Uninstall the server**:

```bash
sudo systemctl disable --now tether-server
sudo rm /etc/systemd/system/tether-server.service && sudo systemctl daemon-reload
sudo rm -rf /opt/tether /etc/tether
# your files remain in /var/lib/tether until you delete that folder yourself
```

For backups, the technical design and the security model, see [DEPLOY.md](DEPLOY.md),
[ARCHITECTURE.md](ARCHITECTURE.md) and [SECURITY.md](SECURITY.md).
