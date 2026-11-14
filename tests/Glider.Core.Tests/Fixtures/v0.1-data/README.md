# Glider v0.1.5 data, as found on a real install (sanitized)

`state/` and `cache/` were copied byte for byte (read-only) from a real `%LocalAppData%\Glider` on 2026-11-14, then the
one personal path (`D:\EpicLibrary\Fortnite`) was replaced by `D:\Games\Fortnite`. Nothing else was edited, so the real
v0.1 quirks are kept exactly: CRLF + 2-space indent from `JsonFileStore`, `\u002B` escapes for `+`, the PascalCase
`"step": "Repair"` in `jobs.json`, an integer `percent`, a negative `totalWriteBytes` in a baseline, and the UTF-8 BOM that
`SmallFiles` wrote into `cache/art.json` and `cache/installed-size.json`. That machine had no `settings.json` and no
`onboarding.json`. `legendary\` (tokens, installed.json) was never read.

`../v0.1-variants/` holds hand-written files in the same v0.1 shapes for what that machine lacked: a non-default
`settings.json`, a BOM `onboarding.json` that is not completed, and a `jobs.json` with an install interrupted in its
verify step.
