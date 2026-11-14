import { ArrowDownToLine, Download, FolderInput, HardDrive, Layers, ShieldCheck, Trash2, Wrench } from "lucide-react";
import type { JobKind } from "../../ipc/contract.gen";

export const KIND_ICON: Record<JobKind, React.ReactNode> = {
  install: <Download size={18} />,
  update: <ArrowDownToLine size={18} />,
  repair: <Wrench size={18} />,
  verify: <ShieldCheck size={18} />,
  move: <FolderInput size={18} />,
  uninstall: <Trash2 size={18} />,
  import: <FolderInput size={18} />,
  modify: <Layers size={18} />,
  overlay: <HardDrive size={18} />,
};

/** Kinds Core can pause (docs/audit-v0.1 §6): the rest would be refused. */
export const PAUSABLE_KINDS: readonly JobKind[] = ["install", "update", "repair", "verify", "modify", "overlay"];
