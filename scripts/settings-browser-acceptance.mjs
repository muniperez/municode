#!/usr/bin/env node
// Run after the dashboard build with: cd modules/dashboard && npm exec --package=playwright -- node ../../scripts/settings-browser-acceptance.mjs
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { mkdir, readFile } from "node:fs/promises";
import { createServer } from "node:http";
import { createRequire } from "node:module";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../", import.meta.url));
const artifacts = join(root, ".local/artifacts/settings-browser");
process.env.PLAYWRIGHT_BROWSERS_PATH = join(artifacts, "chromium");
const packageDirectory = process.env.PATH.split(":").map(directory => dirname(directory))
  .find(directory => existsSync(join(directory, "playwright/package.json")));
assert.ok(packageDirectory, "Run via npm exec --package=playwright.");
const { chromium, expect } = createRequire(join(packageDirectory, "fixture.cjs"))("playwright/test");
const dist = join(root, ".local/artifacts/dashboard-dist");
const flowId = "11111111-2222-4333-8444-555555555555";
const organizations = [
  { id: "org-fixture", name: "Fixture workspace", slug: "fixture", role: "owner" },
  { id: "org-callback", name: "Callback workspace", slug: "callback", role: "owner" },
];
const settingsFlow = {
  id: flowId, kind: "settings", expiresAt: "2099-01-01T00:00:00Z",
  ui: {
    action: `/api/account/flows/settings?flow=${flowId}`, method: "POST", messages: [],
    nodes: [
      { group: "default", type: "hidden", name: "csrf_token", value: "synthetic-csrf", required: true, messages: [] },
      { group: "password", type: "password", name: "password", value: null, required: true, messages: [] },
      { group: "password", type: "submit", name: "method", value: "password", required: false, messages: [] },
    ],
  },
};
const server = createServer(async (request, response) => {
  try {
    const pathname = new URL(request.url, "http://127.0.0.1").pathname;
    if (pathname === "/runtime-config.json") {
      const origin = `http://${request.headers.host}`;
      response.setHeader("content-type", "application/json");
      response.end(JSON.stringify({ apiBaseUrl: origin, sourceUrl: "https://github.com/muniperez/municode",
        auth: { origin, capabilitiesUrl: `${origin}/api/account/capabilities` } }));
      return;
    }
    const asset = resolve(dist, `.${pathname}`);
    assert.ok(asset === dist || asset.startsWith(`${dist}/`));
    const file = pathname.startsWith("/assets/") ? asset : join(dist, "index.html");
    const contentType = file.endsWith(".js") ? "text/javascript" : file.endsWith(".css") ? "text/css" : "text/html";
    response.setHeader("content-type", contentType);
    response.end(await readFile(file));
  } catch (error) {
    response.writeHead(500).end(String(error));
  }
});
let browser;
try {
  await mkdir(artifacts, { recursive: true });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  assert.equal((await fetch(origin)).status, 200);
  browser = await chromium.launch({ headless: true });
  const failures = [];
  const mutations = [];
  async function fixture(role = "owner", viewport = { width: 1280, height: 900 }) {
    const context = await browser.newContext({ viewport });
    await context.route("**/*", async route => {
      const request = route.request();
      const url = new URL(request.url());
      if (url.origin !== origin) {
        failures.push(`External request blocked: ${url.origin}`);
        await route.abort();
        return;
      }
      if (!url.pathname.startsWith("/api/")) { await route.continue(); return; }
      let body;
      if (request.method() !== "GET") {
        mutations.push({ path: url.pathname, body: request.postDataJSON() });
        if (url.pathname === "/api/github-apps/complete" || url.pathname === "/api/github-apps/installation") {
          body = { organizationId: "org-callback", status: url.pathname.endsWith("/complete") ? "app_created" : "connected" };
        } else {
          failures.push(`Unexpected mutation: ${url.pathname}`);
          await route.abort();
          return;
        }
      } else if (url.pathname === "/api/account/capabilities") {
        body = { password: true, google: false, emailRecovery: false, bootstrapState: "ready" };
      } else if (url.pathname === "/api/account/session") {
        body = { userId: "user-fixture", csrfToken: "synthetic-csrf", expiresAt: "2099-01-01T00:00:00Z",
          idleExpiresAt: "2099-01-01T00:00:00Z", renewAfter: "2099-01-01T00:00:00Z" };
      } else if (url.pathname === "/api/me") {
        body = { id: "user-fixture", email: "fixture@example.test", displayName: "Fixture User",
          organizations: organizations.map(organization => ({ ...organization, role })) };
      } else if (url.pathname.startsWith("/api/account/flows/settings")) {
        body = { flow: settingsFlow, session: null };
      } else if (url.pathname === "/api/github-apps") {
        body = { organizationId: url.searchParams.get("organizationId"), status: "not_configured" };
      } else if (["/api/development-workspaces", "/api/providers", "/api/provider-types", "/api/plans", "/api/api-keys"].includes(url.pathname)) {
        body = [];
      } else {
        failures.push(`Unexpected API request: ${url.pathname}`);
        await route.abort();
        return;
      }
      await route.fulfill({ json: body });
    });
    const page = await context.newPage();
    page.on("pageerror", error => failures.push(error.message));
    return { context, page };
  }
  const { context, page } = await fixture();
  await page.goto(`${origin}/workspaces`);
  await expect(page.getByText("No workspaces yet.", { exact: true })).toBeVisible();
  const sidebar = page.locator("aside");
  const settings = sidebar.getByRole("button", { name: "Settings", exact: true });
  assert.equal(await settings.evaluate(button => button.nextElementSibling.textContent.includes("Fixture User")), true);
  for (const name of ["Plans", "GitHub", "API Keys", "Team"]) await expect(sidebar.getByRole("link", { name, exact: true })).toHaveCount(0);
  const search = page.getByRole("textbox", { name: "Search workspaces" });
  await search.fill("preserved search");
  await settings.focus();
  await page.keyboard.press("Enter");
  const dialog = page.getByRole("dialog", { name: "Settings", exact: true });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Account", exact: true })).toBeFocused();
  await page.keyboard.press("ArrowDown");
  await expect(dialog.getByRole("region", { name: "Plans", exact: true })).toBeVisible();
  for (const name of ["Team", "API Keys", "GitHub", "Account"]) {
    await dialog.getByRole("button", { name, exact: true }).click();
    await expect(dialog.getByRole("region", { name, exact: true })).toBeVisible();
    assert.equal(page.url(), `${origin}/workspaces`);
  }
  for (let index = 0; index < 15; index++) {
    await page.keyboard.press("Tab");
    assert.equal(await dialog.evaluate(element => element.contains(document.activeElement)), true);
  }
  await page.screenshot({ path: join(artifacts, "settings-desktop.png") });
  await page.keyboard.press("Escape");
  await expect(dialog).toHaveCount(0);
  await expect(settings).toBeFocused();
  await expect(search).toHaveValue("preserved search");
  const more = page.getByRole("button", { name: "More", exact: true });
  await more.focus();
  await page.keyboard.press("Enter");
  await page.getByRole("menuitem", { name: "Plans", exact: true }).click();
  await expect(dialog.getByRole("button", { name: "Plans", exact: true })).toBeFocused();
  await dialog.getByRole("button", { name: "Provider accounts", exact: true }).click();
  await expect(dialog.getByRole("heading", { name: "Provider accounts", exact: true })).toBeVisible();
  await dialog.getByRole("button", { name: "Back to plans", exact: true }).click();
  assert.equal(page.url(), `${origin}/workspaces`);
  await dialog.getByRole("button", { name: "API Keys", exact: true }).click();
  await expect(dialog.getByText("No API keys", { exact: true })).toBeVisible();
  const createKey = dialog.getByRole("button", { name: "Create key", exact: true }).first();
  await createKey.click();
  await expect(page.getByRole("dialog", { name: "Create API key", exact: true })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(createKey).toBeFocused();
  await expect(dialog).toBeVisible();
  await dialog.getByRole("button", { name: "Close", exact: true }).click();
  await expect(more).toBeFocused();
  assert.equal(mutations.length, 0);
  for (const callback of ["callback", "installed"]) {
    const query = callback === "callback" ? "code=synthetic-code" : "installation_id=123";
    await page.goto(`${origin}/settings/github/${callback}?connectionId=fixture-connection&state=synthetic-state&${query}`);
    await expect(page.getByRole("status")).toContainText("GitHub status:");
    assert.equal(new URL(page.url()).search, "");
    await page.getByRole("link", { name: "Return to GitHub settings" }).click();
    await expect(dialog.getByRole("button", { name: "GitHub", exact: true })).toBeFocused();
    await expect(dialog.getByRole("button", { name: "Set up GitHub", exact: true })).toBeVisible();
    await expect(page.getByRole("status")).toContainText("not_configured");
    await page.keyboard.press("Escape");
    await expect(page.getByRole("button", { name: /Municode Workspaces Callback workspace/ })).toBeVisible();
    assert.equal(new URL(page.url()).search, "");
  }
  assert.deepEqual(mutations.map(mutation => mutation.path), ["/api/github-apps/complete", "/api/github-apps/installation"]);
  assert.equal(await page.evaluate(() => Object.values(localStorage).concat(Object.values(sessionStorage))
    .some(value => /synthetic-(?:code|state|csrf)|fixture-connection/.test(value))), false);
  await context.close();
  for (const role of ["admin", "developer", "viewer"]) {
    const { context, page } = await fixture(role);
    await page.goto(`${origin}/workspaces?settings=plans`);
    const dialog = page.getByRole("dialog", { name: "Settings", exact: true });
    await expect(dialog.getByText("No plans", { exact: true })).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Add plan", exact: true })).toHaveCount(role === "admin" ? 2 : 0);
    await dialog.getByRole("button", { name: "API Keys", exact: true }).click();
    await expect(dialog.getByText("No API keys", { exact: true })).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Create key", exact: true })).toHaveCount(role === "admin" ? 2 : 0);
    await dialog.getByRole("button", { name: "GitHub", exact: true }).click();
    await expect(dialog.getByRole("status")).toContainText("not_configured");
    await expect(dialog.getByRole("button", { name: "Set up GitHub", exact: true })).toHaveCount(role === "admin" ? 1 : 0);
    await context.close();
  }
  for (const width of [390, 320]) {
    const { context, page } = await fixture("owner", { width, height: 844 });
    await page.goto(`${origin}/workspaces`);
    await page.getByRole("button", { name: "Open navigation" }).click();
    const navigation = page.getByRole("dialog", { name: "Navigation" });
    const opener = navigation.getByRole("button", { name: "Settings", exact: true });
    await opener.click();
    const dialog = page.getByRole("dialog", { name: "Settings", exact: true });
    await expect(dialog).toBeVisible();
    for (const name of ["Plans", "Team", "API Keys", "GitHub", "Account"]) {
      await dialog.getByRole("button", { name, exact: true }).click();
      await expect(dialog.getByRole("region", { name, exact: true })).toBeVisible();
    }
    const bounds = await dialog.boundingBox();
    assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width && bounds.y >= 0 && bounds.y + bounds.height <= 844);
    assert.equal(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth), true);
    await page.screenshot({ path: join(artifacts, `settings-${width}.png`) });
    await page.keyboard.press("Escape");
    await expect(dialog).toHaveCount(0);
    await expect(opener).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(navigation).toHaveCount(0);
    await context.close();
  }
  assert.deepEqual(failures, []);
  console.log("Settings browser acceptance passed: desktop/narrow entry points, keyboard/focus, preserved view, callback and permissions; only fixture APIs contacted.");
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
