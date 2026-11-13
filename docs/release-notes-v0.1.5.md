# Glider v0.1.5

## Fixed
- Epic sign-in now works: the login window signs in the same way the Epic Games Launcher does (launcher identity plus its sign-in bridge). Epic previously rejected the 2FA step ("Risposta errata. Aggiorna la pagina") and asked for endless captchas. Verified with a live sign-in including 2FA.
- Google/Facebook/Xbox/PlayStation sign-in pop-ups now open in a themed Glider window (no address bar or Edge chrome).
- Closing the login window shows a calm "Sign-in was cancelled" message with a "Copy diagnostic logs" button.
- F5 / Ctrl+R reloads the login page; it also reloads automatically if Epic reports an invalid captcha.

## Removed
- The "sign in with your browser instead" fallback (temporarily).

## Update
Run the new Setup.exe over your existing install; your data is kept.
