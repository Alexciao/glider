# Glider v0.1.4

## Fixed
- Epic sign-in: the login window now presents itself to Epic exactly like Microsoft Edge (client hints no longer include the "WebView2" brand, `window.chrome.webview` removed, tracking prevention off for the login profile only). This targets the repeated captchas and the 2FA "wrong response" error. Not verified against a real account; please try it.
- If sign-in still fails, "Copy logs" now includes a short, redacted trace of the login requests (status and path only, no codes, cookies or queries) so the failing step can be identified.

## Unchanged
- The login window keeps its native look: themed title bar, sized to Epic's page, centered on Glider, no password-save prompt.

## Update
Run the new Setup.exe over your existing install; your data is kept.
