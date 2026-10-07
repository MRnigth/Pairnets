# Branches

## Active
| Branch | Purpose | Merge? |
|--------|---------|--------|
| `main` | The product. Every push here is a release (see [README](README.md)). | — |
| `feature/sign-in` | Website + app sign-in, ported onto current `main`. | Merge when CI is green and reviewed. |
| `feature/animations` | "Moments that matter" animations, ported onto current `main`. | Merge when green. |
| `feature/main-window-scroll` | Slow the main-window list scroll. | Merge when green. |

## Why some old branches look "ahead" of `main` but aren't
Earlier in the project, `main`'s history was rewritten (and force-pushed) so that every commit is
authored by the repo owner with no other attribution. That rewrite gave the already-merged work new
commit hashes. The original feature branches still point at the *old* hashes, so `git log` shows
them as "ahead" of `main` even though their **content is already on `main`**. Confirm with
`git cherry main <branch>` — lines beginning `-` mean the patch is already present.

These are duplicates/merged and safe to delete:

| Branch | Was | Content on main? |
|--------|-----|------------------|
| `claude/admiring-gates-a6wvhk` | mirror of the original build session | ✅ |
| `claude/busy-bell-c97e79` | the Cloudflare-Tunnel / rename work | ✅ (equals `main`) |
| `claude/gracious-planck-wjvinh` | server-version / logo work | ✅ |
| `claude/stoic-newton-2kzdgo` | the sidebar-UI redesign | ✅ |
| `claude/gifted-edison-otu2ci` | an older variant of the animations work | superseded by `feature/animations` |

> Do **not** re-merge these; their content is already in `main` under different hashes, and merging
> would reintroduce the pre-rename names.

## Porting, not merging
The sign-in and animations work was originally built **before** the Tether→Pairnets rename and the
move to the Cloudflare Tunnel, so those branches conflict heavily with `main` and cannot be merged
directly. They are reapplied onto a fresh branch off `main` (renamed to `Pairnets.*`, with the
Tailscale-era pieces dropped). See [HANDOFFS.md](HANDOFFS.md).
