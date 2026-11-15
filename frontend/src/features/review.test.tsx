import { describe, expect, it } from "vitest";
import { act, screen, waitFor, within } from "@testing-library/react";
import { rpc } from "../ipc/rpc";
import { Rail, railDotStatus } from "../app/Rail";
import { TitleBar } from "../app/TitleBar";
import { navigate } from "../state/router";
import { useApp } from "../state/store";
import { installCleanStores, mount } from "../test/harness";
import { Home } from "./home/Home";

installCleanStores();

describe("the rail says its dot in words", () => {
  it("names each dot colour", () => {
    expect(railDotStatus("danger")).toBe("something couldn't finish");
    expect(railDotStatus("accent")).toBe("downloading");
    expect(railDotStatus("warning")).toBe("paused");
    expect(railDotStatus(null)).toBeUndefined();
  });

  it("Downloads carries 'something couldn't finish' while a failure is listed; Library says updates are waiting", async () => {
    await mount("errors", <Rail />);
    expect(screen.getByRole("button", { name: "Downloads, something couldn't finish" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Library, updates available" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Home" })).toBeInTheDocument();
  });

  it("a quiet queue adds nothing to the name", async () => {
    await mount("emptyLibrary", <Rail />);
    expect(screen.getByRole("button", { name: "Downloads" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Library" })).toBeInTheDocument();
  });

  it("the default mock world has no failed job, so nothing is red out of the box", async () => {
    await mount("returning", <Rail />);
    expect(useApp.getState().snapshot!.queue.jobs.some((j) => j.status === "failed")).toBe(false);
    expect(screen.getByRole("button", { name: /^Downloads$/ })).toBeInTheDocument();
  });
});

describe("one Sign in on screen", () => {
  const signedOut = () => rpc().call("auth.signOut");

  it("Home, Library and a game page carry their own: the title bar's steps aside; Downloads, Settings and Store keep it", async () => {
    await mount("returning", <TitleBar />);
    expect(screen.queryByRole("button", { name: "Sign in" })).toBeNull(); // signed in: the account menu
    await act(async () => {
      await signedOut();
    });
    for (const route of ["home", "library", "game/Fortnite"]) {
      act(() => navigate(route));
      expect(screen.queryByRole("button", { name: "Sign in" })).toBeNull();
    }
    for (const route of ["downloads", "settings", "store"]) {
      act(() => navigate(route));
      expect(await screen.findByRole("button", { name: "Sign in" })).toBeInTheDocument();
    }
  });

  it("Home shows its banner's Sign in and no Updates group whose buttons could not act", async () => {
    await mount("updates", <Home />);
    expect(await screen.findByText("Updates")).toBeInTheDocument();
    await act(async () => {
      await signedOut();
    });
    await waitFor(() => expect(screen.queryByText("Updates")).not.toBeInTheDocument());
    expect(screen.getAllByRole("button", { name: "Sign in" })).toHaveLength(1);
  });
});

describe("Home and failed updates", () => {
  it("does not list an update that sits in Couldn't finish, like 'Update all' and the Downloads count", async () => {
    await mount("errors", <Home />);
    const snap = useApp.getState().snapshot!;
    const failedUpdate = snap.queue.jobs.find((j) => j.status === "failed" && j.kind === "update")!;
    expect(failedUpdate).toBeDefined();
    const panel = (await screen.findByText("Updates")).closest("section") ?? document.body;
    expect(within(panel as HTMLElement).queryByRole("button", { name: `Update ${failedUpdate.title}` })).toBeNull();
  });
});
