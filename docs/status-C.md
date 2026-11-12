Detection uses InstallLocation from every .item and LauncherInstalled.dat on any drive: fixed (logic was already drive-agnostic; real miss was manifests unreadable while EGL has them open for writing, now read with shared access).
Locate existing install on any drive: fixed (also accepts a parent folder with a non-default game folder name, or a folder inside the install).
Quick scan of all fixed drives: fixed (egl.quickScan RPC, known folders only, cancellable; exposed as a command-palette entry, not a Settings button).
Adopt in place (no copy/re-download) on non-C: drives, spaces, non-default names: fixed (verified by tests; no code change needed).
Opt-in EGL manifest write-back on non-C: / spaces / forward slashes: fixed (verified by tests; backup and atomic write unchanged).
Contract: InstallCandidate.Reason: fixed (contract.gen.ts regenerated; frontend candidateIssue not switched to it).
Settings "scan drives" button: not fixed: Settings.tsx is owned by the frontend branch; command palette entry only.
