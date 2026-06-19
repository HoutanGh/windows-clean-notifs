# AGENTS.md

## Goal
Help me ship the Local Windows Notification Dashboard with correct, maintainable changes and clear explanations.

## Product source of truth
- Treat `docs/product-spec.md` as the product source of truth.
- V1 is a **generic Windows 11 notification viewer** with app-level source filtering.
- Discord is only the primary use case. Do not add Discord-specific parsing, channel/server logic, AI filtering, replies, click-through actions, or native-notification suppression unless explicitly requested.
- Keep the app local-only: no accounts, cloud sync, telemetry, analytics, or external API calls.

## Non-negotiables
- Do not refactor unrelated code.
- Keep Windows-specific notification capture isolated from storage, API, and UI logic.
- The React frontend must use the local API; it must not attempt to access Windows notification APIs directly.
- Bind the server to loopback only. Do not expose notification data to the LAN.
- For disabled sources, store source metadata only—never notification title or body content.
- Do not claim notification capture works unless it has been verified on Windows 11.
- State assumptions and add TODOs where evidence is missing.
- Every change must include verification steps.

## Code quality bar
- No hacks, fake success paths, swallowed errors, brittle app-name matching, or tests weakened to pass.
- Prefer simple, maintainable design over premature abstraction.
- Preserve raw Windows text elements so parsing decisions remain reversible.
- Handle permission denial, unavailable Windows APIs, duplicate notifications, restart recovery, retention cleanup, and SSE disconnects explicitly.
- Do not log notification contents by default. Any debug-content logging must be clearly opt-in.
- Keep changes scoped. If missing infrastructure is required, add it cleanly or explain the blocker.
- Report anything uncertain, incomplete, platform-dependent, or not tested on Windows.

## Architecture defaults
Unless the repository already establishes a better equivalent, use:

```text
Windows UserNotificationListener
  → collector
  → application/domain services
  → SQLite
  → local HTTP API + Server-Sent Events
  → React + TypeScript UI
```

Defaults:
- Collector/backend: C#/.NET
- Storage: SQLite
- Live updates: Server-Sent Events
- Frontend: React + TypeScript + Vite, compiled and served locally
- Runtime entry point: `start.bat`

Do not replace this stack or introduce Electron, cloud services, or another database without a clear reason and explicit approval.

## Verification expectations
Use automated tests where practical, plus manual Windows 11 checks for OS integration.

At minimum, verify relevant changes against:
- notification permission granted and denied;
- a newly seen app appearing once in Sources;
- enabled-source notifications being stored and streamed;
- disabled-source content not being stored;
- deduplication and newest-first ordering;
- persistence across restart;
- 72-hour and 2,000-record retention rules;
- loopback-only network binding;
- browser live updates and reconnect behaviour.

Never describe a Linux/macOS-only test run as proof that Windows notification capture works.

## Repo and branch guardrails
- Do not assume a repository path or branch.
- Before committing or pushing, state and verify:
  - `pwd`
  - `git rev-parse --show-toplevel`
  - `git branch --show-current`
  - `git status --short`
- Do not force-push, amend, or rewrite history unless explicitly asked.
- Do not commit generated databases, notification contents, secrets, build output, or user-specific paths.

## Required response format
Skip this for ordinary discussion or simple questions. For implementation work, report:

1. What changed
2. Why
3. How to verify
4. Files changed or created
5. Confidence (`0.0–1.0`) and biggest unknown

## Default workflow
For non-trivial tasks:

1. **Decompose** the task.
2. **Inspect** the current repository and product spec.
3. **Implement** the smallest complete change.
4. **Verify** tests, failure cases, privacy, and Windows-specific assumptions.
5. **Report** results and remaining uncertainty.

Prioritise proving notification capture and source discovery before polishing the frontend.

## First-principles mode
When asked to research and break down a task, return:
- goal, constraints, and invariants;
- recommended design and common failure modes;
- verification plan;
- confidence and largest unknown.

## Prompt-engineer mode
When asked to generate a prompt, provide a copy-paste prompt containing:
- repository and product context;
- task and scope boundaries;
- required outputs;
- verification requirements;
- the workflow above.

## Git shortcut
When I say `acp`:

1. Verify and state the repository and branch.
2. Split relevant changes into separate groups when practical:
   - code/configuration/runtime;
   - tests/fixtures;
   - documentation/examples.
3. For each non-empty group, run a separate add/commit/push cycle:
   - stage only that group;
   - write a concise commit message;
   - `git commit -m "<message>"`;
   - `git push origin HEAD`.
4. Do not mix groups unless a clean split is technically impractical; explain why first.

When I say `acp: <commit message>`, use that message for a single commit. For multiple commits, use it as the base with a short category prefix or suffix.

Do not ask for a commit message when I say only `acp`. Do not include `acp` or `PR` in commit messages. If any step fails, stop and report the error. Never use `--amend` unless explicitly asked.
