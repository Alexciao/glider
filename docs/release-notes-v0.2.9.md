# Glider v0.2.9

Update now: Glider shows a "Glider is ready" notice once the update has downloaded; choose **Restart**, or it installs when you quit Glider. Or run `Glider-win-Setup.exe` and choose **Update**.

## New

- **A new Play button.** A game's Play button, favorite star and menu now sit together in one glass bar. The main button turns blue only when it's the thing to do next (Play, Install, Update, Resume, Sign in); Pause and Close game stay glass. Its menu opens from **⋯**.
- **Where your playtime comes from.** Hover the **ⓘ** next to Playtime to see how many hours Epic Games Launcher recorded and how many you played in Glider.
- **When you last played, exactly.** Hover the **ⓘ** next to Last played to see the date and time you opened the game, and when you closed it. Games you last played before this update show when you closed them, until you play them again.
- **Copy the Store's address.** Hover the address at the top of the Store to see a copy icon; click the address to copy the full link.

## Changed

- **Each thing is said once.** Starting a game shows one spinner and one "Launching…", not two. A paused download says "paused" once, a running job doesn't spin next to its own progress bar, and signed out no longer says "Sign in" twice.
- **The Store's top bar is solid.** Epic's menu bar (Discover, Browse, Wishlist, Cart…) no longer lets the page show through it, blurred, as you scroll.

## Fixed

- **Download time left is steadier and closer to the truth.** On slower drives, downloads arrive in bursts and the time left used to swing between minutes and hours, or read "less than a minute" while the drive still had minutes of writing left. Glider now estimates from the last minute of real progress and counts the writing to disk too.
- **Verifying shows the right size.** Checking Fortnite's files showed "122 of 150 GB" for a much smaller install: Glider counted optional packs you didn't install. It now shows the size of the files actually being checked, or a file count when it can't tell.
