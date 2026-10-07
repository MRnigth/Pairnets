# Let Claude set Pairnets up for you

Prefer not to type the commands yourself? Copy one of the prompts below into Claude and it will do
the install with you, step by step.

* **On the Ubuntu server**, use **Claude Code** (it can run the commands). Install it on the server,
  start `claude` in a terminal, and paste the server prompt.
* **On a Windows PC, Mac or Linux laptop**, use Claude Code the same way, or paste the client prompt
  into the Claude app. The app cannot click for you, but it will walk you through every screen.

Claude asks before running commands that change your system. Read what it proposes before you say
yes. These prompts never ask Claude to put your token anywhere except the Pairnets settings window
and the server's own config file.

The repository is private, so a computer that is not signed in to GitHub cannot download the
release. The prompts cover this: Claude either uses the GitHub CLI (`gh`) if you are signed in, or
asks you to download the files in your browser and copy them over.

---

## Prompt 1: install the server (paste into Claude Code on the Ubuntu server)

First create the tunnel and its public hostname in the Cloudflare dashboard yourself
([HOWTO section 2](HOWTO.md#2-create-a-cloudflare-tunnel)), and have the hostname ready. Keep the
tunnel token to yourself: you paste it into the installer, not into Claude.

```text
Please install the Pairnets sync server on this Ubuntu machine for me. It is reached through a
Cloudflare Tunnel. Pairnets is in the GitHub repo MRnigth/Pairnets; its install guide is
docs/HOWTO.md (sections 2-4) and the installer is deploy/install.sh inside the release file
pairnets-server-linux-x64.tar.gz. My tunnel's public hostname is: <https://sync.example.com>

Work step by step, explain each step in one sentence before you do it, and stop and ask me if
anything is unexpected. Do this:

1. Check that this is Ubuntu on x86_64 and that I have sudo.
2. Get the release files into a new temporary folder:
   - If `gh` is installed and `gh auth status` succeeds, run
     `gh release download latest --repo MRnigth/Pairnets -p pairnets-server-linux-x64.tar.gz -p SHA256SUMS.txt`.
   - Otherwise tell me to download pairnets-server-linux-x64.tar.gz and SHA256SUMS.txt from
     https://github.com/MRnigth/Pairnets/releases/tag/latest in my browser and copy them here with
     `scp`, and give me the exact scp command for this machine. Wait until the files are there.
3. Verify them with `sha256sum --check --ignore-missing SHA256SUMS.txt`. If it does not say OK,
   stop and tell me; do not install.
4. Unpack the tarball. Then ask me to run this myself in a terminal on the server, from the
   unpacked folder, because it asks for the tunnel token and I do not want to share it with you:
   `sudo ./install.sh --public-url <my hostname>`.
   Never ask me for the tunnel token and never put it in a command, file or message. Do not open
   any firewall ports and do not use --bind.
5. When I say it is done, check: `systemctl is-active pairnets-server pairnets-tunnel` and
   `curl -fsS <my hostname>/api/health` (must print ok). If the health check fails, help me with
   the table "If the app cannot connect" in docs/HOWTO.md section 4.
6. Remind me to keep the Server URL and token that install.sh printed in my password manager (do
   not write the token into any other file, log or note), and tell me the next step on my
   computers (Prompt 2).
```

---

## Prompt 2: install the app on a computer (Windows, Mac or Linux)

Have the **Server URL** and **token** from the server step ready. Do this on each computer; each one
needs its own device name.

```text
Please help me install the Pairnets sync app on this computer and connect it to my Pairnets server.
Pairnets is in the GitHub repo MRnigth/Pairnets; the guide is docs/HOWTO.md sections 5 and 6.

Explain each step in one sentence before doing it, and ask me before anything that changes my
system. Do this:

1. Find out which operating system this is (Windows, macOS Apple silicon / Intel, or Linux x86_64).
2. Check that <server URL>api/health shows "ok" in a browser on this computer.
3. Get the right file from https://github.com/MRnigth/Pairnets/releases/tag/latest (the repo is
   private: use `gh release download latest --repo MRnigth/Pairnets -p <file>` if gh is signed in,
   otherwise ask me to download it in my browser):
   - Windows: PairnetsSetup.exe. When Windows says "Windows protected your PC", click
     More info -> Run anyway (the app is not code-signed).
   - Mac: Pairnets-macos-arm64.dmg (Apple silicon) or Pairnets-macos-x64.dmg (Intel). Drag Pairnets to
     Applications, then right-click it -> Open the first time.
   - Linux: pairnets-desktop-linux-x64.tar.gz, unpack it and run ./install-desktop.sh as my normal
     user (not sudo).
   Also download SHA256SUMS.txt and check the file's SHA-256 against it before running it
   (Windows: `Get-FileHash <file> -Algorithm SHA256`; Mac: `shasum -a 256 <file>`;
   Linux: `sha256sum --check --ignore-missing SHA256SUMS.txt`). Stop if it does not match.
4. Start Pairnets. The "Welcome to Pairnets" window opens. Tell me what to type in each field:
   Server address = the URL from my server, Token = my token (I will paste it myself; do not ask
   me to show it to you), Folder = the folder I want to keep in sync, Device name = a name that is
   different on each of my computers. Then: Test connection (it must turn green), then
   Start syncing, and turn on "Start Pairnets when I sign in".
5. If this is my second computer and the folder already has files, explain the merge message
   Pairnets shows (nothing is deleted; files that differ become conflict copies) before I continue.
6. Tell me how to check it is working (the green check in the tray / menu bar, and the Activity
   list in the Pairnets window).
```

---

If something goes wrong, the "When something needs your attention" section of
[the guide](HOWTO.md#9-when-something-needs-your-attention) covers the usual cases, and you can paste
the error message into the same Claude conversation.
