# Glider v0.1.3

## Fixed
- Epic sign-in: the login window now loads visible, focused and correctly positioned from the start (it used to load hidden, which can make Epic's captcha and 2FA reject the response and ask for more captchas). Not confirmed against a real account; see below for a fallback.
- Epic sign-in window sized to fit Epic's page (less scrolling), centered on Glider, themed title bar, no Edge "save password" prompt or autofill.
- Playtime tooltip now says it only counts time played through Glider.

## Added
- "Trouble signing in? Use your browser instead": opens Epic's login in your default browser and lets you paste the result back into Glider.

## Update
Run the new Setup.exe over your existing install; your data is kept.
