# Legendary output fixtures

Two kinds of files live here.

**Real captures** (copied unchanged from `docs/research/fixtures/legendary/`): produced by running the official
`legendary_windows_x64.exe` 0.21.1 **signed out**, config dir replaced by `<CONFIG_DIR>`. Examples:
`install-notloggedin.stderr.txt` (traceback), `status-network-failure.stderr.txt`, `encoding-test-*.stderr.bin`
(raw cp1252 + `\uXXXX` stderr bytes), `status-offline-json.stdout.txt`, `api-sdl-Fortnite.json`.

**Synthetic renders** (`synthetic-*`): Legendary was never run signed in for this project, so installs, verifies,
sign-ins and launches were never captured. `generate_synthetic.py` renders them with the *verbatim* f-strings and
logging format from the 0.21.1 source (each function cites `file:line`), plausible values, CRLF line ends and the
real stream encodings (stderr cp1252 with `backslashreplace`, stdout UTF-8). Text fixtures carry a `#` header line
saying so; the JSON ones (`synthetic-installed.json`, `synthetic-user.json`, `synthetic-metadata-Fortnite.json`) can't,
so this README is their provenance note. Regenerate with `python -I generate_synthetic.py`.

Replace synthetic files with real captures once someone runs Glider signed in (see `docs/research/legendary.md` §12).
