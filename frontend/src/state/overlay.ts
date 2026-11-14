import { rpc } from "../ipc/rpc";
import { toGliderError } from "./actions";
import { toast } from "./notices";

/** Removes the Epic overlay (the snapshot catches up with the host's next `state` event). */
export async function removeOverlay(): Promise<void> {
  try {
    await rpc().call("overlay.remove");
  } catch (e) {
    const err = toGliderError(e);
    toast({ level: "error", title: err.title, message: err.message, action: err.fix, appName: err.appName });
  }
}
