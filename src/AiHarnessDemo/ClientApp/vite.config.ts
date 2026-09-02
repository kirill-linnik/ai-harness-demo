/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// AI Harness Studio client: builds directly into the ASP.NET Core wwwroot folder so
// `dotnet build`/`dotnet publish` produce a single deployable host. See src/AiHarnessDemo/AiHarnessDemo.csproj
// for the MSBuild target that invokes `npm run build`.
export default defineConfig({
  plugins: [
    react(),
    {
      name: "normalize-index-line-endings",
      transformIndexHtml: {
        order: "post",
        handler: html => html.replaceAll("\r\n", "\n")
      }
    }
  ],
  build: {
    outDir: "../wwwroot",
    emptyOutDir: true
  },
  server: {
    proxy: {
      "/api": {
        target: "http://localhost:5283",
        changeOrigin: false
      }
    }
  },
  test: {
    environment: "jsdom",
    globals: false,
    setupFiles: ["./src/test/setup.ts"],
    css: false
  }
});
