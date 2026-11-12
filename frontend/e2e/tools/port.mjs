// The e2e dev-server port. Playwright reuses a server that is already listening, so a fixed port
// shared by two checkouts (git worktrees, parallel agents) makes one silently screenshot the other's
// code. The default is therefore derived from this checkout's path; GLIDER_E2E_PORT still overrides.
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../", import.meta.url)).toLowerCase();

let hash = 0;
for (const ch of root) hash = (Math.imul(hash, 31) + ch.charCodeAt(0)) >>> 0;

/** 5200–5999, stable per checkout. */
export const E2E_PORT = Number(process.env.GLIDER_E2E_PORT ?? 5200 + (hash % 800));
