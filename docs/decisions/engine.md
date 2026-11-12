# Engine decisions (Core: Infrastructure, Legendary, Jobs)

One line of reasoning per decision. Source cites are Legendary 0.21.1 (`docs/research/legendary.md`).

## Process control
- **Job Object with KILL_ON_JOB_CLOSE + below-normal priority limit for every Legendary run**: kills the PyInstaller pair, DLManager and all workers on cancel or Glider crash; the priority limit is inherited by children, so downloads/hashing never starve the foreground.
- **Tree kill (`Process.Kill(entireProcessTree)`) before terminating the job**: also catches a child that escaped the job in the tiny window between `Start` and `AssignProcessToJobObject`; off Windows it is the only mechanism.
- **On normal exit the job is terminated before draining the pipes**: an orphan worker holding inherited pipe handles would otherwise stall the drain.
- **stdin is redirected and closed immediately**: a stray `input()` raises EOFError (classified) instead of hanging forever.
- **Working directory = the Legendary config dir**: never the game folder, whose open cwd handle would block move/delete.
- **One global lock for mutating commands (install/update/repair/verify/import/move/uninstall/auth)**: Legendary allows one installer (lock file, exits 0 when busy) and concurrent logins could race refresh-token rotation; `status`/`info`/`launch --json` stay concurrent so launching isn't blocked by a background verify.
- **config.ini is merge-edited (only `[Legendary]` disable_update_check / disable_update_notice / egl_sync), and only while no Legendary process is alive**: Legendary rewrites it on exit and renames it aside if it changed mid-run; `[Fortnite] install_tags` must survive or `verify` reports optional packs as missing.
- **`disable_update_check = true` only once version.json exists**: `status`/`auth` force the API fetch anyway (keeping SDL config current); before the first fetch, `get_sdl_data` would dereference missing API data (core.py:445).
- **`egl_sync = false` is written explicitly**: Glider owns EGL write-back.
- **Stall watchdog default 90 s; liveness = progress counters moving or a meaningful log line**: chunk-retry spam and identical progress blocks (what an offline download prints forever) don't count; resume keeps completed files.
- **stderr decoded with the ANSI code page (GetACP) and only Python-producible escapes undone**: `\uXXXX` for chars the code page can't encode, `\U` above the BMP; `\x` never (literal `D:\x86\` paths are common, Python only uses it for C1 controls).
- **Line pump = PipeReader + reused buffers + span parsing; only non-progress lines become strings**: verify prints a line per file and downloads five per second for hours.

## Output classification
- **Exit codes are ignored; each command needs its success marker** (`Finished installation process` / `Download size is 0`, `Verification finished successfully.`, `has been imported.`, `Finished.`, `Game has been uninstalled.`, `Successfully logged in as`, `User data deleted.`, valid JSON for status/info/launch).
- **Facts are accumulated over the whole run, not read from a bounded tail**: a write failure from minute 3 still counts after an hour of retry spam.
- **Write failures are counted from the manager's `Writing for … failed!` only; the worker line supplies the OS error**: every worker failure is followed by exactly one manager line (workers.py:320-327).
- **Write failures beat the success marker**: Legendary marks the game installed even when writes failed (manager.py:588-590).
- **Disk full / sharing violation / access denied are recognised in both repr and str forms** (`OSError(28, …)`, `[WinError 32]`, `Permission denied`).
- **SDL download failure aborts the run in every mode**: Legendary would fall back to a stale hardcoded tag table (core.py:449-453); a fresh install would miss files and an update would *delete* "untagged" files.
- **MemoryError → one automatic retry with Legendary's suggested cache size rounded up to 256 MiB (cap: half the RAM, ≤ 12 GiB)**.
- **Repair with a missing local manifest → one retry with `--repair-and-update`**.
- **`status --json` crashing in the forced api.legendary.gl check after a clean login is not a sign-out**: the account is read from user.json.
- **`auth --code` while a session exists → `auth --delete` and retry once**: `auth` tests stored credentials before using the code, so the code is still valid.
- **User launch args are appended by Glider, never passed through Legendary**: `parse_known_args` would swallow look-alike flags such as `-y`.
- **Exchange codes and authorization codes never reach logs or tails** (sensitive stdout, redacted argv).
- **Install sizes for Fortnite = sum of the chosen tags' sizes, capped at the manifest totals**: tags come from installed.json, else `[Fortnite] install_tags`, else the cached SDL `__required` pack; per-tag sums double-count shared files, so this errs high (safe for space checks).
- **Cross-volume move: copy with progress → `move --skip-move` → delete the source**: Legendary's move is a rename; any failure before the metadata switch leaves the original intact.

## Jobs
- **Strictly serial; a paused job blocks the jobs behind it**: an update or verify must never run under a half-finished install.
- **Jobs are claimed (status + running slot) in the same critical section that selects them**: a concurrent pause/cancel sees either a queued or a running job, never one in between.
- **Pause = kill + mark Paused; resume = re-run the identical command**: Legendary's resume file (completed files) does the rest; pause is offered for install/update/repair/verify, not for move/uninstall/import.
- **Restart/crash: Running/Waiting jobs come back Queued and resume automatically; Paused stays Paused**; shutdown interrupts instead of pausing, so the job stays "running" on disk.
- **Nothing runs or is written before `RestoreAsync`**: an early save would overwrite the jobs it is about to load.
- **Cancel of a fresh install deletes the resume file, and the game folder only if this job created it and nothing is registered**: a later install starts clean; pre-existing folders/data are never touched.
- **Cancel of an update/repair keeps the resume file**: the folder is a mix of old and new files and only that file tells Legendary which are new; without it Legendary would patch from already-updated files.
- **Verify policy: always after a fresh install; after an update only when writes failed for an unknown reason; then at most one automatic repair of what verify found**: a fresh install pays one SHA-1 pass for certainty, weekly updates don't pay minutes of disk I/O for nothing; disk-full/locked-file failures are surfaced instead (repair can't fix them).
- **`IJobHooks.BeforeRunAsync` runs before every (re)start; `OnSucceeded/OnFailed` run before the terminal status is published**: EGL waits show as Waiting + BlockedReason, and the UI never sees "completed" with a stale game record.
- **Enqueue is idempotent per kind while a job of that kind is active**: double clicks don't queue duplicates.
- **jobs.json is written on every state change; progress at most every 10 s**: state is crash-safe without 10 Hz disk writes.
- **Overall progress = base + (100 − base) × run%, never decreasing within a phase**: Legendary restarts its counters each run; base is the share of the first run's download the current run no longer needs. Verify and download are separate bars.
- **Speeds: time-based EMA (τ = 3 s); ETA: countdown corrected towards the measurement (τ = 6 s), snapping when our own measurement first replaces Legendary's average; events ≤ 10 Hz, leading + trailing edge**.
- **The game folder is assumed to be `<base>\Fortnite`** (catalog FolderName, unverified signed-out) for the "did this job create it" check.

## Infrastructure
- **AutoTune: workers = clamp(2 × CPUs, 6, 16), capped at 4/8 on < 4/< 8 GiB RAM; cache = 1/2/3/4 GiB for < 6/< 12/< 24/≥ 24 GiB RAM**: workers are latency-bound processes (~10–20 MiB/s each), 16 saturates multi-Gbit links and the single writer; more cache only helps slow disks and reuse-heavy patches.
- **AtomicFile = temp + FlushFileBuffers + `File.Replace`/`File.Move`, retried for ~3.5 s**: Windows can't rename over a file another process has open at that instant (antivirus does this briefly).
- **JsonFileStore saves are ordered and coalesced; the previous version is kept as `.bak` and used when the main file is corrupt**.
- **Settings load field by field over `Settings.Default`**: a file from another build never resets everything, and an invalid value only drops that field.
- **FileLog: unbounded channel to a single writer, files shared read/write/delete, 14-day retention**: `Write` never blocks, the in-app viewer uses the in-memory tail.
