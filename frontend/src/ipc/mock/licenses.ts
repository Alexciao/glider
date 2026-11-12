import type { LicenseInfo } from "../contract.gen";

const LEGENDARY_TEXT = [
  "Legendary is free software, licensed under the GNU General Public License, version 3 (GPL-3.0).",
  "",
  "Glider bundles an unmodified legendary.exe and runs it as a separate process to talk to Epic's services.",
  "You may use, study, share and modify Legendary under the terms of the GPL-3.0. The complete corresponding",
  "source code for the bundled version (0.21.1) is available at:",
  "https://github.com/legendary-gl/legendary/tree/0.21.1",
].join("\n");

export const LICENSES: LicenseInfo[] = [
  {
    name: "Legendary",
    version: "0.21.1",
    license: "GPL-3.0",
    url: "https://github.com/legendary-gl/legendary/tree/0.21.1",
    text: LEGENDARY_TEXT,
  },
  { name: ".NET", version: "10.0", license: "MIT", url: "https://github.com/dotnet/runtime", text: null },
  {
    name: "Microsoft Edge WebView2",
    version: "1.0.4314.35",
    license: "Microsoft Software License Terms",
    url: "https://www.nuget.org/packages/Microsoft.Web.WebView2",
    text: null,
  },
  { name: "Velopack", version: "1.2.161", license: "MIT", url: "https://github.com/velopack/velopack", text: null },
  { name: "H.NotifyIcon", version: "2.4.1", license: "MIT", url: "https://github.com/HavenDV/H.NotifyIcon", text: null },
  { name: "React", version: "19.3.1", license: "MIT", url: "https://github.com/facebook/react", text: null },
  { name: "Motion", version: "14.13.0", license: "MIT", url: "https://github.com/motiondivision/motion", text: null },
  { name: "Radix UI Primitives", version: "1.8.1", license: "MIT", url: "https://github.com/radix-ui/primitives", text: null },
  { name: "Tailwind CSS", version: "4.3.5", license: "MIT", url: "https://github.com/tailwindlabs/tailwindcss", text: null },
  { name: "Zustand", version: "5.1.0", license: "MIT", url: "https://github.com/pmndrs/zustand", text: null },
  { name: "lucide-react", version: "1.72.0", license: "ISC", url: "https://github.com/lucide-icons/lucide", text: null },
  { name: "Inter", version: "4.1", license: "OFL-1.1", url: "https://github.com/rsms/inter", text: null },
];
