# Group E status

- Design screenshots / `docs/design-critique.md`: deferred to final screenshot pass.
- `game.close` RPC: fixed (`CloseGame`, graceful close then kill of Shipping + launcher stubs; contract regenerated; test added).
- Installer size under 60 MB: not fixed: runtime + WPF are untrimmable, vpk has no compression option; needs a framework-dependent build (product decision), see DECISIONS.md.
- Idle CPU/polling audit: fixed (audited, no waste found; results in `docs/perf.md`).
- Architecture review (v0.2): not fixed: needs real account/machine, scheduled for v0.2.
- Epic exchange-code lifetime verification: not fixed: needs real account/machine.
- `docs/perf.md` and `docs/decisions/perf.md`: fixed.
- Single-instance edge cases with `--data-dir`: not fixed: no bug found; mutex and pipe names already carry a hash of the data dir (Program.cs) and the mutex is session-local, so no change or test needed.
- Easy Anti-Cheat signer verification: not fixed: needs real account/machine.
- Data dir shared with Velopack root, uninstall deletes Legendary data: not fixed: no tiny safe fix that preserves existing users' data; needs a deliberate migration.
- FileLog flush: fixed (flush plus FlushFileBuffers per batch; test added, though the 0-byte symptom did not reproduce in-process).
- Never run against a real Epic account: not fixed: needs real account/machine.
- Code-signing: not fixed: no certificate available.
- Velopack self-updates from GitHub Releases: fixed (`AppUpdater`, skipped for dev/portable/test, announced via Notice; untested against a real release).
