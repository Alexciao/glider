# Concurrency experiments — Legendary 0.21.1 (pinned exe, sha256 6be77857…baf5b)

All runs used a throw-away `LEGENDARY_CONFIG_PATH` (shown as `<LK>\cfg`) with **fake** data written by
`tools/mk.py` (two fake installed games `FakeA`/`FakeB`) and `tools/mkmeta.py` (fake `metadata/FakeA.json` and a fake
`user.json` whose tokens are the literal string `FAKE`; no real credentials were used or read). No network writes, no GUI.
`tools/hold.py <lockfile> <seconds>` takes a lock exactly like filelock's `WindowsFileLock`
(`msvcrt.locking(fd, LK_NBLCK, 1)` on an `O_RDWR|O_CREAT|O_TRUNC` handle).

## out1 — Legendary vs Legendary on `installed.json.lock`

Process A = `legendary uninstall FakeA` **without** `-y`, stdin = a pipe that answers `n` after 20 s, so A sits at the
`Do you wish to uninstall` prompt **while holding `installed.json.lock`** (taken at `cli.py:1188`, never released until exit,
`lfs/lgndry.py:322-341`). While A waited:

| file prefix | command | result |
|---|---|---|
| `out1-B-uninstall` | `-y uninstall FakeB --keep-files` | `[cli] CRITICAL: Failed to acquire installed data lock…`, **exit 0**, immediate |
| `out1-C-import` | `-y import FakeC <dir>` | same CRITICAL, exit 0 |
| `out1-G-move` | `move FakeB <dir> --skip-move` | same CRITICAL, exit 0 |
| `out1-H-eglsync` | `-y egl-sync --one-shot` | same CRITICAL, exit 0 |
| `out1-F-verify` | `verify FakeA` | ran (no lock); failed only because the fake game has no manifest |
| `out1-I-status-offline` | `status --offline --json` | ran, `games_installed: 2` |
| `out1-D*-list-installed` | `list-installed [--json]` | ran (no lock) and crashed `TypeError` in `core.py:492` because there is no `assets.json` |
| `out1-E-launch-offline` | `launch FakeA --offline --json` | ran (no lock) and crashed `AttributeError` at `core.py:882` because `metadata/FakeA.json` was missing |

The `install` command takes the same lock at `cli.py:845` (same code path, same message).

## out2 — `launch --offline` output with fake metadata

`out2-launch-offline-json.stdout.txt` is a real-format `launch --json` document (offline ⇒ empty `-AUTH_PASSWORD=`).
`out2-launch-offline-dryrun.stderr.txt` is the `--dry-run` text form.

## out3 — `user.json.lock` held by `tools/hold.py` for 12 s, six commands started in parallel ~1.5 s later

| command | took | why |
|---|---|---|
| `status --offline --json` | **10 972 ms** (blocked until release) | reads `lgd.userdata` → `LockedJSONData` (blocking FileLock, timeout -1) |
| `launch FakeA --offline --json` | **10 927 ms** (blocked) | `core.get_launch_parameters` reads `lgd.userdata['account_id']` (`core.py:896`) |
| `list-installed --json` | 2 878 ms | no userdata access |
| `info FakeA --offline --json` | 2 913 ms | no userdata access (crashed, no assets) |
| `verify FakeA` | 2 899 ms | no userdata access |
| `-y uninstall FakeB --keep-files` | 2 898 ms | installed lock only |

(≈1.6–1.8 s of every run is PyInstaller one-file start-up: `--version` alone takes 1.6–1.8 s on this machine.)

## eos-overlay-info.stderr.txt

`legendary eos-overlay info` on the research machine (read-only; reads HKCU/HKLM). Shows an EOSH-managed overlay under
`%ProgramFiles(x86)%\Epic Games\Epic Online Services\managedArtifacts\98bc04bc…` and HKLM Vulkan implicit layers, but
no HKCU `OverlayPath` ⇒ "Overlay enabled: No".
