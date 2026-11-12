# Security & robustness review (2026-11-12)

Independent review of `src/Glider.Core` and `src/Glider.App` (frontend out of scope). Branch `t3/security-robustness-review`.
Every fixed finding has a regression test that fails without the fix. The review was stopped early for budget, so the
"Not reviewed" list at the end is real.

## Fixed

| Sev | Area | Where | Finding | Fix |
|---|---|---|---|---|
| High | Files | `Jobs/JobQueue.cs` StartCleanup; `Legendary/LegendaryCli.cs` MoveAsync | Recursive deletes had no guard. Cancel cleanup deleted the `GameFolder` read from `jobs.json`, so a corrupt or edited file could point it anywhere. A cross-drive move copied and then deleted whatever `installed.json` called the install path, even a drive root, without checking for the game. | `499215b`: `Infrastructure/DeleteGuard` refuses roots, links, and anything that is or contains a Windows/user/Glider folder (`..` resolved first). Cleanup only deletes `<base>\Fortnite` recomputed from the request. Move requires the game exe in the source. Junctions inside a tree are unlinked first, never followed (verified with `mklink /J`). |
| Medium | Files | `LegendaryCli.MoveAsync` | A crash mid-copy left a partial destination, and every retry then failed with "already exists". If `move --skip-move` was misclassified as failed, the new copy was deleted even though Legendary already pointed at it. | `499215b`: a marker file identifies Glider's own partial copy, which the retry discards. The copy is only deleted if Legendary demonstrably doesn't point at it. |
| High | Jobs | `JobQueue.RestoreAsync` | A `jobs.json` that parses but holds nulls (`{"jobs":null}`, null entries, null baseline) threw. Prewarm swallowed the error, but the queue stayed un-restored: from then on nothing ran or saved, and installs sat at "queued" forever. | `1bf23ee`: unusable entries are sanitized away and the queue always comes up. `JsonFileStore` also refuses files over 16 MiB. |
| Medium | EGL | `Egl/EglFileTransaction.cs` PruneOldBackups | Pruning sorts by timestamp name. After the clock is set back, the backup just taken sorts oldest and was deleted before the write it protects, leaving rollback with nothing to restore. | `b9169c3`: the current set is always kept. |
| Medium | EGL | `Egl/EglService.cs` | `.item`, `.mancpn` and `LauncherInstalled.dat` (in shared, writable ProgramData) were read with `File.ReadAllBytes`, so a ~2 GB planted file would be allocated in full. | `b9169c3`: 8 MiB cap. Fuzz tests (wrong types, deep nesting, binary) show the tolerant parser already held. |
| High | Auth | `FileLog`, `HostLog`, `Diagnostics`, `EpicLogin.cs:244` | Redaction ran only on Copy logs and only matched JSON double quotes. Log files on disk were never redacted. Python reprs, `?code=` query strings and bare `eg1~` tokens slipped through. The login window logged the first 80 characters of each URL, which for IdP callbacks includes the one-time code. | `57220d3`: `SecretRedactor` runs on every log line, on error `Detail`s and Legendary tails, and on exception messages. Login URLs are logged as scheme + host + path only. |
| Low | IPC | `Contracts/RpcDispatcher.cs` | Unexpected exceptions sent the full stack trace to the UI. | `57220d3`: the UI gets the type and redacted message. The full exception still goes to the host log. |
| High | Elevation | `Game/EacService.cs` | `EasyAntiCheat_EOS_Setup.exe` is run with `runas` from the game folder, which Legendary creates unelevated, so any program of this user could swap it and borrow Glider's UAC prompt. A manifest prerequisite path could also point outside the game folder. | `464a078`: the exe needs a valid Authenticode signature (WinVerifyTrust, cache-only revocation) from Epic Games / Easy Anti-Cheat, and the path must stay inside the game folder. |
| Medium | Robustness | `Legendary/LegendaryProcessRunner.cs` | The pinned Legendary hash was only checked at build time. A damaged exe showed "Retry; progress is kept", which no retry can fix. | `737e3fe`: SHA-256 check before the first run (and when the file changes), failing with LegendaryMissing ("reinstall Glider"). A test keeps the pin equal to `build/legendary.json`. |
| Low | IPC | `MainWindow.cs:328`, `WebViewSetup.cs` | The RPC origin check also accepted `about:blank`, and any port. | `160cf97`: only `https://app.glider` on the default port, with no userinfo. |
| Low | Host | `WindowsHostPlatform.OpenPath` | `explorer.exe` was started by bare name with `UseShellExecute=false`, which looks in the app and current directories before System32 (binary planting). | `160cf97`: started from `%WINDIR%\explorer.exe`. |
| Low | Args | `SettingsStore.CleanHost`, `SettingsValidator`, `InstallArguments` | A CDN value like `-y` passed the filter and broke Legendary's argument parsing. Values can't add argv entries because they go through `ArgumentList`. | `160cf97`: must start with a letter or digit. |

## Open / accepted risk

| Sev | Area | Where | Finding | Status |
|---|---|---|---|---|
| Medium | Host | `MainWindow.OnProcessFailed` | On `BrowserProcessExited` the window is recreated from the same, dead `CoreWebView2Environment`. A second crash within 10 s is ignored, leaving a blank window. | Open. Needs a GUI test to fix safely: recreate the environment, and show an error after N failures. |
| Low | Process | `LegendaryProcessRunner` / `NativeMethods` | Job assignment happens after `Process.Start`, so a child spawned in that window escapes the job. Tree-kill covers cancel, but not a Glider crash. | Accepted: PyInstaller's unpack makes the window effectively unhittable. The fix is `CREATE_SUSPENDED` / `PROC_THREAD_ATTRIBUTE_JOB_LIST` via a custom `CreateProcess`. |
| Low | Auth | `LegendaryCli.SignInWithCodeAsync` | The authorization code is in Legendary's argv, so same-user processes can see it. | Accepted: those processes can already read `user.json`. |
| Low | Auth | `EpicLogin.cs` | The login window has no navigation or new-window restriction, which IdP flows need. Password autosave is on. Sign-out clears the whole profile. | Accepted (it behaves like a browser). Spoofing the redirect needs content on `www.epicgames.com/id/api/redirect`. |
| Low | IPC | `RpcDispatcher` | No per-request timeout, so a method that never completes never answers. All methods except SignIn, which waits on the user, are bounded. | Accepted. |
| Low | Host | `Program.cs` single instance | The `--data-dir` scope isn't path-normalized, and a mutex per session lets two sessions of the same user run Cores on one data dir. | Open (CLI/RDP edge cases). |
| Info | EAC | `AuthenticodePublisherCheck` | The trusted-publisher names weren't checked against a real `EasyAntiCheat_EOS_Setup.exe` (none on this machine). | **To verify** on a real install: if the signer subject lacks "Epic Games" / "Easy Anti-Cheat", extend `TrustedPublishers`. |
| Info | Tests | Core suite | One unidentified test failed once, right after a full solution build (likely a timing test under load). The next 3 runs passed. | Open. |

## Not reviewed (budget stop)

`HttpArtCache` file naming and downloads, `StateHub`, `UpdateChecker`, `ProcessWatcher`, full `ErrorClassifier` coverage (`Unknown`
paths), disk-full / network-loss end-to-end, WebView2 `cache.glider` traversal (relies on WebView2's mapping), and the
frontend.
