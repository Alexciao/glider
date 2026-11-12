# Group D status

- Updates not recognized (task 7): fixed. UpdateChecker re-reads installed.json before comparing with the freshly fetched latest (regression: `UpdateCheckTests`). Epic-side version-string mismatch and "out of date" at launch not verified/handled.
- Verify and repair (task 8): fixed. Verify job = verify, repair, verify, with files checked/bad/fixed in `JobInfo.Verify` and the notice (tests: `VerifyRepairTests`). Works for adopted installs (import saves the manifest; missing manifest falls back to repair-and-update).
- Installed size on disk (task 9): fixed. `GameState.InstalledSizeBytes` measured in the background, cached, throttled, cancellable, remeasured after jobs (tests: `InstalledSizeTests`).
- Download size (task 9): fixed. Update delta from Legendary's dry run, manifest `download_size` for fresh installs, null while unknown (tests: `DownloadSizeTests`, `UpdateDryRunTests`).
- `AppInfo.AvailableCdns` contract: not fixed: Legendary exposes no CDN list, so it needs a hard-coded list and Settings UI wiring (Group A territory).
- Throughput/AutoTune benchmark vs Epic CDN: not fixed: needs a real Epic account.
- Not-yet-reviewed update checker / error-mapping / disk-full / network-loss: partly fixed: update checker reviewed (above); error-mapping coverage and disk-full/network-loss not reviewed.
- Accepted risks (Job Object race, sign-in code on Legendary's command line): not fixed: accepted risks, out of scope.
- Frontend: mocks and stress data need `installedSizeBytes: null` on `GameState` literals (Group A adds the field in the same place); `verify: null` added on `JobInfo` literals. Frontend tests were not run (no node_modules in this worktree).
