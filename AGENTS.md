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

## Efficient task execution

- Start with docs/WORKING-CONTEXT.md and live Git branch/status. Use docs/WORKFLOW.md for workflow details only when needed. Current user instructions override saved preferences.
- Keep context brief and current at task boundaries. Historical test results are not current evidence; actual receipts/logs stay under ignored build/.
- Search relevant src/tests/tools/docs paths and read the needed functions. Avoid repository-wide dumps, old prototypes and private/generated data.
- Define scope/acceptance internally with docs/TASK-TEMPLATE.md. Resolve routine choices; ask only for material missing information. Preserve existing user changes and styling.
- Prefer python tools/workflow.py check PROFILE for compact output. Read logs on failure/unresolved questions. last PROFILE checks source freshness only, not external dependencies.
- Never automatically skip tests based on receipts. Use focused checks during edits, mandatory full checks at commit/release boundaries, and rerun affected checks after changes. Existing safety/validation rules remain mandatory.
- Avoid unrelated cleanup, duplicate reports, repeated successful checks without cause and re-reading settled decisions.
- User currently prefers no routine status updates: give the required initial notice, surface blockers needing input, and finish concisely. A newer status request takes precedence.
- Final response normally gives outcome, meaningful validation and limitations with a report link; no repeated command logs or project history.
- Workflow optimization does not authorize commit, push, release, installation, model/account changes or new chats.
## Model recommendation at task boundaries

- For each new substantive task, before tools, recommend an available model and reasoning effort in one short Russian sentence with a reason. For a mixed task, add at most one line assigning its major phases. Use the request and known context; never run code/tests or scan the repository just to choose a model.
- Default heuristic: 6.1 Sol Medium for ordinary code/UI implementation; Sol High for concurrency, networking, QMP, storage/update safety or difficult debugging; Terra Medium for writing, screenshots and visual concepts; 6 Astra High for exceptionally uncertain architecture or stubborn complex reasoning. These are project heuristics, not measured rankings. Prefer the already adequate model over gratuitous switches.
- Notify once before a substantially harder phase, naming the reason and proposed model/effort. A normal test failure or environmental permission error alone does not justify escalation. Consider escalation after two materially different failed diagnoses or newly discovered data/ownership risks. Suggest a downgrade only at a substantial phase boundary, not repeatedly.
- Keep recommendations to at most two initial lines and changed-assessment notices. Do not repeat on routine follow-ups. No extra agents/API requests/catalog lookups solely for routing; obey applicable skill instructions. Details: docs/MODEL-ROUTING.md.
- The user switches the active model manually. Do not claim a switch, infer an unobservable current model, change settings, or block authorized work merely because advice was given. If the user requests a switch/wait, preserve a brief handoff and wait before dependent work.
## Chat choice and scoped handoffs

- Alongside the initial model recommendation, say where the new task belongs: here, a verified existing chat by exact title, or a new chat; give a brief reason within two short initial lines total. Continue related work here; prefer a new chat only for independent substantial scope, not routinely because history is long.
- User authorized linking related QemuM chats. Use docs/CHAT-ROUTING.md and the ignored local build/workflow/chat-links.json registry. Verify targets and read minimal recent context only when needed. Send compact decisions/results/blockers/handoffs, never full history or private VM data. No unrelated chats, broadcast, cyclic acknowledgments or background polling.
- Do not create chats without an explicit human request, start new development in another chat merely to sync context, or allow concurrent writers in the same files without an agreed owner/isolated worktree. ChatGPT discussion does not imply access to local files. Delivered context is not confirmed adoption/completion or automatic shared memory.