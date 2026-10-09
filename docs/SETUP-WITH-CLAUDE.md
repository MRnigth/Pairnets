# Let Claude set Pairnets up for you

Prefer not to type the commands yourself? Copy one of the prompts below into Claude and it will do
the install with you, step by step.

* **On the Ubuntu server**, use **Claude Code** (it can run the commands). Install it on the server,
  start `claude` in a terminal, and paste the server prompt.
* **On a Windows PC, Mac or Linux laptop**, use Claude Code the same way, or paste the computer prompt
  into the Claude app. The app cannot click for you, but it will walk you through every screen.

Claude asks before running commands that change your system. Read what it proposes before you say
yes. The prompts keep your secrets with you: you paste the tunnel token into the installer yourself,
and you sign in on your nest's website yourself. Claude never needs your tunnel token, setup link,
password, passkey or sign-in code.

The repository is public, so every computer can download the release directly; no GitHub account is
needed.

---

## Prompt 1: install the server (paste into Claude Code on the Ubuntu server)

No domain of your own? You do not need this prompt: run the one-line install with a free name from
[HOWTO section 3](HOWTO.md#3-install-the-server) yourself (`--name alice`; it asks for a code sent to
your email, which only you can type). The prompt below is for your own domain.

First create the tunnel and its public hostname in the Cloudflare dashboard yourself
([HOWTO: your own domain](HOWTO.md#advanced-your-own-domain-and-tunnel)), and have the hostname ready. It becomes
your nest's name. Keep the tunnel token to yourself: you paste it into the installer, not into
Claude.

```text
Please install the Pairnets sync server on this Ubuntu machine for me. It is reached through a
Cloudflare Tunnel. Pairnets is in the public GitHub repo MRnigth/Pairnets; its install guide is
docs/HOWTO.md (sections 2-4) and the installer is install.sh inside the release file
pairnets-server-linux-x64.tar.gz. My tunnel's public hostname (my nest's name) is: <https://sync.example.com>

Work step by step, explain each step in one sentence before you do it, and stop and ask me if
anything is unexpected. Do this:

1. Check that this is Ubuntu on x86_64 and that I have sudo.
2. In a new temporary folder, download the release files:
   curl -LO https://github.com/MRnigth/Pairnets/releases/latest/download/pairnets-server-linux-x64.tar.gz
   curl -LO https://github.com/MRnigth/Pairnets/releases/latest/download/SHA256SUMS.txt
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
6. Remind me to open the one-time setup link that install.sh printed at the end (it works once,
   within 24 hours), in my browser, and to add a passkey or a password of at least 10 characters
   there. Do not ask me for the link and do not copy it anywhere. If I lost it, tell me to run
   `sudo -u pairnets /opt/pairnets/pairnets-server owner-link` myself and open the new link.
7. Tell me the next step on my computers (Prompt 2).
```

---

## Prompt 2: install the app on a computer (Windows, Mac or Linux)

Have your **nest's name** ready (the hostname from the server step), and set up the nest's website
first (step 6 above). Do this on each computer.

```text
Please help me install the Pairnets sync app on this computer and sign it in to my Pairnets nest
(my own server). Pairnets is in the public GitHub repo MRnigth/Pairnets; the guide is docs/HOWTO.md
sections 5 and 6. My nest's name is: <sync.example.com>

Explain each step in one sentence before doing it, and ask me before anything that changes my
system. Do this:

1. Find out which operating system this is (Windows, macOS Apple silicon / Intel, or Linux x86_64).
2. Check that https://<my nest's name>/api/health shows "ok" in a browser on this computer.
3. Get the right file from https://github.com/MRnigth/Pairnets/releases/latest:
   - Windows: PairnetsSetup.exe. When Windows says "Windows protected your PC", click
     More info -> Run anyway (the app is not code-signed).
   - Mac: Pairnets-macos-arm64.dmg (Apple silicon) or Pairnets-macos-x64.dmg (Intel). Drag Pairnets to
     Applications, then right-click it -> Open the first time.
   - Linux: pairnets-desktop-linux-x64.tar.gz, unpack it and run ./install-desktop.sh as my normal
     user (not sudo).
   Also download SHA256SUMS.txt and check the file's SHA-256 against it before running it
   (Windows: `Get-FileHash <file> -Algorithm SHA256`; Mac: `shasum -a 256 <file>`;
   Linux: `sha256sum --check --ignore-missing SHA256SUMS.txt`). Stop if it does not match.
4. Start Pairnets. The sign-in window opens. Tell me to type my nest's name and then choose
   "Continue with Google", "Continue with email", or the button that signs in with my browser.
   My browser opens my nest's website: I sign in there myself, check that the code on the website
   matches the one in Pairnets, and press Allow. Never ask me for my password, passkey, email link
   or the code.
5. When Pairnets asks for the folder, help me choose the folder I want to keep in sync, tick
   "Start Pairnets when I sign in" (or "when I log in"), and press Start syncing.
6. If this is my second computer and the folder already has files, explain the merge message
   Pairnets shows (nothing is deleted; files that differ become conflict copies) before I continue.
7. Tell me how to check it is working (the green check in the tray / menu bar, and the Activity
   list in the Pairnets window).
```

---

If something goes wrong, the "When something needs your attention" section of
[the guide](HOWTO.md#9-when-something-needs-your-attention) covers the usual cases, and you can paste
the error message into the same Claude conversation.
