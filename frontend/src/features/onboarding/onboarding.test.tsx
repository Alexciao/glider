import { describe, expect, it } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import { rpc } from "../../ipc/rpc";
import { useRouter } from "../../state/router";
import { useApp } from "../../state/store";
import { mount, installCleanStores } from "../../test/harness";
import { Onboarding } from "./Onboarding";

installCleanStores();

const onboarding = () => useApp.getState().snapshot!.onboarding;
const FAST = { timeScale: 400 } as const;

describe("Onboarding: Welcome and Sign in", () => {
  it("starts at Welcome with one headline and one sentence, and persists the step on Get started", async () => {
    const { user } = await mount("firstRun", <Onboarding />);
    expect(screen.getByRole("heading", { name: /Your Epic games,\s*without the weight/ })).toBeInTheDocument();
    expect(screen.queryByRole("list")).not.toBeInTheDocument(); // no feature bullets
    await user.click(screen.getByRole("button", { name: "Get started" }));
    expect(await screen.findByRole("button", { name: "Sign in with Epic" })).toBeInTheDocument();
    expect(onboarding().step).toBe("signIn");
  });

  it("a closed sign-in window shows a calm message with Copy diagnostic logs", async () => {
    const { user, controller } = await mount("firstRun", <Onboarding />);
    await user.click(screen.getByRole("button", { name: "Get started" }));
    const signIn = await screen.findByRole("button", { name: "Sign in with Epic" });
    controller.failNext("authCancelled");
    await user.click(signIn);
    expect(await screen.findByText("Sign-in was canceled")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Copy diagnostic logs" })).toBeInTheDocument();
  });

  it("offline: sign-in is unavailable, and Continue without signing in skips both account steps", async () => {
    const { user, controller } = await mount("firstRun", <Onboarding />);
    controller.setConnectivity("offline");
    await user.click(screen.getByRole("button", { name: "Get started" }));
    expect(await screen.findByText(/You're offline/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Sign in with Epic" })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Continue without signing in" }));
    expect(await screen.findByText("Where should games install?")).toBeInTheDocument();
    expect(onboarding().skipped).toEqual(expect.arrayContaining(["signIn", "scan"]));
  });

  it("resumes on the persisted step after a restart", async () => {
    await mount("firstRunSignedIn", <Onboarding />);
    await rpc().call("onboarding.setStep", { step: "mode", skipped: false });
    expect(await screen.findByText("How should downloads behave?")).toBeInTheDocument();
  });
});

describe("Onboarding: Find games", () => {
  it("lists ready games one line each, all selected, and the rest with a short reason", async () => {
    await mount("firstRunSignedIn", <Onboarding />);
    expect(await screen.findByText(/Found \d+ games? on this PC/)).toBeInTheDocument();
    const boxes = screen.getAllByRole("checkbox");
    const rows = boxes.filter((b) => b.getAttribute("aria-labelledby"));
    expect(rows.length).toBeGreaterThanOrEqual(5);
    expect(rows.every((b) => b.getAttribute("aria-checked") === "true")).toBe(true);
    for (const label of ["Folder missing", "Launcher is busy", "Not in your library", "Already in Glider"]) expect(screen.getAllByText(label).length).toBeGreaterThan(0);
    // The backend's long reasons are tooltips, not text on the page.
    expect(screen.queryByText(/isn't at this location any more/)).not.toBeInTheDocument();
  });

  it("select all / none, and Import is disabled with nothing selected", async () => {
    const { user } = await mount("firstRunSignedIn", <Onboarding />);
    await screen.findByText(/Found \d+ games? on this PC/);
    await user.click(screen.getByRole("checkbox", { name: "Deselect all" }));
    expect(screen.getByRole("button", { name: "Import" })).toBeDisabled();
    await user.click(screen.getByRole("checkbox", { name: "Select all" }));
    expect(screen.getByRole("button", { name: /^Import \d+ games$/ })).toBeEnabled();
  });

  it("imports the selection and reports each game briefly, then says nothing was downloaded", async () => {
    const { user } = await mount("firstRunSignedIn", <Onboarding />, FAST);
    await screen.findByText(/Found \d+ games? on this PC/);
    await user.click(screen.getByRole("checkbox", { name: "Deselect all" }));
    const first = screen.getAllByRole("checkbox").find((b) => b.getAttribute("aria-labelledby"))!;
    await user.click(first);
    await user.click(screen.getByRole("button", { name: "Import 1 game" }));
    expect(await screen.findByText(/^1 game ready$/, undefined, { timeout: 8000 })).toBeInTheDocument();
    expect(screen.getByText("Nothing was downloaded.")).toBeInTheDocument();
    expect(within(screen.getByRole("list", { name: "Import results" })).getAllByRole("listitem")).toHaveLength(1);
    await user.click(screen.getByRole("button", { name: "Continue" }));
    expect(await screen.findByText("Where should games install?")).toBeInTheDocument();
  });

  it("the result of each game is what Core's check found: no Repair button before a check says there is damage", async () => {
    const { user } = await mount("firstRunSignedIn", <Onboarding />, FAST);
    await screen.findByText(/Found \d+ games? on this PC/);
    await user.click(screen.getByRole("checkbox", { name: "Deselect all" }));
    for (const box of screen.getAllByRole("checkbox").filter((b) => b.getAttribute("aria-labelledby")).slice(0, 2)) await user.click(box);
    await user.click(screen.getByRole("button", { name: "Import 2 games" }));
    const list = await screen.findByRole("list", { name: "Import results" });
    // Everything settles: each row reads Ready, Repaired · n files or Needs repair · n files, and the headline counts them.
    await waitFor(() => expect(screen.getByText(/games? ready/)).toBeInTheDocument(), { timeout: 20000 });
    for (const row of within(list).getAllByRole("listitem")) expect(row).toHaveTextContent(/Ready|Repaired · \d+ files?|Needs repair|Failed/);
    expect(within(list).queryByText("Checking files")).toBeNull();
    expect(within(list).queryByRole("button", { name: /^Repair / })).toBeNull();
    expect(screen.getByText("Nothing was downloaded.")).toBeInTheDocument();
  }, 40_000);

  it("a failed import says why in a few words and offers Try again", async () => {
    const { user, controller } = await mount("firstRunSignedIn", <Onboarding />, { timeScale: 1 });
    await screen.findByText(/Found \d+ games? on this PC/);
    await user.click(screen.getByRole("checkbox", { name: "Deselect all" }));
    await user.click(screen.getAllByRole("checkbox").find((b) => b.getAttribute("aria-labelledby"))!);
    await user.click(screen.getByRole("button", { name: "Import 1 game" }));
    await waitFor(() => expect(useApp.getState().snapshot!.queue.jobs.some((j) => j.kind === "import" && j.status === "running")).toBe(true), { timeout: 4000 });
    controller.failActiveJob("filesLocked");
    expect(await screen.findByText("Failed · folder in use")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Try .* again/ })).toBeInTheDocument();
  });

  it("an empty scan is a short invitation and still lets the user continue", async () => {
    const { user } = await mount("firstRunSignedIn", <Onboarding />, { games: 0 });
    expect(await screen.findByText("No games found on this PC")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Scan all drives" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Continue" }));
    expect(await screen.findByText("Where should games install?")).toBeInTheDocument();
  });

  it("Skip moves on and remembers it was skipped", async () => {
    const { user } = await mount("firstRunSignedIn", <Onboarding />);
    await screen.findByText(/Found \d+ games? on this PC/);
    await user.click(screen.getByRole("button", { name: "Skip" }));
    expect(await screen.findByText("Where should games install?")).toBeInTheDocument();
    expect(onboarding().skipped).toContain("scan");
  });

  it("locate another folder: an invalid one explains why", async () => {
    const { user } = await mount("firstRunSignedIn", <Onboarding />, FAST);
    await screen.findByText(/Found \d+ games? on this PC/);
    await user.click(screen.getByRole("button", { name: /Locate another folder/ })); // first pick = a valid game folder
    await waitFor(() => expect(screen.getByRole("list", { name: "Import results" })).toBeInTheDocument(), { timeout: 4000 });
    // The import ran: the row says what happens to the files now (Core queues a check for a folder it knows nothing about).
    await within(screen.getByRole("list", { name: "Import results" })).findByText(/^(Ready|Checking files|Repaired)/, undefined, { timeout: 8000 });
    await user.click(screen.getByRole("button", { name: /Locate another folder/ })); // second pick = not a game
    expect(await screen.findByText("Not a game folder")).toBeInTheDocument();
  });
});

describe("Onboarding: Folders, Downloads, Done", () => {
  it("walks Folders → Downloads → Done and completes onboarding on Open Glider", async () => {
    const { user } = await mount("firstRunSignedIn", <Onboarding />);
    await rpc().call("onboarding.setStep", { step: "folders", skipped: false });
    expect(await screen.findByText("Where should games install?")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Continue" })).toBeDisabled(); // no library folder yet
    await user.click(screen.getByRole("button", { name: "Add folder…" }));
    const radios = await screen.findAllByRole("radio", { name: /\\/ });
    expect(radios).toHaveLength(1);
    expect(radios[0]).toBeChecked(); // the first folder is the default
    await user.click(screen.getByRole("button", { name: "Continue" }));

    expect(await screen.findByText("How should downloads behave?")).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: /Balanced/ })).toBeChecked();
    expect(screen.getByText("Recommended")).toBeInTheDocument();
    await user.click(screen.getByRole("radio", { name: /^Full/ }));
    await waitFor(() => expect(useApp.getState().snapshot!.settings.downloadMode).toBe("full"));
    await user.click(screen.getByRole("button", { name: "Continue" }));

    expect(await screen.findByText("You're all set")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Go to your library" }));
    await waitFor(() => expect(onboarding().completed).toBe(true));
    expect(useRouter.getState().route).toEqual({ page: "library" });
  });

  it("the stepper shows skipped steps as skipped", async () => {
    await mount("firstRunSignedIn", <Onboarding />);
    await rpc().call("onboarding.setStep", { step: "folders", skipped: true });
    await rpc().call("onboarding.setStep", { step: "mode", skipped: true });
    const nav = await screen.findByRole("navigation", { name: "Setup progress" });
    await waitFor(() => expect(within(nav).getByText(/Folders/).textContent).toMatch(/skipped/));
  });
});
