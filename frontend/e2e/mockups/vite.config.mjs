// Scratch mockups of the active download card (docs/design-download-card.md). Not product code.
// The real app and mock backend, with one swap: Downloads.tsx's `./ActiveJobCard` resolves to CardSwitch.jsx, which renders
// the live card or a mockup picked by `?card=current|a|b|c`. Everything else (tokens, fonts, ui kit, progress store) is the app's.
//   pnpm exec vite --config e2e/mockups/vite.config.mjs --port 5988
//   pnpm exec vite build --config e2e/mockups/vite.config.mjs && pnpm exec vite preview --config e2e/mockups/vite.config.mjs --port 5988
import { fileURLToPath } from "node:url";
import { mergeConfig } from "vite";
import base from "../../vite.config.ts";

const switcher = fileURLToPath(new URL("./CardSwitch.jsx", import.meta.url));

export default mergeConfig(base, {
  plugins: [
    {
      name: "glider-mockup-card",
      enforce: "pre",
      resolveId(source, importer) {
        if (source === "./ActiveJobCard" && importer?.replace(/\\/g, "/").endsWith("features/downloads/Downloads.tsx")) return switcher;
        return null;
      },
    },
  ],
  build: { outDir: fileURLToPath(new URL("../../node_modules/.mockups-dist", import.meta.url)) },
});
