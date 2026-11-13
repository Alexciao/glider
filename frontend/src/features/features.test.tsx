import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setRpcForTests } from "../ipc/rpc";
import type { ScenarioName } from "../ipc/mock";
import { startBridge } from "../state/bridge";
import { resetProgressForTests } from "../state/progress";
import { useApp } from "../state/store";
import { useUi } from "../state/ui";
import { useNotices } from "../state/notices";
import { mockClient } from "../test/fixtures";
import { MotionProvider, TooltipProvider } from "../ui";
import { Dialogs } from "./dialogs/Dialogs";
import { Downloads } from "./downloads/Downloads";
import { Library } from "./library/Library";
import { Onboarding } from "./onboarding/Onboarding";

let stop: (() => void) | undefined;

async function mount(scenario: ScenarioName, ui: React.ReactElement, opts: Parameters<typeof mockClient>[1] = {}) {
  const { client, controller } = mockClient(scenario, opts);
  setRpcForTests(client, controller);
  stop = await startBridge();
  const user = userEvent.setup();
  const view = render(
    <MotionProvider>
      <TooltipProvider>
        {ui}
        <Dialogs />
      </TooltipProvider>
    </MotionProvider>,
  );
  return { user, view, controller };
}

beforeEach(() => {
  useApp.setState({ snapshot: null });
  useUi.setState({ page: "library", dialog: null, errors: {}, pending: {}, readyAt: null });
  useNotices.setState({ items: [] });
  resetProgressForTests();
});
afterEach(() => {
  stop?.();
  setRpcForTests(null);
});

describe("Library", () => {
  it("shows 'Ready to play' and launches through Launching to Playing", async () => {
    const { user, controller } = await mount("installed", <Library />);
    expect(screen.getByText("Ready to play")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /^Play$/ }));
    await waitFor(() => expect(screen.getByRole("button", { name: /Launching|Preparing/ })).toBeDisabled(), { timeout: 3000 });
    await waitFor(() => expect(screen.getByRole("button", { name: /Playing/ })).toBeInTheDocument(), { timeout: 6000 });
    expect(screen.getByText("Fortnite is running")).toBeInTheDocument();
    controller.dispose?.();
  });

  it("offers Update with its size when a newer version exists", async () => {
    await mount("updateAvailable", <Library />);
    expect(screen.getByText("Update available")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Update$/ })).toBeEnabled();
  });

  it("shows playtime, last played and installed size in the facts row", async () => {
    await mount("installed", <Library />);
    expect(screen.getByText("Playtime")).toBeInTheDocument();
    expect(screen.getByText("42 h 17 min")).toBeInTheDocument();
    expect(screen.getByText("Last played")).toBeInTheDocument();
    expect(screen.getByText("Installed size")).toBeInTheDocument();
    expect(screen.queryByText("On disk")).not.toBeInTheDocument();
  });

  it("explains waiting on Epic Games Launcher", async () => {
    await mount("eglBusy", <Library />, { freeze: true });
    expect(screen.getByText(/Epic Games Launcher is updating Fortnite, so Glider will wait/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Waiting/ })).toBeInTheDocument();
  });

  it("shows a failed job's error with its one-click fix", async () => {
    await mount("diskFull", <Library />);
    expect(screen.getByRole("alert")).toHaveTextContent("Not enough disk space");
    expect(within(screen.getByRole("alert")).getByRole("button", { name: "Free up space" })).toBeInTheDocument();
  });

  it("surfaces a launch failure inline with Repair", async () => {
    const { user, controller } = await mount("installed", <Library />);
    controller.failNext("launchFailed");
    await user.click(screen.getByRole("button", { name: /^Play$/ }));
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Couldn't start Fortnite");
    expect(within(alert).getByRole("button", { name: "Repair" })).toBeInTheDocument();
  });
});

describe("Downloads", () => {
  it("renders live progress and pauses/resumes through the real actions", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: false });
    expect(await screen.findByRole("heading", { name: /Installing Fortnite/ })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Pause" }));
    await waitFor(() => expect(screen.getByText("Paused", { selector: "span" })).toBeInTheDocument());
    await user.click(await screen.findByRole("button", { name: "Resume" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Pause" })).toBeInTheDocument());
  });

  it("confirms before cancelling, then returns to an empty state", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: true });
    await user.click((await screen.findAllByRole("button", { name: "Cancel" }))[0]!);
    const dialog = await screen.findByRole("alertdialog");
    expect(dialog).toHaveTextContent("Cancel install?");
    await user.click(within(dialog).getByRole("button", { name: "Cancel install" }));
    await waitFor(() => expect(screen.queryByRole("progressbar", { name: /install progress/i })).not.toBeInTheDocument());
  });

  it("is an invitation, not a void, when there is nothing to show", async () => {
    await mount("signedIn", <Downloads />);
    await act(async () => {
      await useApp.getState().snapshot;
    });
    expect(screen.getByText("Nothing downloading")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Install Fortnite" })).toBeInTheDocument();
  });
});

describe("Onboarding", () => {
  it("adopts an existing EGL install with one click and ends at 'You're all set'", async () => {
    const { user } = await mount("eglDetected", <Onboarding />);
    expect(await screen.findByText("We found your existing Fortnite install")).toBeInTheDocument();
    expect(screen.getByText(/Nothing re-downloads/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Use this install" }));
    expect(await screen.findByText("You're all set", {}, { timeout: 5000 })).toBeInTheDocument();
  });

  it("disables stale candidates and explains why", async () => {
    await mount("eglMultiple", <Onboarding />);
    expect(await screen.findByText("We found 2 Fortnite installs")).toBeInTheDocument();
    const stale = screen.getByRole("radio", { name: /Old Games/ });
    expect(stale).toBeDisabled();
    expect(screen.getByText("This folder no longer exists")).toBeInTheDocument();
  });

  it("starts at Welcome on a fresh machine, then asks for sign-in", async () => {
    const { user } = await mount("fresh", <Onboarding />);
    expect(screen.getByRole("heading", { name: /Install, update and play Fortnite/ })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Get started" }));
    expect(await screen.findByRole("button", { name: /Sign in with Epic/ })).toBeInTheDocument();
  });

  it("browser fallback: pasting Epic's JSON page signs in and advances to the install step", async () => {
    const { user } = await mount("fresh", <Onboarding />);
    await user.click(screen.getByRole("button", { name: "Get started" }));
    await user.click(await screen.findByRole("button", { name: /Use your browser instead/ }));
    const dialog = await screen.findByRole("dialog", { name: "Sign in with your browser" });
    const signIn = within(dialog).getByRole("button", { name: "Sign in" });
    expect(signIn).toBeDisabled();
    await user.click(within(dialog).getByLabelText("Code from Epic's page"));
    await user.paste('{"redirectUrl":"x","authorizationCode":"a1b2c3d4e5f60718293a4b5c6d7e8f90"}');
    await user.click(signIn);
    await waitFor(() => expect(useApp.getState().snapshot?.auth.status).toBe("signedIn"), { timeout: 5000 });
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Sign in with your browser" })).not.toBeInTheDocument());
  });

  it("browser fallback: text without a code shows an inline error and keeps the dialog open", async () => {
    const { user } = await mount("fresh", <Onboarding />);
    await user.click(screen.getByRole("button", { name: "Get started" }));
    await user.click(await screen.findByRole("button", { name: /Use your browser instead/ }));
    const dialog = await screen.findByRole("dialog", { name: "Sign in with your browser" });
    await user.click(within(dialog).getByLabelText("Code from Epic's page"));
    await user.paste("hello");
    await user.click(within(dialog).getByRole("button", { name: "Sign in" }));
    expect(await within(dialog).findByText("That isn't a sign-in code")).toBeInTheDocument();
    expect(useApp.getState().snapshot?.auth.status).toBe("signedOut");
  });

  it("a closed sign-in window shows a calm message with Copy diagnostic logs and the browser route", async () => {
    const { user } = await mount("fresh", <Onboarding />, { fail: "authCancelled" });
    await user.click(screen.getByRole("button", { name: "Get started" }));
    await user.click(await screen.findByRole("button", { name: "Sign in with Epic" }));
    expect(await screen.findByText("Sign-in was cancelled")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Copy diagnostic logs" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /Sign in with your browser instead/ }));
    expect(await screen.findByRole("dialog", { name: "Sign in with your browser" })).toBeInTheDocument();
  });

  it("blocks the fresh install when the drive is too small", async () => {
    const { user } = await mount("signedIn", <Onboarding />);
    const change = await screen.findByRole("button", { name: "Change…" });
    await user.click(change); // D:\Games (plenty)
    await waitFor(() => expect(screen.getByText(/Fits with/)).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: "Change…" })); // E:\Games (18.4 GB free)
    await waitFor(() => expect(screen.getByText(/only 18.4 GB is free/)).toBeInTheDocument());
    expect(screen.getByRole("button", { name: "Install Fortnite" })).toBeDisabled();
  });
});
