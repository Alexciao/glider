/**
 * The page frame: every page's content sits in one centred column of at most `PAGE_FRAME` px, between gutters of 40 px
 * (28 px under a 1100 px window). On a window wider than the frame the spare room is split left and right, so the pages'
 * titles keep ONE shared left edge at any window size, and grids use the frame's whole width (more columns, not wider art).
 * Narrow reading pages stay left-aligned inside the frame, so their edge is the same too. The Store's native view is the one
 * thing that stays full-bleed.
 */
export const PAGE_FRAME = 1440;

/** Gutter beside the frame for a scroller of this width: 40 px, 28 px under a 1100 px window (the content area is the window minus the 72 px rail). */
export const gutterFor = (containerWidth: number): number => (containerWidth >= 1028 ? 40 : 28);

/** Width of the frame's content inside a scroller of this width. */
export const frameWidth = (containerWidth: number): number => Math.min(PAGE_FRAME, containerWidth - gutterFor(containerWidth) * 2);

/** Left edge of the frame's content inside a scroller of this width (what `mx-auto max-w-[1440px]` inside the gutters produces). */
export const frameInset = (containerWidth: number): number => {
  const gutter = gutterFor(containerWidth);
  return gutter + Math.max(0, Math.floor((containerWidth - gutter * 2 - PAGE_FRAME) / 2));
};
