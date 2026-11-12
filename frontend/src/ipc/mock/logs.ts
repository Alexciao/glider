import { LATEST_VERSION, OLD_VERSION, shortVersion } from "./constants";
import { mulberry32 } from "./sim";

const pad = (n: number, w = 2): string => String(n).padStart(w, "0");

function stamp(ms: number): string {
  const d = new Date(ms);
  return (
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ` +
    `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`
  );
}

export function logFileName(nowMs: number): string {
  const d = new Date(nowMs);
  return `C:\\Users\\alex\\AppData\\Local\\Glider\\logs\\glider-${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}.log`;
}

type Level = "INFO" | "WARN" | "ERROR";
interface Line {
  gapMs: number;
  level: Level;
  source: string;
  text: string;
}

const hms = (s: number): string => `${pad(Math.floor(s / 3600))}:${pad(Math.floor((s % 3600) / 60))}:${pad(Math.floor(s % 60))}`;

/** ~300 plausible log lines, newest last, ending a few seconds before `nowMs`. Deterministic for a given seed. */
export function generateLogs(nowMs: number, maxLines: number, seed = 20261112): string {
  const rand = mulberry32(seed);
  const lines: Line[] = [];
  const add = (level: Level, source: string, text: string, gapMs = 40 + rand() * 900): void => {
    lines.push({ gapMs, level, source, text });
  };

  add("INFO", "Glider.App", "Glider 0.1.0 starting (pid 14872, Windows 11 10.0.26300, x64)", 0);
  add("INFO", "Glider.App", "WebView2 runtime 157.0.4341.46, backdrop: Mica");
  add("INFO", "Glider.Settings", "Loaded settings from C:\\Users\\alex\\AppData\\Local\\Glider\\state\\settings.json");
  add("INFO", "Glider.Legendary", "legendary.exe 0.21.1 (SHA-256 verified), config C:\\Users\\alex\\AppData\\Local\\Glider\\legendary");
  add("INFO", "Glider.Auth", "legendary status --offline --json: signed in as Marlowe");
  add("INFO", "Glider.Egl", "EGL data directory found: C:\\ProgramData\\Epic\\EpicGamesLauncher\\Data");
  add("INFO", "Glider.Egl", "Scanned 1 manifest, 1 Fortnite candidate: D:\\Epic Games\\Fortnite (" + shortVersion(LATEST_VERSION) + ")");
  add("INFO", "Glider.Game", "Install record: D:\\Epic Games\\Fortnite " + shortVersion(OLD_VERSION) + ", shared with EGL");
  add("INFO", "Glider.Prewarm", "Token refresh ok (212 ms)");
  add("INFO", "Glider.Prewarm", `Update check: latest ${shortVersion(LATEST_VERSION)}, installed ${shortVersion(OLD_VERSION)}`, 1400);
  add("INFO", "Glider.Egl", "Drift check: EGL manifest matches install record");
  add("INFO", "Glider.App", "UI ready after 438 ms; window revealed");
  add("WARN", "Glider.Egl", "EGL manifest is older than the installed version; sync will rewrite it on next change", 3200);
  add("ERROR", "Glider.Auth", "Token refresh failed: The operation was canceled (network unreachable). Retrying in 30 s", 8400);
  add("INFO", "Glider.Auth", "Token refresh ok (341 ms) after 1 retry", 30_000);
  add("INFO", "Glider.Jobs", "Job job-1 (update) queued: 40.10 → 40.20, target D:\\Epic Games\\Fortnite", 20_000);
  add("INFO", "Glider.Jobs", "Job job-1 started");
  add("INFO", "Glider.Legendary", "legendary -y update Fortnite --skip-sdl --skip-dlcs");
  add("INFO", "Glider.Legendary", "[cli] INFO: Preparing download for Fortnite, version " + shortVersion(LATEST_VERSION));
  add("INFO", "Glider.Legendary", "[DLM] INFO: Cache usage: 1024 MiB shared memory, 8 workers");

  const total = 27.9;
  const step = total / 268;
  let done = 0;
  let t = 3;
  let dl = 58;
  let warned = false;
  while (done < total && lines.length < 320) {
    dl = Math.min(92, Math.max(38, dl + (rand() - 0.5) * 9));
    const speed = dl + rand() * 6;
    const gained = step * (0.8 + rand() * 0.4);
    done = Math.min(total, done + gained);
    const seconds = (gained * 1024) / speed;
    t += seconds;
    const p = (done / total) * 100;
    const eta = Math.max(0, ((total - done) * 1024) / speed);
    add(
      "INFO",
      "Glider.Legendary",
      `[DLM] INFO: = Progress: ${p.toFixed(2)}% (${done.toFixed(1)}/${total} GiB), Running for ${hms(t)}, ETA: ${hms(eta)} ` +
        `| Download: ${speed.toFixed(1)} MiB/s, Disk: ${(speed * 3.45).toFixed(1)} MiB/s`,
      seconds * 1000,
    );
    if (!warned && p > 24) {
      warned = true;
      add("WARN", "Glider.Legendary", "[DLM] WARNING: Chunk 7F3A9C1E download failed (HTTP 503), retrying (attempt 2/5)", 80);
      add("INFO", "Glider.Legendary", "[DLM] INFO: Chunk 7F3A9C1E recovered after 1 retry", 900);
    }
    if (lines.length === 180) add("WARN", "Glider.Jobs", "Stall watchdog: no progress for 12 s, still waiting (limit 90 s)", 12_000);
  }
  add("INFO", "Glider.Legendary", "[DLM] INFO: Download complete, verifying installed files");
  add("INFO", "Glider.Jobs", `Job job-1 completed in ${hms(t)}`);
  add("INFO", "Glider.Egl", "Wrote EGL manifest D:\\…\\3F2A91C4B7E04D11A6C0D8E5F27B9A13.item (backup kept)");
  add("INFO", "Glider.Game", "Install record updated: 40.20");

  // Space the lines so the last one lands ~15 s before `nowMs`, preserving relative gaps (but capped for readability).
  const gaps = lines.map((l) => Math.min(l.gapMs, 30_000));
  const span = gaps.reduce((s, g) => s + g, 0);
  const fit = Math.min(1, (45 * 60_000) / Math.max(1, span));
  let at = nowMs - 15_000 - span * fit;
  const out = lines.map((l, i) => {
    at += (gaps[i] ?? 0) * fit;
    return `${stamp(at)} ${l.level.padEnd(5)} ${l.source.padEnd(17)} ${l.text}`;
  });
  return out.slice(-Math.max(1, maxLines)).join("\n");
}
