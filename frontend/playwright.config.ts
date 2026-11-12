import { defineConfig } from "@playwright/test";
import { E2E_PORT as PORT } from "./e2e/tools/port.mjs";

/**
 * Headless Chromium against the Vite dev server with the mock backend (no WebView2, no native GUI).
 * `pnpm screenshots` renders every screen/state into docs/screenshots (see e2e/screenshots.spec.ts).
 */
export default defineConfig({
  testDir: "./e2e",
  timeout: 90_000,
  fullyParallel: true,
  workers: Number(process.env.GLIDER_E2E_WORKERS ?? 4),
  reporter: [["list"]],
  use: {
    baseURL: `http://localhost:${PORT}`,
    browserName: "chromium",
    deviceScaleFactor: 1,
    reducedMotion: "no-preference",
  },
  webServer: {
    command: `pnpm exec vite --port ${PORT} --strictPort`,
    url: `http://localhost:${PORT}`,
    reuseExistingServer: true,
    timeout: 60_000,
  },
});
