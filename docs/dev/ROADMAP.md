# Roadmap

## In flight
- **Website + app sign-in** (`feature/sign-in`) — per-computer keys, device pairing, the nest
  website, passkey/password/email/Google. See [HANDOFFS](HANDOFFS.md#sign-in).
- **"Moments that matter" animations** (`feature/animations`).
- **Slow the main-window list scroll** (`feature/main-window-scroll`).

## Planned / worthwhile next
- **Move to .NET 10.** .NET 8 reaches end of support on **2026-11-10**. Do this as its own task.
- **Sign-in extras not built yet:** a QR code to approve a new computer from a phone; `pairnets://`
  links; approving a join from inside another app; a phone app; installer prompts for SMTP/Google.

## Deliberately not in v1
These are left out on purpose; the code is structured so they can be added without a rewrite:
- syncing empty folders; file permissions/ACLs; rename/move detection (today: delete + create);
- delta transfers and resumable *downloads* (uploads already resume; downloads support HTTP Range);
- more than two PCs used at the same time; end-to-end encryption at rest on the server.

## UI ideas raised but not built
Conflict-resolver screen, deletion-review screen, a setup wizard, "Uploaded 120 files"-style
activity grouping, and a Mica/acrylic window backdrop on Windows.
