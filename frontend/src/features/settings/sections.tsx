import type { ReactNode } from "react";
import { Cloud, Download, Gamepad2, HardDrive, Info, Layers, Link2, Palette, Power, RefreshCw, SlidersHorizontal, UserRound } from "lucide-react";
import type { SettingsSection } from "../../state/ui";

export type SectionId = Exclude<SettingsSection, "general">;

export interface SectionDef {
  id: SectionId;
  label: string;
  icon: ReactNode;
  /** Extra words the command palette and search match ("speed", "cdn"…). */
  keywords: string;
}

/** Nav order. A divider is drawn between the groups. */
export const SECTION_GROUPS: SectionDef[][] = [
  [
    { id: "downloads", label: "Downloads", icon: <Download size={16} />, keywords: "speed limit mode bandwidth cdn workers cache memory idle playing" },
    { id: "library", label: "Library folders", icon: <HardDrive size={16} />, keywords: "install location drive disk space default folder" },
    { id: "updates", label: "Updates", icon: <RefreshCw size={16} />, keywords: "auto update check interval patch" },
    { id: "saves", label: "Cloud saves", icon: <Cloud size={16} />, keywords: "backup sync progress" },
  ],
  [
    { id: "egl", label: "Epic Games Launcher", icon: <Gamepad2 size={16} />, keywords: "egl epic launcher import scan uninstall sync" },
    { id: "overlay", label: "EOS overlay", icon: <Layers size={16} />, keywords: "epic online services eos friends achievements" },
    { id: "links", label: "Links", icon: <Link2 size={16} />, keywords: "protocol com.epicgames.launcher glider:// url handler" },
  ],
  [
    { id: "appearance", label: "Appearance", icon: <Palette size={16} />, keywords: "theme dark light system color" },
    { id: "startup", label: "Startup and tray", icon: <Power size={16} />, keywords: "start with windows minimized tray close notifications" },
    { id: "account", label: "Account", icon: <UserRound size={16} />, keywords: "sign in sign out epic login profile" },
  ],
  [
    { id: "advanced", label: "Advanced", icon: <SlidersHorizontal size={16} />, keywords: "logs diagnostics debug reset setup onboarding data folder" },
    { id: "about", label: "About and licenses", icon: <Info size={16} />, keywords: "version legendary licenses" },
  ],
];

export const SECTIONS: SectionDef[] = SECTION_GROUPS.flat();

/** v0.1's "general" page is now Startup and tray. */
export const resolveSection = (id: SettingsSection): SectionId => (id === "general" ? "startup" : id);

export const sectionById = (id: SettingsSection): SectionDef => SECTIONS.find((s) => s.id === resolveSection(id)) ?? SECTIONS[0]!;
