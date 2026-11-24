#!/usr/bin/env node
// Uses dashboard dependencies and Playwright/Chromium installed under .local/artifacts/browser.
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../", import.meta.url));
const dashboard = join(root, "modules/dashboard");
const artifacts = join(root, ".local/artifacts/browser");
process.chdir(dashboard);
process.env.PLAYWRIGHT_BROWSERS_PATH = join(artifacts, "chromium");
const { chromium } = createRequire(join(artifacts, "fixture.cjs"))("playwright");
const { createServer } = await import(createRequire(join(dashboard, "package.json")).resolve("vite"));
const connectFiles = new Map(await Promise.all([
  ["", "html", "text/html"],
  ["connect.js", "js", "text/javascript"],
  ["connect.css", "css", "text/css"],
].map(async ([path, extension, contentType]) => [path, {
  contentType, body: await readFile(join(root, `templates/development-workspaces/t3-connect.${extension}`), "utf8"),
}])));
const virtualId = "\0workspace-t3-fixture";
const html = '<!doctype html><html><body><div id="root"></div><script type="module" src="/@id/__x00__workspace-t3-fixture"></script></body></html>';
const fixture = `
  import { createElement as h } from "react";
  import { createRoot } from "react-dom/client";
  import { WorkspaceOverviewTab } from "/app/modules/Workspaces/WorkspaceOverviewTab.tsx";
  import { WorkspaceSettingsTab } from "/app/modules/Workspaces/WorkspaceSettingsTab.tsx";
  import { loadRuntimeConfig } from "/app/core/config/runtimeConfig.ts";
  import { setCsrfToken } from "/app/api/apiClient.ts";
  import "/app/styles.css";
  await loadRuntimeConfig(async () => new Response(JSON.stringify({
    apiBaseUrl: location.origin, sourceUrl: "https://example.invalid",
    auth: { origin: location.origin, capabilitiesUrl: location.origin + "/api/account/capabilities" }
  })), false);
  setCsrfToken("synthetic-fixture-only");
  const vm = {
    id: "primary", kind: "primary", generation: 3, revision: 1, desiredState: "running", phase: "ready",
    imageId: "image", profile: "Standard", planId: "plan", providerAccountId: "synthetic", region: "fixture",
    sku: "fixture", computeHourlyRate: 0, estimatedMonthlyCompute: 0, computeReserved: true,
    resources: { state: "unsupported" }
  };
  const item = {
    id: "workspace", organizationId: "org", name: "Synthetic workspace", primaryVmId: vm.id, vms: [vm],
    currency: "EUR", revision: 1, volumeSizeGb: 20, effectiveIdleTimeoutMinutes: 30
  };
  const root = createRoot(document.getElementById("root"));
  const refresh = async () => { throw new Error("Fixture must never refresh after a mutation"); };
  window.fixture = { render(settings = false, revision = 1) {
    root.render(h(settings ? WorkspaceSettingsTab : WorkspaceOverviewTab, {
      item: { ...item, vms: [{ ...vm, revision }] }, organizationId: "org", refresh, refreshAfterMutation: refresh,
      canManage: true, canManageRole: true, canManageConnect: true, deletion: null, actorId: "creator"
    }));
  }};
  window.fixture.render();
`;
const server = await createServer({
  root: dashboard, configFile: join(dashboard, "vite.config.ts"),
  server: { host: "127.0.0.1", port: 0, open: false },
  plugins: [{
    name: "credential-free-t3-fixture",
    resolveId(id) { if (id === virtualId) return virtualId; },
    load(id) { if (id === virtualId) return fixture; },
    configureServer(server) {
      server.middlewares.use(async (req, res, next) => {
        if (req.url !== "/fixture.html") return next();
        res.setHeader("content-type", "text/html");
        res.end(await server.transformIndexHtml("/fixture.html", html));
      });
    },
  }],
});
let browser;
let release;
try {
  await server.listen();
  const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
  assert.equal((await fetch(`${origin}/fixture.html`)).status, 200);
  browser = await chromium.launch();
  const context = await browser.newContext({ serviceWorkers: "block" });
  const requests = [];
  const unexpected = [];
  let fail = false;
  let linked = false;
  await context.route("**/*", async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.origin === origin && !url.pathname.startsWith("/api/")) return route.continue();
    if (url.origin === origin && url.pathname === "/api/development-workspaces/workspace/vms/primary/launch") {
      const body = request.postDataJSON();
      requests.push(body);
      assert.equal(request.method(), "POST");
      assert.equal(url.search, "?organizationId=org");
      assert.equal(body.generation, 3);
      assert.ok(["t3", "connect"].includes(body.service));
      if (release) await new Promise(resolve => { release.resolve = resolve; release.started(); });
      return route.fulfill({ status: fail ? 503 : 200, json: fail
        ? { error: { code: "fixture_unavailable", message: "Synthetic launch failure", details: [] } }
        : { url: `https://${body.service}.example.test/`, expiresAt: new Date(Date.now() + 60_000).toISOString() } });
    }
    if (url.origin === "https://t3.example.test" && url.pathname === "/")
      return route.fulfill({ contentType: "text/html", body: "<h1>Synthetic instance T3</h1>" });
    if (url.origin === "https://connect.example.test") {
      const file = connectFiles.get(url.pathname.slice(1));
      if (file) return route.fulfill(file);
      if (url.pathname === "/status" && request.method() === "GET")
        return route.fulfill({ json: { operation: "idle", linked, desired: linked } });
    }
    unexpected.push(request.url());
    return route.abort();
  });
  const page = await context.newPage();
  await page.goto(`${origin}/fixture.html`);
  const t3 = page.getByRole("region", { name: "Primary VM T3", exact: true });
  await t3.getByRole("button", { name: "Open instance T3", exact: true }).waitFor();
  assert.deepEqual(await t3.getByRole("button").allTextContents(), ["Open instance T3", "Connect T3 account"]);
  assert.equal(requests.length, 0);
  const launch = async label => {
    const opened = context.waitForEvent("page");
    await t3.getByRole("button", { name: label, exact: true }).click();
    return opened;
  };
  const instance = await launch("Open instance T3");
  await instance.getByRole("heading", { name: "Synthetic instance T3" }).waitFor();
  assert.equal(await instance.evaluate(() => window.opener), null);
  assert.deepEqual(requests, [{ generation: 3, service: "t3" }]);
  assert.equal(await page.getByRole("link", { name: "Open T3", exact: true }).count(), 0);
  await instance.close();
  const account = await launch("Connect T3 account");
  await account.getByRole("button", { name: "Link my T3 account", exact: true }).waitFor();
  assert.equal(await account.getByRole("status").textContent(), "Not linked");
  linked = true;
  await account.reload();
  await account.getByText("Manage connection", { exact: true }).click();
  await account.getByRole("button", { name: /Unlink/ }).waitFor();
  assert.equal(requests.at(-1).service, "connect");
  await account.close();
  fail = true;
  const failed = await launch("Open instance T3");
  await page.getByRole("alert").filter({ hasText: "Couldn't get a secure link" }).waitFor();
  assert.equal(failed.isClosed(), true);
  assert.equal(context.pages().length, 1);
  fail = false;
  const held = Promise.withResolvers();
  release = { started: held.resolve };
  const stale = await launch("Open instance T3");
  await held.promise;
  assert.equal(stale.url(), "about:blank");
  const closed = stale.waitForEvent("close");
  await page.evaluate(() => window.fixture.render(false, 2));
  await closed;
  release.resolve();
  release = null;
  await page.evaluate(() => { window.open = () => null; });
  const count = requests.length;
  await t3.getByRole("button", { name: "Open instance T3", exact: true }).click();
  await page.getByRole("alert").filter({ hasText: "browser blocked" }).waitFor();
  assert.equal(requests.length, count);
  assert.equal(context.pages().length, 1);
  await page.evaluate(() => window.fixture.render(true));
  await page.getByText("Idle timeout", { exact: true }).waitFor();
  assert.equal(await page.getByRole("button", { name: /T3/ }).count(), 0);
  assert.deepEqual(unexpected, []);
  console.log("Overview T3 browser acceptance passed: one-click launch/account entry, link status/unlink access, failure cleanup, stale context, popup blocking, no T3 in Settings; no live requests.");
} finally {
  release?.resolve?.();
  await browser?.close();
  await server.close();
}
