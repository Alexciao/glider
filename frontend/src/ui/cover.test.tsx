import { describe, expect, it } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import type { GameArt } from "../ipc/contract.gen";
import { GameCover, ProgressRing, coverSources } from "./index";

const art = (over: Partial<GameArt> = {}): GameArt => ({ tall: "data:image/svg+xml,tall", tallThumb: "data:image/svg+xml,thumb", wide: "data:image/svg+xml,wide", logo: null, placeholder: "#2a1d5e", ...over });
const NONE: GameArt = { tall: null, tallThumb: null, wide: null, logo: null, placeholder: null };

describe("GameCover", () => {
  it("shows the placeholder colour and a blurred thumbnail, then fades the real image in", () => {
    const { container } = render(<GameCover art={art()} title="Atlas" />);
    const cover = container.querySelector<HTMLElement>("[data-cover]")!;
    expect(cover.style.backgroundColor).toBe("rgb(42, 29, 94)");
    const imgs = container.querySelectorAll("img");
    expect(imgs).toHaveLength(2); // blurred thumb + full image
    const full = imgs[1]!;
    expect(full).toHaveAttribute("loading", "lazy");
    expect(full.className).toContain("opacity-0");
    expect(cover).not.toHaveAttribute("data-loaded");
    fireEvent.load(full);
    expect(cover).toHaveAttribute("data-loaded");
    expect(full.className).toContain("opacity-100");
  });

  it("a thumb-size cover is one download: the 300 px image, no blurred twin", () => {
    const { container } = render(<GameCover art={art()} title="Atlas" size="thumb" />);
    const imgs = container.querySelectorAll("img");
    expect(imgs).toHaveLength(1);
    expect(imgs[0]).toHaveAttribute("src", "data:image/svg+xml,thumb");
    expect(container.querySelector<HTMLElement>("[data-cover]")!.style.backgroundImage).toContain("color-mix");
  });

  it("fetches above-the-fold covers at high priority", () => {
    const { container } = render(<GameCover art={art()} title="Atlas" size="thumb" priority />);
    expect(container.querySelector("img")).toHaveAttribute("fetchpriority", "high");
  });

  it("is eager when it is above the fold", () => {
    const { container } = render(<GameCover art={art()} title="Atlas" priority />);
    expect(container.querySelectorAll("img")[1]).toHaveAttribute("loading", "eager");
  });

  it("falls back to initials with no art, and when the image breaks", () => {
    const { container, unmount } = render(<GameCover art={NONE} title="Atlas of Dusk" />);
    expect(container.querySelector("img")).toBeNull();
    expect(screen.getByText("AD")).toBeInTheDocument();
    unmount();
    const broken = render(<GameCover art={art()} title="Brazen Tide" />);
    fireEvent.error(broken.container.querySelectorAll("img")[1]!);
    expect(screen.getByText("BT")).toBeInTheDocument();
  });

  it("picks sources by shape and size", () => {
    expect(coverSources(art(), "tall", "full")).toEqual({ src: "data:image/svg+xml,tall", blur: "data:image/svg+xml,thumb" });
    expect(coverSources(art(), "tall", "thumb")).toEqual({ src: "data:image/svg+xml,thumb", blur: null });
    expect(coverSources(art(), "wide", "full").src).toBe("data:image/svg+xml,wide");
    expect(coverSources(art({ wide: null }), "wide", "full").src).toBe("data:image/svg+xml,tall");
  });
});

describe("ProgressRing", () => {
  it("exposes a determinate value and an indeterminate state", () => {
    const { rerender } = render(<ProgressRing value={42.7} label="Installing" />);
    const ring = screen.getByRole("progressbar", { name: "Installing" });
    expect(ring).toHaveAttribute("aria-valuenow", "42");
    rerender(<ProgressRing value={null} label="Installing" />);
    expect(screen.getByRole("progressbar")).not.toHaveAttribute("aria-valuenow");
  });

  it("clamps out-of-range values", () => {
    render(<ProgressRing value={180} label="x" />);
    expect(screen.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "100");
  });
});
