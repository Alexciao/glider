/**
 * Pure geometry for the virtualized library. Every row has the same height, so the scroll window is arithmetic and tiles
 * never move once placed (no layout shift while art loads).
 */
export const GRID_GAP = 20;
export const MIN_TILE = 168;
export const MAX_TILE = 232;
/** The title line (20) and the 8 px gap above it. */
export const TILE_TEXT = 28;
export const ROW_GAP = 24;

export const LIST_ROW = 64;

/** Horizontal page padding for a scroller of this width (matches the other pages' gutters). */
export const padFor = (containerWidth: number): number => (containerWidth >= 1000 ? 40 : 28);

export interface GridLayout {
  cols: number;
  tileW: number;
  coverH: number;
  rowH: number;
  /** Left padding of the grid inside the scroller. */
  pad: number;
}

export function gridLayout(containerWidth: number): GridLayout {
  const pad = padFor(containerWidth);
  const inner = Math.max(MIN_TILE, containerWidth - pad * 2);
  const cols = Math.max(2, Math.min(12, Math.floor((inner + GRID_GAP) / (MIN_TILE + GRID_GAP))));
  // Tiles stop growing at MAX_TILE; leftover width centres the grid instead of stretching art.
  const tileW = Math.min(MAX_TILE, Math.floor((inner - GRID_GAP * (cols - 1)) / cols));
  const used = tileW * cols + GRID_GAP * (cols - 1);
  const coverH = Math.round((tileW * 4) / 3);
  return { cols, tileW, coverH, rowH: coverH + TILE_TEXT + ROW_GAP, pad: pad + Math.max(0, Math.floor((inner - used) / 2)) };
}

/** Which list columns fit. The name column always stays; the rest drop in this order as the window narrows. */
export interface ListColumns {
  status: boolean;
  size: boolean;
  played: boolean;
}

export function listColumns(containerWidth: number): ListColumns {
  const inner = containerWidth - padFor(containerWidth) * 2;
  return { status: inner >= 520, size: inner >= 640, played: inner >= 760 };
}
