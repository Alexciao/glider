import type {
  AntiCheat,
  GameArt,
  GameKind,
  GameSettings,
  GameSummary,
  InstallComponent,
  InstallStatus,
  LaunchOption,
  Prerequisite,
  SaveConflict,
  SaveState,
  SaveSyncStatus,
  ShortcutState,
  ThirdPartyStore,
} from "../contract.gen";
import { FORTNITE_ART, generatedArt } from "./art";
import { DAY, FORTNITE, GiB, HOUR, LATEST_VERSION, MiB, OLD_VERSION, iso } from "./constants";
import { between, chance, hashString, mulberry32, pick, shuffled, type Rand } from "./rng";

/**
 * Deterministic fictional library for the mock backend. Titles, studios and art are invented (no real trademarks, no
 * copyrighted art); Fortnite appears by name only, as the user's v0.1 game. The same (seed, count) always yields the
 * same library, so tests and screenshots are stable.
 */

/** Everything the mock knows about one owned item; `summary` is what goes on the wire in library deltas. */
export interface MockGame {
  summary: GameSummary;
  description: string;
  publisher: string;
  releaseDate: string;
  /** AppNames of DLC owned for this base game. */
  dlc: string[];
  components: InstallComponent[];
  prerequisites: Prerequisite[];
  /** Null = the game has no cloud saves. */
  saves: SaveState | null;
  conflict: SaveConflict | null;
  settings: GameSettings;
  launchOptions: LaunchOption[];
  shortcuts: ShortcutState;
  quirks: string[];
  antiCheat: AntiCheat;
  executable: string | null;
  installedAt: string | null;
  eglManifestPath: string | null;
  /** Catalog FolderName: the leaf folder installs go in. */
  folderName: string;
  fullDownloadBytes: number;
  fullInstallBytes: number;
  updateDownloadBytes: number;
  updateWriteBytes: number;
  /** Third-party game that has not been linked/activated yet. */
  needsActivation: boolean;
  /** Files a verify would find damaged (broken installs). */
  badFiles: number;
  badBytes: number;
}

export interface CatalogOptions {
  seed: number;
  /** Base games (DLC come on top). */
  count: number;
  now: number;
  /** false → every GameArt is all-null (the no-art fallback). */
  art: boolean;
}

/** Library folders of the mock PC; installs are spread over them. */
export const LIBRARY_FOLDERS = ["D:\\Games", "E:\\Epic Games", "F:\\Library", "C:\\Program Files\\Epic Games"] as const;

/* ------------------------------------------------------------------ names */

const ADJ = [
  "Hollow", "Ember", "Verdant", "Quiet", "Gilded", "Lantern", "Cinder", "Sable", "Tidal", "Mossy", "Neon", "Paper", "Brass",
  "Frozen", "Wandering", "Salt", "Hidden", "Moonlit", "Copper", "Drifting", "Last", "Silent", "Crimson", "Amber", "Hushed",
  "Scarlet", "Ivory", "Wild", "Distant", "Clockwork", "Midnight", "Sunken", "Azure", "Rusted", "Gentle", "Broken", "Velvet",
  "Fallen", "Restless", "Pale", "Burning", "Lonely", "Crooked", "Starlit", "Feral", "Marble", "Thorned", "Misty", "Humble",
];
const NOUN = [
  "Orchard", "Foundry", "Harbor", "Signal", "Meridian", "Kite", "Atlas", "Bramble", "Citadel", "Dune", "Echo", "Fathom",
  "Gambit", "Horizon", "Isle", "Jetty", "Keep", "Loom", "Mirage", "Nimbus", "Outpost", "Prism", "Quarry", "Reverie", "Spire",
  "Thicket", "Vessel", "Warren", "Zenith", "Lighthouse", "Cartographer", "Ferryman", "Gardener", "Clocktower", "Lagoon",
  "Archive", "Canopy", "Delta", "Embassy", "Furnace", "Glacier", "Hollow", "Inlet", "Junction", "Lantern", "Marsh", "Nomad",
  "Observatory", "Pilgrim", "Rift", "Sanctum", "Tundra", "Undertow", "Vault", "Wayfarer", "Yard", "Atoll", "Bazaar", "Cove",
];
const PATTERN = [
  (a: string, n: string) => `${a} ${n}`,
  (a: string, n: string) => `${a} ${n}`,
  (a: string, n: string) => `The ${a} ${n}`,
  (a: string, n: string) => `${n} of the ${a} Coast`,
  (a: string, n: string) => `${a} ${n} II`,
  (a: string, n: string) => `${n}: ${a} Edition`,
  (a: string, n: string) => `${a} ${n} Tactics`,
  (_a: string, n: string) => `Project ${n}`,
  (a: string, n: string) => `${n} & ${a} Co.`,
];
const STUDIOS = [
  "Lanternfish Games", "Paper Moth Studio", "Brightwater Interactive", "Tin Kettle Works", "Halfmoon Collective", "Ferncliff Digital",
  "Orbit & Ash", "Small Hours", "Copperline Studios", "Marrow Lane", "Saltmarsh Games", "Quillfeather", "Northgate Labs", "Dovetail Interactive",
  "Pebble & Pine", "Redkite Software", "Wren House", "Cobalt Orchard", "Starling Forge", "Grayfriar Games",
];
const PUBLISHERS = ["Lanternlight Publishing", "Meridian Arts", "Fieldstone Games", "Kestrel Entertainment", "Open Harbor", "Bluebell Press"];
const CODENAMES = [
  "Anemone", "Barnacle", "Cormorant", "Dandelion", "Egret", "Foxglove", "Gannet", "Heron", "Iris", "Juniper", "Kestrel", "Lupin",
  "Marten", "Nightjar", "Osprey", "Plover", "Quokka", "Rowan", "Sparrow", "Teasel", "Urchin", "Vole", "Whinchat", "Yarrow", "Zinnia",
  "Alder", "Bracken", "Campion", "Dunlin", "Elder", "Fennel", "Goshawk", "Hazel", "Ibis", "Jackdaw", "Knapweed", "Linnet", "Mallow",
];
const DLC_SUFFIX = [
  "Season Pass", "Original Soundtrack", "Cosmetic Pack", "Digital Artbook", "Expansion: The Long Way Home", "Founders Pack", "Challenge Pack",
  "Bonus Missions", "Deluxe Upgrade", "Supporter Pack",
];
const DESCRIPTIONS = [
  "A slow, careful adventure about rebuilding a place the world forgot.",
  "Plan your route, trade at every harbor and keep the lights on through the long nights.",
  "A tactical puzzle game where every move leaves a trace the next level remembers.",
  "Explore a hand-drawn archipelago by raft, kite and borrowed boat.",
  "Run a small workshop at the edge of a growing city, one commission at a time.",
  "Cooperative survival for up to four. The tide always comes back.",
  "A cozy detective story told through postcards, maps and half-finished letters.",
  "Fast arena duels with a rewind mechanic that rewards patience over reflexes.",
];

export const folderNameOf = (title: string): string => title.replace(/[^A-Za-z0-9]+/g, "");

function uniqueTitle(rand: Rand, used: Set<string>): string {
  for (let tries = 0; tries < 50; tries++) {
    const t = pick(rand, PATTERN)(pick(rand, ADJ), pick(rand, NOUN));
    if (!used.has(t)) {
      used.add(t);
      return t;
    }
  }
  let n = 2;
  const base = `${pick(rand, ADJ)} ${pick(rand, NOUN)}`;
  while (used.has(`${base} ${n}`)) n++;
  const t = `${base} ${n}`;
  used.add(t);
  return t;
}

const hex = (rand: Rand, len: number): string => Array.from({ length: len }, () => Math.floor(rand() * 16).toString(16)).join("");

function appNameFor(rand: Rand, used: Set<string>): string {
  for (;;) {
    const name = chance(rand, 0.55) ? `${pick(rand, CODENAMES)}${hex(rand, 3).toUpperCase()}` : hex(rand, 32);
    if (!used.has(name)) {
      used.add(name);
      return name;
    }
  }
}

/* ------------------------------------------------------------------ the generator */

const setOf = (indices: readonly number[], from: number, n: number): Set<number> => new Set(indices.slice(from, from + n));

/** Scale a count for a library of `count` games (300 is the reference), never below `min` when the library can hold it. */
const scaleTo = (count: number, per300: number, min = 1): number => Math.min(count, Math.max(count >= 40 ? min : 0, Math.round((per300 * count) / 300)));

const defaultSettings = (): GameSettings => ({ launchArgs: "", executableOverride: null, workingDirectory: null, autoUpdate: "default", cloudSaves: true, launchMode: "auto", launchOption: null });

export function generateCatalog(opts: CatalogOptions): MockGame[] {
  const { seed, count, now } = opts;
  const rand = mulberry32(seed);
  const used = new Set<string>(["Fortnite"]);
  const apps = new Set<string>([FORTNITE]);
  const artFor = (key: string, art?: { tall?: boolean; wide?: boolean }): GameArt =>
    opts.art ? generatedArt(key, art) : { tall: null, tallThumb: null, wide: null, logo: null, placeholder: null };

  // ---- decide who gets which trait (indices into 1..count-1; index 0 is Fortnite) ----
  const order = shuffled(rand, Array.from({ length: Math.max(0, count - 1) }, (_, i) => i + 1));
  let cursor = 0;
  const take = (n: number): Set<number> => {
    const s = setOf(order, cursor, n);
    cursor += n;
    return s;
  };
  const installed = take(scaleTo(count, 41, 6));
  const updates = new Set([...installed].slice(0, scaleTo(count, 9, 2)));
  const broken = new Set([...installed].slice(scaleTo(count, 9, 2), scaleTo(count, 9, 2) + scaleTo(count, 3, 1)));
  const unverified = new Set([...installed].slice(-scaleTo(count, 2, 1)));
  const noEglShare = new Set([...installed].slice(scaleTo(count, 12, 2), scaleTo(count, 12, 2) + scaleTo(count, 14, 2)));
  const ubisoftInstalled = new Set([...installed].slice(scaleTo(count, 30, 3), scaleTo(count, 30, 3) + scaleTo(count, 3, 1)));
  const ubisoftOnly = take(scaleTo(count, 5, 1));
  const eaOnly = take(scaleTo(count, 7, 1));
  const otherThirdParty = take(scaleTo(count, 2, 1));
  const offlineNo = new Set([...take(scaleTo(count, 26, 2)), ...[...installed].slice(-scaleTo(count, 6, 2))]);
  const noCloud = new Set([...take(Math.round(count * 0.3)), ...[...installed].slice(-scaleTo(count, 8, 2))]);
  const hiddenSet = take(scaleTo(count, 6, 1));
  const unmeasured = new Set([...installed].slice(2, 2 + scaleTo(count, 2, 1)));
  const missingTall = take(scaleTo(count, 4, 1));
  const missingWide = take(scaleTo(count, 4, 1));
  const dlcHosts = new Set([...take(Math.round(count * 0.14)), ...[...installed].slice(0, scaleTo(count, 14, 3))]);
  const addonHosts = new Set([...dlcHosts].slice(0, scaleTo(count, 3, 1)));
  const needsPrereq = new Set([...installed].slice(scaleTo(count, 20, 3), scaleTo(count, 20, 3) + scaleTo(count, 2, 1)));
  const everPlayedUninstalled = take(scaleTo(count, 7, 1));

  const thirdPartyOf = (i: number): ThirdPartyStore =>
    ubisoftInstalled.has(i) || ubisoftOnly.has(i) ? "ubisoft" : eaOnly.has(i) ? "ea" : otherThirdParty.has(i) ? "other" : "none";
  const thirdPartyName = (s: ThirdPartyStore): string | null => (s === "ubisoft" ? "Ubisoft Connect" : s === "ea" ? "EA app" : s === "other" ? "Another launcher" : null);

  // The first installed game (after Fortnite) with cloud saves that is not unverified/broken sits in conflict; two more differ.
  const installedList = [...installed].filter((i) => !broken.has(i) && !updates.has(i) && thirdPartyOf(i) === "none" && !noCloud.has(i));
  const conflictIdx = installedList[0] ?? -1;
  const localNewer = new Set(installedList.slice(1, 3));
  const cloudNewer = new Set(installedList.slice(3, 5));
  const saveError = new Set(installedList.slice(5, 6));
  const saveOff = new Set(installedList.slice(6, 8));

  const out: MockGame[] = [];
  const dlcOut: MockGame[] = [];

  const makeGame = (i: number): MockGame => {
    const isFortnite = i === 0;
    const title = isFortnite ? "Fortnite" : uniqueTitle(rand, used);
    const appName = isFortnite ? FORTNITE : appNameFor(rand, apps);
    const tp = isFortnite ? "none" : thirdPartyOf(i);
    const isInstalled = isFortnite || installed.has(i);
    const isBroken = broken.has(i);
    const notInstallable = !isInstalled && tp !== "none";
    const status: InstallStatus = notInstallable ? "notInstallable" : isBroken ? "broken" : isInstalled ? "installed" : "notInstalled";
    const hasUpdate = isFortnite || updates.has(i);

    const installBytes = isFortnite ? Math.round(96.3 * GiB) : Math.round(Math.exp(between(rand, Math.log(1.2 * GiB), Math.log(140 * GiB))));
    const downloadFull = isFortnite ? Math.round(27.9 * GiB) : Math.round(installBytes * between(rand, 0.42, 0.78));
    const updateDl = isFortnite ? Math.round(1.2 * GiB) : Math.round(Math.exp(between(rand, Math.log(60 * MiB), Math.log(7 * GiB))));
    const updateWrite = isFortnite ? Math.round(4.1 * GiB) : Math.round(updateDl * between(rand, 2.2, 3.6));

    const folderName = isFortnite ? "Fortnite" : folderNameOf(title);
    const base = isFortnite ? "D:\\Epic Games" : LIBRARY_FOLDERS[Math.floor(rand() * 100) % (rand() < 0.7 ? 2 : LIBRARY_FOLDERS.length)]!;
    const installPath = isInstalled ? `${base}\\${folderName}` : null;

    const addedAt = now - Math.floor(Math.pow(rand(), 1.6) * 6.2 * 365 * DAY) - DAY;
    const played = isInstalled ? chance(rand, isFortnite ? 1 : 0.78) : everPlayedUninstalled.has(i);
    const lastPlayed = isFortnite ? now - 5 * HOUR : played ? now - Math.floor(Math.pow(rand(), 2.2) * 50 * DAY) - HOUR : null;
    const playtime = isFortnite ? 842 * 3600 + 17 * 60 : played ? Math.round(Math.exp(between(rand, Math.log(600), Math.log(260 * 3600)))) : 0;

    const cloud = !isFortnite && !noCloud.has(i) && tp !== "other" && !eaOnly.has(i);
    let saveStatus: SaveSyncStatus = !cloud ? "notSupported" : isInstalled ? "inSync" : "unknown";
    if (cloud && isInstalled) {
      if (i === conflictIdx) saveStatus = "conflict";
      else if (localNewer.has(i)) saveStatus = "localNewer";
      else if (cloudNewer.has(i)) saveStatus = "cloudNewer";
      else if (saveError.has(i)) saveStatus = "error";
      else if (saveOff.has(i)) saveStatus = "disabled";
    }

    const sharedWithEgl = isFortnite || (isInstalled && !noEglShare.has(i) && tp === "none" && rand() < 0.5);
    const installedVersion = isInstalled ? (isFortnite ? OLD_VERSION : `${1 + Math.floor(rand() * 3)}.${Math.floor(rand() * 9)}.${Math.floor(rand() * 20)}`) : null;
    const latest = notInstallable ? null : isFortnite ? LATEST_VERSION : hasUpdate && installedVersion ? bump(installedVersion) : (installedVersion ?? `${1 + Math.floor(rand() * 3)}.${Math.floor(rand() * 9)}.${Math.floor(rand() * 20)}`);
    const measured = isInstalled && !unmeasured.has(i) ? Math.round(installBytes * between(rand, 0.97, 1.0)) : null;

    const tallMissing = missingTall.has(i);
    const wideMissing = missingWide.has(i);
    const art = isFortnite
      ? opts.art
        ? FORTNITE_ART
        : { tall: null, tallThumb: null, wide: null, logo: null, placeholder: null }
      : artFor(appName, { tall: !tallMissing, wide: !wideMissing });

    const hasDlc = dlcHosts.has(i);
    const summary: GameSummary = {
      appName,
      title,
      kind: "game",
      baseAppName: null,
      namespace: hex(rand, 32),
      catalogItemId: hex(rand, 32),
      developer: isFortnite ? "Epic Games" : pick(rand, STUDIOS),
      installStatus: status,
      installedVersion,
      latestVersion: latest,
      updateAvailable: isInstalled && hasUpdate && !isBroken,
      installPath,
      installSizeBytes: notInstallable ? null : installBytes,
      installedSizeBytes: measured,
      downloadSizeBytes: notInstallable ? null : isInstalled ? (hasUpdate ? updateDl : null) : downloadFull,
      cloudSaves: cloud,
      saveStatus,
      canRunOffline: !offlineNo.has(i) && !isFortnite,
      thirdParty: tp,
      thirdPartyName: thirdPartyName(tp),
      dlcCount: 0,
      favorite: false,
      hidden: hiddenSet.has(i),
      addedAt: iso(isFortnite ? now - 3.4 * 365 * DAY : addedAt),
      lastPlayedAt: lastPlayed === null ? null : iso(lastPlayed),
      playtimeSeconds: playtime,
      art,
      runState: "idle",
      activeJobId: null,
      activeJobKind: null,
      sharedWithEgl: isInstalled && sharedWithEgl,
      needsVerification: unverified.has(i) && isInstalled,
    };
    const hasPrereqIssue = needsPrereq.has(i);
    const comps = i % 4 === 0 || isFortnite ? makeComponents(rand, isFortnite, installBytes) : [];
    const prereqs: Prerequisite[] = isFortnite
      ? [{ id: "eac", name: "Easy Anti-Cheat", installed: true }]
      : i % 3 === 0 || hasPrereqIssue
        ? [
            { id: "vcredist", name: "Visual C++ Redistributable 2015-2022", installed: !hasPrereqIssue },
            { id: "dx", name: "DirectX Runtime", installed: true },
          ]
        : [];

    const game: MockGame = {
      summary,
      description: isFortnite ? "Battle Royale, Zero Build, Creative and more. Installed by Glider v0.1." : pick(rand, DESCRIPTIONS),
      publisher: isFortnite ? "Epic Games" : pick(rand, PUBLISHERS),
      releaseDate: iso(now - Math.floor(between(rand, 0.4, 9) * 365 * DAY)),
      dlc: [],
      components: comps,
      prerequisites: prereqs,
      saves: cloud
        ? {
            supported: true,
            enabled: saveStatus !== "disabled",
            status: saveStatus,
            savePath: isInstalled ? `C:\\Users\\alex\\AppData\\Local\\${folderName}\\Saved` : null,
            localModifiedAt: isInstalled ? iso(now - Math.floor(rand() * 20 * DAY)) : null,
            cloudModifiedAt: iso(now - Math.floor(rand() * 20 * DAY)),
            lastSyncedAt: isInstalled ? iso(now - Math.floor(rand() * 25 * DAY)) : null,
            lastError:
              saveStatus === "error"
                ? { code: "saveSyncFailed", title: "Cloud saves didn't sync", message: `Glider couldn't sync ${title}'s saves with Epic. You can play without syncing.`, fix: "retry", detail: "HTTP 500", appName }
                : null,
            backups: isInstalled ? Math.floor(rand() * 5) : 0,
            conflict: null,
            note: null,
          }
        : null,
      conflict: null,
      settings: defaultSettings(),
      launchOptions: i % 9 === 0 ? [{ name: "DirectX 11", args: "-dx11" }, { name: "DirectX 12", args: "-dx12" }] : [],
      shortcuts: { desktop: isInstalled && rand() < 0.3, startMenu: isInstalled && rand() < 0.4 },
      quirks: isFortnite ? ["Easy Anti-Cheat"] : tp !== "none" ? [`Launches through ${thirdPartyName(tp)}`] : [],
      antiCheat: isFortnite ? "easyAntiCheat" : i % 11 === 0 ? "battlEye" : i % 7 === 0 ? "easyAntiCheat" : "none",
      executable: isInstalled ? `${folderName}\\Binaries\\Win64\\${folderName}.exe` : null,
      installedAt: isInstalled ? iso(now - Math.floor(rand() * 400 * DAY) - 2 * DAY) : null,
      eglManifestPath: sharedWithEgl && isInstalled ? `C:\\ProgramData\\Epic\\EpicGamesLauncher\\Data\\Manifests\\${hex(rand, 32).toUpperCase()}.item` : null,
      folderName,
      fullDownloadBytes: downloadFull,
      fullInstallBytes: installBytes,
      updateDownloadBytes: updateDl,
      updateWriteBytes: updateWrite,
      needsActivation: false,
      badFiles: isBroken ? 3 + Math.floor(rand() * 40) : 0,
      badBytes: isBroken ? Math.round(between(rand, 40 * MiB, 2.4 * GiB)) : 0,
    };
    if (i === conflictIdx && game.saves) {
      game.conflict = {
        appName,
        title,
        localModifiedAt: game.saves.localModifiedAt,
        cloudModifiedAt: game.saves.cloudModifiedAt,
        localSizeBytes: Math.round(between(rand, 0.3, 6) * MiB),
        cloudSizeBytes: Math.round(between(rand, 0.3, 6) * MiB),
        lastSyncedAt: game.saves.lastSyncedAt,
      };
    }
    if (isFortnite) {
      game.summary = { ...game.summary, favorite: true, runState: "idle" };
      game.settings = { ...game.settings, autoUpdate: "default", cloudSaves: false };
    }
    if (hasDlc && !isFortnite) addDlc(game, i);
    return game;
  };

  const addDlc = (host: MockGame, i: number): void => {
    const n = 1 + Math.floor(rand() * 3);
    const hostSummary = host.summary;
    for (let k = 0; k < n; k++) {
      const addon = addonHosts.has(i) && k === 0;
      const entitlementOnly = !addon && chance(rand, 0.4);
      const isInst = hostSummary.installStatus === "installed" && !entitlementOnly && chance(rand, 0.6);
      const title = addon ? `${hostSummary.title}: Side Stories` : `${hostSummary.title} ${pick(rand, DLC_SUFFIX)}`;
      const appName = appNameFor(rand, apps);
      const size = Math.round(between(rand, 20 * MiB, entitlementOnly ? 20 * MiB : 6 * GiB));
      const kind: GameKind = addon ? "launchableAddon" : "dlc";
      const status: InstallStatus = entitlementOnly ? "notInstallable" : isInst ? "installed" : "notInstalled";
      const summary: GameSummary = {
        ...hostSummary,
        appName,
        title,
        kind,
        baseAppName: hostSummary.appName,
        namespace: hostSummary.namespace,
        catalogItemId: hex(rand, 32),
        installStatus: status,
        installedVersion: isInst ? hostSummary.installedVersion : null,
        latestVersion: entitlementOnly ? null : hostSummary.latestVersion,
        updateAvailable: false,
        installPath: isInst || (addon && hostSummary.installPath) ? hostSummary.installPath : null,
        installSizeBytes: entitlementOnly ? null : size,
        installedSizeBytes: isInst ? size : null,
        downloadSizeBytes: entitlementOnly || isInst ? null : Math.round(size * 0.6),
        cloudSaves: false,
        saveStatus: "notSupported",
        canRunOffline: hostSummary.canRunOffline,
        dlcCount: 0,
        favorite: false,
        hidden: false,
        lastPlayedAt: addon && isInst ? hostSummary.lastPlayedAt : null,
        playtimeSeconds: addon && isInst ? Math.round(hostSummary.playtimeSeconds * 0.1) : 0,
        sharedWithEgl: false,
        needsVerification: false,
        art: artFor(appName, { tall: true, wide: !addon }),
        activeJobId: null,
        activeJobKind: null,
        runState: "idle",
        thirdParty: hostSummary.thirdParty,
        thirdPartyName: hostSummary.thirdPartyName,
      };
      dlcOut.push({
        ...host,
        summary,
        description: `Extra content for ${hostSummary.title}.`,
        dlc: [],
        components: [],
        prerequisites: [],
        saves: null,
        conflict: null,
        launchOptions: [],
        shortcuts: { desktop: false, startMenu: false },
        quirks: [],
        antiCheat: "none",
        executable: addon && isInst ? `${host.folderName}\\SideStories.exe` : null,
        eglManifestPath: null,
        folderName: folderNameOf(title),
        fullDownloadBytes: Math.round(size * 0.6),
        fullInstallBytes: size,
        updateDownloadBytes: 0,
        updateWriteBytes: 0,
        needsActivation: false,
        badFiles: 0,
        badBytes: 0,
      });
      host.dlc.push(appName);
    }
    host.summary = { ...host.summary, dlcCount: host.dlc.length };
  };

  for (let i = 0; i < count; i++) out.push(makeGame(i));

  // ---- post-pass: favorites, the activation case ----
  const favPool = out.filter((g) => g.summary.installStatus === "installed" && g.summary.appName !== FORTNITE);
  const favCount = scaleTo(count, 12, 2);
  for (const g of shuffled(rand, favPool).slice(0, favCount)) g.summary = { ...g.summary, favorite: true };
  for (const g of shuffled(rand, out.filter((x) => !x.summary.favorite && x.summary.installStatus === "notInstalled")).slice(0, scaleTo(count, 4, 1))) {
    g.summary = { ...g.summary, favorite: true };
  }
  const activation = out.find((g) => g.summary.thirdParty === "ubisoft" && g.summary.installStatus === "installed");
  if (activation) activation.needsActivation = true;

  return [...out, ...dlcOut];
}

function bump(version: string): string {
  const parts = version.split(".").map((n) => Number(n));
  parts[parts.length - 1] = (parts[parts.length - 1] ?? 0) + 1;
  return parts.join(".");
}

function makeComponents(rand: Rand, fortnite: boolean, installBytes: number): InstallComponent[] {
  if (fortnite) {
    return [
      { id: "base", name: "Game", description: "Everything you need to play.", sizeBytes: Math.round(installBytes * 0.8), required: true, selected: true },
      { id: "hires", name: "High-resolution textures", description: "Sharper art on 4K displays.", sizeBytes: Math.round(installBytes * 0.2), required: false, selected: false },
    ];
  }
  const extras: [string, string][] = [
    ["Voice-over: Spanish", "Spanish voice acting."],
    ["Voice-over: Japanese", "Japanese voice acting."],
    ["High-resolution textures", "Sharper art on 4K displays."],
    ["Cinematics", "Pre-rendered story scenes."],
  ];
  const picked = shuffled(rand, extras).slice(0, 2 + Math.floor(rand() * 2));
  return [
    { id: "base", name: "Game", description: "Everything you need to play.", sizeBytes: Math.round(installBytes * 0.7), required: true, selected: true },
    ...picked.map(([name, description], i) => ({
      id: `tag-${hashString(name) % 1000}`,
      name,
      description,
      sizeBytes: Math.round(installBytes * (0.05 + i * 0.04)),
      required: false,
      selected: i === 0,
    })),
  ];
}
