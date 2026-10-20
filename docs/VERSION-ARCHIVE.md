# Keeping every version on file

Every release gets its own folder on the maintainer's PC, so any past version can be found again.
GitHub keeps only the newest build (the rolling "Latest build" release is replaced on every push to main), and
git tags are not used for this.

## Where

`%USERPROFILE%\Pairnets\versions\<version>\`, for example `...\versions\1.0.60\`. Kept from 1.0.59 on; earlier
versions are not archived.

Each folder holds:

- the release files: the Windows installer and zip, the Linux and Mac apps, the server package, `get.sh`
- `SHA256SUMS.txt` and `SHA256SUMS.txt.sig` (the release signature) and `version.json`
- `source.zip`: the code of that exact commit
- `COMMIT.txt`: version, commit, date

About 400 MB per version.

## After every release

Once the release is published and its build is on GitHub, run:

```
powershell -ExecutionPolicy Bypass -File %USERPROFILE%\Pairnets\versions\archive-version.ps1
```

(add `-Version 1.0.60` to double-check the number, or `-Tag v1.0.60` for a tagged release).

The script downloads the release, checks the signature and every checksum, snapshots the commit with `git archive`,
and writes the folder. It stops without writing anything if a check fails, and never overwrites an existing
version folder. It only reads the repo and GitHub.

The script itself is not in this repo (it lives next to the archive and uses the private-side key folder's
public key only).
