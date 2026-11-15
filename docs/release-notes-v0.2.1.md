# Glider v0.2.1

A fix release for v0.2.0. Update now: a running v0.2.0 or v0.1 picks this up by itself and installs it when you close Glider, or run `Glider-win-Setup.exe` and choose **Update**.

## Fixed

- **Downloads, sign-in and status checks failed with "Can't reach Epic" although the connection was fine.** The bundled Legendary (`0.21.1-glider.1`) crashed while checking its own version (`ValueError: invalid literal for int() with base 10: '1-glider'`). Glider now bundles `0.21.1-glider.2`, which reads its version correctly; its build now tests exactly this, so it can't ship again.
- **A crash inside Legendary is no longer reported as a connection problem.** It now reads "Legendary stopped unexpectedly", with the technical details under *Details*, so you aren't sent to check a working connection.

Everything else is as in [v0.2.0](release-notes-v0.2.0.md).
