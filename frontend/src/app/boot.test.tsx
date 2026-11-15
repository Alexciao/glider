import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { RpcError } from "../ipc/client";
import type { GliderError } from "../ipc/contract.gen";
import { bootErrorDetail } from "./boot";
import { BootFailed } from "./Splash";

const startupFailed: GliderError = {
  code: "unknown",
  title: "Glider couldn't start",
  message: "Glider's background service failed to start: Access to the path is denied.",
  fix: "openLogs",
  detail: "IOException; logs: C:/Glider/logs",
  appName: null,
  requiredBytes: null,
  drive: null,
};

describe("boot failure (M3)", () => {
  it("keeps the backend's explanation from an RpcError and ignores anything else", () => {
    expect(bootErrorDetail(new RpcError(startupFailed))).toEqual(startupFailed);
    expect(bootErrorDetail(new Error("network"))).toBeNull();
    expect(bootErrorDetail(undefined)).toBeNull();
  });

  it("shows why Glider couldn't start and offers the logs", () => {
    render(<BootFailed onRetry={() => undefined} error={startupFailed} />);
    expect(screen.getByText(/Access to the path is denied/)).toBeInTheDocument();
    expect(screen.getByText("IOException; logs: C:/Glider/logs")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Copy diagnostic logs" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
  });

  it("keeps the generic text when the backend gave no reason", () => {
    render(<BootFailed onRetry={() => undefined} />);
    expect(screen.getByText(/couldn't reach its background service/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Copy diagnostic logs" })).not.toBeInTheDocument();
  });
});
