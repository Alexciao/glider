// Stands in for features/downloads/ActiveJobCard in the mockup build only (see vite.config.mjs). `?card=a|b|c|d` picks a
// direction; anything else renders the live card. The other exports pass through so the rest of the page is unchanged.
import { ActiveJobCard as Live } from "../../src/features/downloads/ActiveJobCard";
import { CardA } from "./CardA";
import { CardB } from "./CardB";
import { CardC } from "./CardC";
import { CardD } from "./CardD";

export { JobSparkline, KIND_ICON } from "../../src/features/downloads/ActiveJobCard";

const pick = () => new URLSearchParams(location.search).get("card");

export function ActiveJobCard(props) {
  switch (pick()) {
    case "a":
      return <CardA {...props} />;
    case "b":
      return <CardB {...props} />;
    case "c":
      return <CardC {...props} />;
    case "d":
      return <CardD {...props} />;
    default:
      return <Live {...props} />;
  }
}
