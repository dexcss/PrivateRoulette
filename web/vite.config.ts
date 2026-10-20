import { defineConfig } from "vite";

// base './' so the build works from GitHub Pages project subpaths
export default defineConfig({
  base: "./",
  build: { target: "es2022" },
  // The demo imports the backend's payout table so the two can never drift.
  server: { fs: { allow: [".."] } },
});
