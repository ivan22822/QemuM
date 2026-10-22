# QemuM update protocol 1

## Patch builds without a new GitHub Release

`BUILD-NUMBER` is an independent monotonically increasing integer within VERSION.
Both package.json and the schema-1 feed carry optional `build`; absence means 0
for existing installations. Version is compared first, build second. A future
alpha release still outranks every build of an older alpha. The updater displays
both values, refuses stale downgrades under its locks, and checks the package's
version, build and protocol against its feed before replacing application files.

The fixed channel is alpha.json on branch `codex/alpha-update-channel` in this
repository, fetched from raw.githubusercontent.com. Only this exact raw URL is
allowed. Patch ZIPs remain SHA-256 verified GitHub Release assets in this same
repository, under the matching version tag. No arbitrary download hosts are
accepted. Release discovery is retained for later full releases. A missing
channel (HTTP 404) falls back to Releases; corrupt metadata and network failures
stop the check without installing anything.

Build with `python tools/release.py --patch`, audit with
`python tools/audit_release.py --patch`, run unit/GUI and isolated installation
checks, then commit and push the source branch for CI. The explicit publication
tool `python tools/publish_patch.py --commit <full SHA> --publish` requires a clean
checkout and successful CI for that exact commit. It adds uniquely named immutable
package/feed/checksum assets to the existing release. It never creates a release,
changes a tag, merges main, force-pushes, or edits the original app ZIP/installer.
After verifying uploads it advances the JSON-only channel branch. It also refreshes
the existing release's mutable update.json so old updaters can find the patch;
the previous feed and checksum file are retained under unique archive names.
There is a short interval during this feed switch when legacy checks may fail;
retrying is safe. Future full releases must also publish their latest feed to the
channel; normal Releases discovery remains active as a compatibility safeguard.

Users of the released alpha.4 choose “Обновления” and explicitly reinstall the
same version once; their old updater ignores the additional build field safely.
Early local alpha.4 builds that cannot reinstall must run QemuM-Refresh.exe from
the existing release once. Thereafter newer same-version builds are shown as
updates. Checks/installations remain user-triggered: no background service or
silent termination of QEMU/manager is introduced. The FAQ documents this transition.
The channel relies on GitHub account/repository security and TLS, like the existing
release feed; SHA-256 protects integrity, not against a compromised publisher.

## Explicit reinstall of the current version

The updater offers «Переустановить текущую версию» when GitHub and the installed
version have the same number. This is opt-in. Download SHA-256 and package file
hashes are verified. An identical manifest is a no-op. A different same-version
package is prepared under staging; the prior directory is retained under
`repairs/<operation>/previous`, while active.json and data remain unchanged.
Both running.lock and update.lock are required. Durable repair.json records
the old/new manifest hashes before renaming. The launcher reconciles an interrupted
repair before startup, retains unknown files and fails closed, and restores the
prior build if the new frontend fails before readiness. A successful startup
archives the receipt; old application files remain available. This is a rename
and verification protocol, not an assertion about physical power loss.

Early locally installed alpha.4 updaters do not know this action. Download and
run `QemuM-Refresh.exe` from the release once, close the manager and explicitly
choose reinstall. It finds the default installation, fetches GitHub's package
and refreshes the protocol-1 launcher with an exact backup before repairing.
Custom locations use `--check-updates --install-root <directory>`.
Subsequent requests can use the normal Updates button. No Python or QEMU
replacement, guest termination, or VM-data migration is performed by repair.

The stable `QemuM.exe` launcher is a separate .NET Framework executable. It does
not import the frontend or depend on Python. Keep this contract when replacing
the UI, backend, language or framework. An arbitrary replacement that ignores
the contract is not compatible.

## Installation layout

- `QemuM.exe`: stable launcher and update UI.
- `active.json`: schema 1, `current` version, optional `previous`.
- `versions/<version>/`: immutable application files and `package.json`.
- `data/`: user configurations, logs, appearance and runtime state.
- `runtime/`: private dependencies, independent of application updates.
- `runtime.json`: Python executable and QEMU directory chosen at installation.
- `downloads/`, `staging/`, `health/`: updater-owned work files.

The GUI runs with `QEMUM_APP_DIR`, `QEMUM_DATA_DIR`, `QEMUM_INSTALL_DIR`,
`QEMUM_READY_FILE` and (when requested by the package) `QEMUM_PYTHON`.
The frontend writes its version to the fresh ready file only after its backend
and library are ready. It must keep the process alive for its entire UI lifetime.
Start the update UI with `<install>/QemuM.exe --check-updates`.

## Package

`package.json` contains `schema: 1`, `min_launcher_protocol: 1`,
`channel: "alpha"`, `version: "0.1.0-alpha.1"`, an EXE `entrypoint`,
`requires_python: true|false` and a `files` object mapping every relative file
name to its lowercase SHA-256. The manifest does not hash itself.
ZIP contains exactly these files plus `package.json`; no directory entries,
symlinks, duplicate/case-colliding paths or external paths.

`update.json` is a GitHub Release asset with schema, minimum protocol, channel,
version, package `url` and package `sha256`. Alpha versions use
`major.minor.patch-alpha.sequence`, compared numerically. The launcher reads
up to 100 recent releases in the fixed project and selects the greatest alpha
version. Stable releases are outside protocol 1's alpha channel.

Hashes detect corruption. Authenticity relies on HTTPS and the project's GitHub
release access: a compromised publisher could replace both package and manifest.
Protocol 1 does not claim package signatures or Authenticode signing.

## Transaction and recovery

The launcher holds `running.lock` for the entire frontend lifetime. Updates
require that lock and `update.lock`. They never terminate processes.
Download, validate and unpack into a unique staging directory; verify every file;
move into a new version directory; atomically replace `active.json`.
Existing version directories are never overwritten or automatically removed.
User data and QEMU dependencies are outside the package.

If the new frontend exits without readiness, verify and restore the previous
pointer. A frontend that hangs but remains alive is not killed or rolled back
automatically. Close it to allow the readiness failure to be handled.
Rollback restores application code, not user data. Future data migrations must
be backward compatible or implement their own verified backups/recovery before
signalling readiness. Do not silently introduce destructive migrations.

For an incompatible protocol, publish a new installer and a higher
`min_launcher_protocol`. Old launchers must refuse it with an explanation.
QEMU/Python are pinned dependencies, not automatically updated with the GUI.

The application package may include QemuM.Update.exe: a refreshed update UI
using the same protocol/core. It accepts --check-updates --install-root <root>.
The permanent launcher remains independent; no replacement of the running
launcher is needed for UI changes.
