# Let Claude set Tether up for you

Prefer not to type the commands yourself? Copy one of the prompts below into Claude and it will do
the install with you, step by step.

* **On the Ubuntu server**, use **Claude Code** (it can run the commands). Install it on the server,
  start `claude` in a terminal, and paste the server prompt.
* **On a Windows PC, Mac or Linux laptop**, use Claude Code the same way, or paste the client prompt
  into the Claude app. The app cannot click for you, but it will walk you through every screen.

Claude asks before running commands that change your system. Read what it proposes before you say
yes. These prompts never ask Claude to put your token anywhere except the Tether settings window
and the server's own config file.

The repository is private, so a computer that is not signed in to GitHub cannot download the
release. The prompts cover this: Claude either uses the GitHub CLI (`gh`) if you are signed in, or
asks you to download the files in your browser and copy them over.

---

## Prompt 1: install the server (paste into Claude Code on the Ubuntu server)

```text
Please install the Tether sync server on this Ubuntu machine for me. Tether is in the GitHub repo
MRnigth/Tether; its install guide is docs/HOWTO.md (sections 2-4) and the installer is
deploy/install.sh inside the release file tether-server-linux-x64.tar.gz.

Work step by step, explain each step in one sentence before you do it, and stop and ask me if
anything is unexpected. Do this:

1. Check that this is Ubuntu on x86_64 and that I have sudo.
2. Tailscale: check whether `tailscale` is installed and logged in (`tailscale status`,
   `tailscale ip -4`). If not, install it with the official script from tailscale.com, run
   `sudo tailscale up`, show me the login link, and wait until I say I have signed in.
3. Get the release files into a new temporary folder:
   - If `gh` is installed and `gh auth status` succeeds, run
     `gh release download latest --repo MRnigth/Tether -p tether-server-linux-x64.tar.gz -p SHA256SUMS.txt`.
   - Otherwise tell me to download tether-server-linux-x64.tar.gz and SHA256SUMS.txt from
     https://github.com/MRnigth/Tether/releases/tag/latest in my browser and copy them here with
     `scp`, and give me the exact scp command for this machine (user name and Tailscale IP).
     Wait until the files are there.
4. Verify them with `sha256sum --check --ignore-missing SHA256SUMS.txt`. If it does not say OK,
   stop and tell me; do not install.
5. Unpack the tarball and run `sudo ./install.sh` from the unpacked folder. Do not use
   --bind 0.0.0.0 and do not open any firewall ports to the internet; the server must only be
   reachable over Tailscale.
6. Check that it works: `systemctl is-active tether-server` and
   `curl -fsS http://<tailscale-ip>:5075/api/health`.
7. Show me the Server URL and the token that install.sh printed, and tell me to save them in my
   password manager. Do not write the token into any other file, log or note.
8. Finally, explain in plain words how to restrict access to my own PCs with the Tailscale ACL from
   docs/HOWTO.md section 4 (deploy/tailscale-acl.hujson in the release), and what the next step is
   on my computers.
```

---

## Prompt 2: install the app on a computer (Windows, Mac or Linux)

Have the **Server URL** and **token** from the server step ready. Do this on each computer; each one
needs its own device name.

```text
Please help me install the Tether sync app on this computer and connect it to my Tether server.
Tether is in the GitHub repo MRnigth/Tether; the guide is docs/HOWTO.md sections 2, 5 and 6.

Explain each step in one sentence before doing it, and ask me before anything that changes my
system. Do this:

1. Find out which operating system this is (Windows, macOS Apple silicon / Intel, or Linux x86_64).
2. Make sure Tailscale is installed and signed in with the same account as my server
   (tailscale.com/download), and that `tailscale ping <server name>` or a ping to the server's
   100.x.y.z address works.
3. Get the right file from https://github.com/MRnigth/Tether/releases/tag/latest (the repo is
   private: use `gh release download latest --repo MRnigth/Tether -p <file>` if gh is signed in,
   otherwise ask me to download it in my browser):
   - Windows: TetherSetup.exe. When Windows says "Windows protected your PC", click
     More info -> Run anyway (the app is not code-signed).
   - Mac: Tether-macos-arm64.dmg (Apple silicon) or Tether-macos-x64.dmg (Intel). Drag Tether to
     Applications, then right-click it -> Open the first time.
   - Linux: tether-desktop-linux-x64.tar.gz, unpack it and run ./install-desktop.sh as my normal
     user (not sudo).
   Also download SHA256SUMS.txt and check the file's SHA-256 against it before running it
   (Windows: `Get-FileHash <file> -Algorithm SHA256`; Mac: `shasum -a 256 <file>`;
   Linux: `sha256sum --check --ignore-missing SHA256SUMS.txt`). Stop if it does not match.
4. Start Tether. The "Welcome to Tether" window opens. Tell me what to type in each field:
   Server address = the URL from my server, Token = my token (I will paste it myself; do not ask
   me to show it to you), Folder = the folder I want to keep in sync, Device name = a name that is
   different on each of my computers. Then: Test connection (it must turn green), then
   Start syncing, and turn on "Start Tether when I sign in".
5. If this is my second computer and the folder already has files, explain the merge message
   Tether shows (nothing is deleted; files that differ become conflict copies) before I continue.
6. Tell me how to check it is working (the green check in the tray / menu bar, and the Activity
   list in the Tether window).
```

---

If something goes wrong, the "When something needs your attention" section of
[the guide](HOWTO.md#9-when-something-needs-your-attention) covers the usual cases, and you can paste
the error message into the same Claude conversation.
