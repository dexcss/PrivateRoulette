import { defineConfig } from "vite";

// base './' so the build works from GitHub Pages project subpaths
export default defineConfig({
  base: "./",
  build: { target: "es2022" },
});
