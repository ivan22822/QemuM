# Development rules

- This repository is source code. The installed manager and its VM data are external. Never deploy, update, close, or manipulate that installation unless the user explicitly authorizes it.
- Never open, hash, modify, replace, move, or remove the original pre-QMP backup archive. Never import backup folders or synced ChatGPT project sources into Git.
- Keep VM configurations, disks, ISO images, logs, named parameter backups, custom logos, credentials, personal paths, runtime state, generated installers and compiled binaries out of tracked files.
- Use a separate `codex/<meaningful-feature-name>` branch for each change. Preserve existing history. Use descriptive commit messages explaining the actual change.
- Never force push, including `--force-with-lease`. Never merge into `main` without explicit user confirmation.
- Before the first push, show the exact commit contents, destination repository and branch, and wait for confirmation. Do not push automatically as part of building or testing.
- Build only under ignored `build/`; do not build an installer unless explicitly requested. A test build is not a deployment.
- Run `python tools/test.py` and `python tools/audit.py --staged` before committing. Windows GUI checks: `python tools/test.py --gui`. VM integration checks require `--qemu` and use only disposable paused TCG guests with private temporary disks.
- Never use the user's VM configuration, disk or ISO for tests; never start or stop existing VMs. Tests must clean up only processes and files they created.
- Keep launch logic separate from visual appearance. Opening or cancelling a dialog must not mutate VM configuration. Keep QMP identity checks, disk ownership checks, exact backups and optimistic file-hash checks intact.
- Prefer shared UI components and clear ownership/disposal. Do not add timer-based layout repair or self-rewriting code. Preserve native text editing, Unicode, quoting and cancellation.
- Do not copy machine-specific absolute paths into source or tests. Use relative project paths, `sys.executable`, environment discovery, and temporary test fixtures.
- Explain what changed, how it was checked, and any unverified limits. Do not claim GitHub write access merely because a public repository can be cloned.
