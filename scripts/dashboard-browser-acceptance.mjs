#!/usr/bin/env node
// Run after the dashboard build with: cd modules/dashboard && npm exec --package=playwright@1.61.0 -- node ../../scripts/dashboard-browser-acceptance.mjs
// Set PLAYWRIGHT_CHANNEL=chrome to use an installed Google Chrome instead of a downloaded Chromium.
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { mkdir, readFile } from "node:fs/promises";
import { createServer } from "node:http";
import { createRequire } from "node:module";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../", import.meta.url));
const artifacts = join(root, ".local/artifacts/dashboard-browser");
const dist = join(root, ".local/artifacts/dashboard-dist");
process.env.PLAYWRIGHT_BROWSERS_PATH ??= join(root, ".local/artifacts/browser/chromium");
const packageDirectory = [...process.env.PATH.split(":").map(directory => dirname(directory)),
  join(root, ".local/artifacts/browser/node_modules")]
  .find(directory => existsSync(join(directory, "playwright/package.json")));
assert.ok(packageDirectory, "Run via npm exec --package=playwright or install Playwright under .local/artifacts/browser.");
const playwright = createRequire(join(packageDirectory, "fixture.cjs"))("playwright/test");
const { chromium } = playwright;
const expect = playwright.expect.configure({ timeout: 10_000 });

const organizationId = "org-fixture";
const option = {
  imageId: "img-t3", imageName: "Ubuntu 24.04 · T3 v0.1.9", profile: "Standard", planId: "plan-eu",
  planName: "EU standard", providerAccountName: "Fixture cloud", region: "eu-central", sku: "cx32",
  cpuCount: 4, memoryGiB: 8, computeHourlyRate: 0.0125, isDefault: true, agents: null,
};
const startupStages = ["preparing_vm", "preparing_storage", "setting_up", "checking_readiness"];
const startupPhases = ["provisioning", "attaching", "bootstrapping", "enrolling"];
const sha = "0123456789abcdef0123456789abcdef01234567";
const iso = minutesAgo => new Date(Date.now() - minutesAgo * 60_000).toISOString();

function vm(overrides) {
  return {
    kind: "primary", generation: 1, revision: 1, desiredState: "running", phase: "ready", imageId: option.imageId,
    profile: option.profile, planId: option.planId, providerAccountId: "provider-fixture", region: option.region,
    sku: option.sku, pushAuthorized: false, ignoredDataAcknowledged: false, gitStatus: "not_applicable",
    computeHourlyRate: option.computeHourlyRate, estimatedMonthlyCompute: 9.13, computeReserved: true,
    lastActivityAt: iso(5), startupStage: null, startupOperationStatus: null, errorCode: null, errorMessage: null,
    resources: { state: "unsupported", filesystems: [] }, ...overrides,
  };
}

function workspace(id, name, vms, overrides = {}) {
  return {
    id, organizationId, createdBy: "user-fixture", name, imageId: option.imageId, profile: option.profile,
    planId: option.planId, providerAccountId: "provider-fixture", region: option.region, sku: option.sku,
    volumeSizeGb: 20, desiredState: "running", phase: "ready", generation: 1, estimatedMonthlyCompute: 9.13,
    estimatedMonthlyStorage: 1.6, currency: "EUR", pricingUpdatedAt: "2026-11-01T00:00:00Z", revision: 1,
    createdAt: iso(600), updatedAt: iso(5), primaryVmId: vms.find(item => item.kind === "primary")?.id ?? null,
    volumeStatus: "attached", effectiveIdleTimeoutMinutes: 30, vms, ...overrides,
  };
}

function seed() {
  return [
    workspace("ws-alpha", "alpha-api", [
      vm({ id: "vm-alpha", generation: 4, revision: 7 }),
      vm({ id: "vm-task-1", kind: "ephemeral", generation: 2, repository: "https://github.com/example/alpha.git",
        baseRef: "main", baseCommit: sha, workBranch: "municode/task-1", pushAuthorized: true,
        ignoredDataAcknowledged: true, gitStatus: "pending" }),
      vm({ id: "vm-task-2", kind: "ephemeral", generation: 3, desiredState: "stopped", phase: "awaiting_git_save",
        repository: "https://github.com/example/alpha.git", baseRef: "main", baseCommit: sha, workBranch: "municode/task-2",
        pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "push_failed", errorCode: "git_push_rejected",
        errorMessage: "The remote rejected the work branch push." }),
    ]),
    workspace("ws-beta", "beta-docs", [
      vm({ id: "vm-beta", generation: 2, desiredState: "stopped", phase: "stopped", computeReserved: false,
        lastActivityAt: iso(3 * 24 * 60) }),
    ], { volumeSizeGb: 40, estimatedMonthlyStorage: 3.2, desiredState: "stopped", phase: "stopped" }),
    workspace("ws-gamma", "gamma-ml", [
      vm({ id: "vm-gamma", generation: 6, phase: "failed", startupStage: "setting_up", startupOperationStatus: "failed",
        errorCode: "bootstrap_failed", errorMessage: "Cloud-init reported a failed unit." }),
    ]),
  ];
}

const apiError = (status, code, message) => ({ status, json: { error: { code, message, details: [] } } });
const clone = value => structuredClone(value);

// A stateful, credential-free control plane that validates organization, target VM and generation on every call.
function createApi({ role = "owner", workspaces = seed(), selections } = {}) {
  const state = {
    workspaces, creates: new Map(), requests: [], mutations: [], failures: [], once: [], gitRepaired: false,
    launches: [], selections: selections ?? { available: true, options: [option], currency: "EUR", volumeMonthlyRatePerGb: 0.08 },
  };
  const find = id => state.workspaces.find(item => item.id === id);
  const bump = (item, target) => { item.revision += 1; if (target) target.revision += 1; };
  // A newly started VM advances one startup stage per status read, like a real asynchronous reconcile.
  const advance = item => {
    for (const target of item.vms) {
      if (target.desiredState !== "running" || !startupPhases.includes(target.phase)) continue;
      const index = startupStages.indexOf(target.startupStage) + 1;
      if (index >= startupStages.length) Object.assign(target, { phase: "ready", startupStage: "ready", lastReadyAt: iso(0) });
      else Object.assign(target, { phase: startupPhases[index], startupStage: startupStages[index] });
      if (item.primaryVmId === target.id) item.phase = target.phase;
      bump(item, target);
    }
  };
  const operate = (item, target, operation, body) => {
    if (operation === "start") {
      if (target.desiredState === "running" && target.phase !== "failed") return apiError(409, "invalid_state", "The VM is already starting.");
      Object.assign(target, { desiredState: "running", phase: "provisioning", startupStage: "preparing_vm", errorCode: null,
        errorMessage: null, computeReserved: true, startupOperationStatus: "running" });
    } else if (operation === "stop") {
      if (target.phase === "awaiting_git_save") {
        if (!state.gitRepaired) return apiError(409, "git_push_rejected", "The remote still rejects the work branch push.");
        Object.assign(target, { phase: "stopped", gitStatus: "saved", errorCode: null, errorMessage: null, computeReserved: false,
          verifiedRemoteRef: "refs/heads/municode/task-2", verifiedRemoteCommit: sha });
      } else Object.assign(target, { desiredState: "stopped", phase: "draining", errorCode: null, errorMessage: null });
    } else if (operation === "discard") {
      if (body.discardLocalData !== true || body.acknowledgeIgnoredDataLoss !== true) return apiError(400, "invalid_request", "Discard needs both confirmations.");
      Object.assign(target, { desiredState: "stopped", phase: "deleting", gitStatus: "discarded" });
    } else return apiError(400, "unsupported_operation", `Fixture does not support ${operation}.`);
    bump(item, target);
    return { status: 200, json: clone(item) };
  };

  async function handle(route) {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();
    const body = method === "GET" ? undefined : request.postDataJSON();
    const entry = { method, path: url.pathname, search: url.search, body };
    state.requests.push(entry);
    if (method !== "GET") state.mutations.push(entry);
    const pending = state.once.findIndex(rule => rule.method === method && rule.path.test(url.pathname));
    if (pending >= 0) {
      const [rule] = state.once.splice(pending, 1);
      const result = await rule.respond(entry, () => respond(method, url, body));
      if (result === "abort") return route.abort("failed");
      if (result) return route.fulfill(result);
    }
    const result = respond(method, url, body);
    if (!result) {
      state.failures.push(`Unexpected API request: ${method} ${url.pathname}`);
      return route.abort();
    }
    return route.fulfill(result);
  }

  function respond(method, url, body) {
    const path = url.pathname;
    if (method === "GET" && path === "/api/account/capabilities")
      return { json: { password: true, google: false, emailRecovery: false, bootstrapState: "ready" } };
    if (method === "GET" && path === "/api/account/session")
      return { json: { userId: "user-fixture", csrfToken: "synthetic-csrf", expiresAt: "2099-01-01T00:00:00Z",
        idleExpiresAt: "2099-01-01T00:00:00Z", renewAfter: "2099-01-01T00:00:00Z" } };
    if (method === "GET" && path === "/api/me")
      return { json: { id: "user-fixture", email: "fixture@example.test", displayName: "Fixture User",
        organizations: [{ id: organizationId, name: "Fixture workspace", slug: "fixture", role }] } };
    if (!path.startsWith("/api/development-workspaces")) return null;
    const org = url.searchParams.get("organizationId") ?? body?.organizationId;
    if (org !== organizationId) {
      state.failures.push(`Wrong organization for ${path}: ${org}`);
      return apiError(404, "not_found", "Workspace not found.");
    }
    if (method !== "GET" && role === "viewer") {
      state.failures.push(`Viewer sent a mutation: ${method} ${path}`);
      return apiError(403, "forbidden", "You do not have permission to perform this action.");
    }
    if (path === "/api/development-workspaces/selections" && method === "GET") return { json: clone(state.selections) };
    if (path === "/api/development-workspaces") {
      if (method === "GET") { state.workspaces.forEach(advance); return { json: clone(state.workspaces) }; }
      if (method === "POST") {
        const existing = state.creates.get(body.idempotencyKey);
        if (existing) return { status: 200, json: clone(find(existing)) };
        const id = `ws-${body.name}`;
        const primary = vm({ id: `vm-${body.name}`, phase: body.startPrimaryVm ? "provisioning" : "stopped",
          desiredState: body.startPrimaryVm ? "running" : "stopped", startupStage: body.startPrimaryVm ? "preparing_vm" : null,
          startupOperationStatus: body.startPrimaryVm ? "running" : null, lastActivityAt: null, computeReserved: !!body.startPrimaryVm });
        state.workspaces.push(workspace(id, body.name, [primary], { volumeSizeGb: body.volumeSizeGb, createdAt: iso(0),
          estimatedMonthlyStorage: body.volumeSizeGb * 0.08, phase: primary.phase, desiredState: primary.desiredState }));
        state.creates.set(body.idempotencyKey, id);
        return { status: 201, json: clone(find(id)) };
      }
      return null;
    }
    const match = path.match(/^\/api\/development-workspaces\/([^/]+)(?:\/(.+))?$/);
    const item = match && find(decodeURIComponent(match[1]));
    if (!item) return apiError(404, "not_found", "Workspace not found.");
    const operation = match[2];
    if (!operation && method === "GET") { advance(item); return { json: clone(item) }; }
    if (operation === "stop-all" && method === "POST") {
      for (const target of item.vms) {
        if (target.desiredState === "stopped" || target.phase === "awaiting_git_save") continue;
        Object.assign(target, { desiredState: "stopped", phase: "draining" });
        bump(item, target);
      }
      return { json: clone(item) };
    }
    if (operation === "vms/ephemeral" && method === "POST") {
      if (!body.pushAuthorized || !body.acknowledgeIgnoredDataLoss || !body.idempotencyKey)
        return apiError(400, "invalid_request", "Both acknowledgements and an idempotency key are required.");
      const id = `vm-task-${item.vms.length + 1}`;
      item.vms.push(vm({ id, kind: "ephemeral", phase: "provisioning", startupStage: "preparing_vm",
        startupOperationStatus: "running", repository: body.repository, baseRef: body.baseRef, baseCommit: body.baseCommit,
        workBranch: null, pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "pending", lastActivityAt: null }));
      bump(item);
      return { status: 201, json: clone(item) };
    }
    const vmMatch = operation?.match(/^vms\/([^/]+)\/([a-z-]+)$/);
    const target = vmMatch && item.vms.find(candidate => candidate.id === decodeURIComponent(vmMatch[1]));
    if (!target || method !== "POST") return null;
    if (body?.generation !== target.generation)
      return apiError(409, "stale_generation", `VM ${target.id} is now at generation ${target.generation}.`);
    if (vmMatch[2] === "launch") {
      state.launches.push({ vmId: target.id, ...body });
      if (!(target.phase === "ready" && target.desiredState === "running")) return apiError(409, "vm_not_ready", "The VM is not running.");
      const host = body.service === "t3" ? "t3.example.test" : `${body.service}.preview.example.test`;
      return { json: { url: `https://${host}/?ticket=synthetic-${state.launches.length}`, expiresAt: new Date(Date.now() + 120_000).toISOString() } };
    }
    return operate(item, target, vmMatch[2], body);
  }

  return { state, handle, find, mutations: () => state.mutations.map(({ method, path, body }) => `${method} ${path}${body?.generation === undefined ? "" : `#${body.generation}`}`) };
}

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
let origin;
const failures = [];

async function open(options = {}) {
  const api = createApi(options);
  const context = await browser.newContext({ viewport: options.viewport ?? { width: 1280, height: 900 } });
  await context.route("**/*", async route => {
    const url = new URL(route.request().url());
    if (url.origin === origin) return url.pathname.startsWith("/api/") ? api.handle(route) : route.continue();
    if (url.hostname === "t3.example.test" || url.hostname.endsWith(".preview.example.test"))
      return route.fulfill({ contentType: "text/html", body: `<h1>Synthetic ${url.hostname}</h1>` });
    failures.push(`External request blocked: ${url.origin}`);
    return route.abort();
  });
  context.on("page", page => page.on("pageerror", error => failures.push(error.message)));
  const page = await context.newPage();
  return { api, context, page };
}

const vmCard = (page, id) => page.getByRole("article", { name: `VM ${id}`, exact: true });
const alertWith = (scope, text) => scope.getByRole("alert").filter({ hasText: text });
const workspaceRow = (page, name) => page.getByRole("row").filter({ has: page.getByRole("link", { name, exact: true }) });

async function menuItems(page, trigger) {
  await trigger.click();
  const items = (await page.getByRole("menuitem").allTextContents()).map(text => text.trim());
  await page.keyboard.press("Escape");
  await expect(page.getByRole("menu")).toHaveCount(0);
  return items;
}

async function assertNoHorizontalOverflow(page) {
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true,
    "page scrolls horizontally");
}

// Header actions by role; a new environment action extends this list and adds its own scenario below.
const workspaceActions = { manager: ["Refresh status", "Stop all VMs", "Delete workspace"], viewer: ["Refresh status"] };

const scenarios = [
  async function listFilterDetailsAndDirectLinks() {
    const { api, context, page } = await open();
    await page.goto(`${origin}/workspaces`);
    const table = page.getByRole("table", { name: "Workspaces" });
    await expect(table.getByRole("link")).toHaveText(["alpha-api", "beta-docs", "gamma-ml"]);
    await expect(page.getByText("3 of 3 workspaces", { exact: true })).toBeVisible();
    // Status is text, not only color.
    await expect(workspaceRow(page, "alpha-api")).toContainText("Running");
    await expect(workspaceRow(page, "alpha-api")).toContainText("1 needs attention");
    await expect(workspaceRow(page, "beta-docs")).toContainText("Stopped");
    await expect(workspaceRow(page, "beta-docs")).toContainText("40 GB");
    await expect(workspaceRow(page, "gamma-ml")).toContainText("Needs attention");
    await page.getByRole("button", { name: "Filter & sort" }).click();
    await page.getByRole("combobox", { name: "Primary VM state" }).click();
    await page.getByRole("option", { name: "Stopped", exact: true }).click();
    await page.keyboard.press("Escape");
    await expect(table.getByRole("link")).toHaveText(["beta-docs"]);
    await expect(page.getByText("1 of 3 workspaces", { exact: true })).toBeVisible();
    await table.getByRole("link", { name: "beta-docs" }).click();
    await expect(page).toHaveURL(`${origin}/workspaces/ws-beta`);
    await expect(page.getByRole("heading", { level: 1, name: "beta-docs" })).toBeVisible();
    await expect(vmCard(page, "vm-beta").getByRole("button", { name: "Start VM", exact: true })).toBeEnabled();
    await page.getByRole("tab", { name: "Storage & costs" }).click();
    await expect(page.getByText("3.20 EUR", { exact: true })).toBeVisible();
    await expect(page.getByText(/Storage is charged even when all VMs are stopped/)).toBeVisible();
    await page.getByRole("navigation", { name: "Breadcrumb" }).getByRole("link", { name: "Workspaces" }).click();
    await expect(page).toHaveURL(`${origin}/workspaces`);
    await expect(table.getByRole("link")).toHaveText(["beta-docs"]);
    await page.goBack();
    await expect(page.getByRole("heading", { level: 1, name: "beta-docs" })).toBeVisible();
    await page.goBack();
    await page.getByRole("textbox", { name: "Search workspaces" }).fill("no-such-workspace");
    await expect(page.getByText("Adjust search or Filter & sort to find a workspace.")).toBeVisible();
    await page.getByRole("textbox", { name: "Search workspaces" }).fill("");
    // Direct links, reloads and manual refreshes only read state.
    for (const id of ["ws-alpha", "ws-beta", "ws-gamma"]) {
      await page.goto(`${origin}/workspaces/${id}`);
      await expect(page.getByRole("tablist", { name: "Workspace details" })).toBeVisible();
      await page.reload();
      await expect(page.getByRole("tablist", { name: "Workspace details" })).toBeVisible();
      const actions = page.getByRole("button", { name: "Workspace actions" });
      assert.deepEqual(await menuItems(page, actions), workspaceActions.manager);
      await actions.click();
      await page.getByRole("menuitem", { name: "Refresh status" }).click();
      await expect(page.getByRole("menu")).toHaveCount(0);
    }
    await page.goto(`${origin}/workspaces/ws-missing`);
    await expect(page.getByText("Workspace not found.")).toBeVisible();
    assert.deepEqual(api.mutations(), []);
    await page.screenshot({ path: join(artifacts, "list-detail-desktop.png"), fullPage: true });
    await context.close();
  },

  async function createAutoStartsOnceThenOpenStopAndRevisit() {
    const { api, context, page } = await open();
    await page.goto(`${origin}/workspaces`);
    const trigger = page.getByRole("button", { name: "Create workspace" });
    await trigger.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Create workspace" });
    await expect(dialog.getByRole("note", { name: "Automatic start" })).toContainText("Creating starts the primary VM automatically");
    await page.keyboard.press("Escape");
    await expect(trigger).toBeFocused();
    await trigger.click();
    await dialog.getByRole("textbox", { name: "Name" }).fill("delta-web");
    await expect(dialog.getByText("EU standard · eu-central")).toBeVisible();
    // The first response is lost after the server created the workspace; the retry must reuse the same key.
    api.state.once.push({ method: "POST", path: /^\/api\/development-workspaces$/, respond: (_, created) => {
      created();
      return "abort";
    } });
    await dialog.getByRole("button", { name: "Create workspace" }).click();
    await expect(alertWith(dialog, "Failed to fetch")).toBeVisible();
    await expect(dialog.getByRole("textbox", { name: "Name" })).toHaveValue("delta-web");
    await dialog.getByRole("button", { name: "Create workspace" }).click();
    await expect(page).toHaveURL(`${origin}/workspaces/ws-delta-web`);
    const creates = api.state.mutations.filter(entry => entry.path === "/api/development-workspaces");
    assert.equal(creates.length, 2);
    assert.equal(creates[0].body.idempotencyKey, creates[1].body.idempotencyKey);
    assert.deepEqual({ ...creates[1].body, idempotencyKey: undefined }, { organizationId, name: "delta-web",
      imageId: option.imageId, profile: option.profile, planId: option.planId, volumeSizeGb: 20, startPrimaryVm: true,
      idempotencyKey: undefined });
    assert.equal(api.state.workspaces.filter(item => item.name === "delta-web").length, 1);
    const card = vmCard(page, "vm-delta-web");
    await expect(card.getByRole("list", { name: "VM startup stages" })).toBeVisible();
    await page.screenshot({ path: join(artifacts, "create-starting.png") });
    await expect(card.getByRole("button", { name: "Open instance T3" })).toBeEnabled({ timeout: 30_000 });
    await expect(card.getByText("Running", { exact: true })).toBeVisible();
    // Startup was followed by reads only: no lifecycle calls, launches or duplicate starts.
    assert.deepEqual(api.mutations(), ["POST /api/development-workspaces", "POST /api/development-workspaces"]);
    const popupPromise = context.waitForEvent("page");
    await card.getByRole("button", { name: "Open instance T3" }).click();
    const popup = await popupPromise;
    await expect(popup.getByRole("heading", { name: "Synthetic t3.example.test" })).toBeVisible();
    assert.equal(await popup.evaluate(() => window.opener), null);
    await popup.close();
    await card.getByRole("button", { name: "Open preview" }).click();
    await card.getByRole("textbox", { name: "Preview name" }).fill("web");
    await card.getByRole("button", { name: "Get preview link" }).click();
    const link = card.getByRole("link", { name: "Open preview" });
    await expect(link).toHaveAttribute("href", /^https:\/\/web\.preview\.example\.test\//);
    await expect(link).toHaveAttribute("rel", "noopener noreferrer");
    assert.deepEqual(api.state.launches.map(({ vmId, generation, service }) => [vmId, generation, service]),
      [["vm-delta-web", 1, "t3"], ["vm-delta-web", 1, "web"]]);
    await card.getByRole("button", { name: "More actions for Primary VM" }).click();
    await page.getByRole("menuitem", { name: "Stop VM" }).click();
    await expect(card.getByRole("button", { name: "Start VM after it stops" })).toBeVisible();
    api.find("ws-delta-web").vms[0].phase = "stopped";
    api.find("ws-delta-web").vms[0].computeReserved = false;
    api.find("ws-delta-web").revision += 1;
    await page.getByRole("button", { name: "Workspace actions" }).click();
    await page.getByRole("menuitem", { name: "Refresh status" }).click();
    await expect(card.getByRole("button", { name: "Start VM", exact: true })).toBeEnabled();
    const stopped = api.mutations().length;
    await page.getByRole("navigation", { name: "Breadcrumb" }).getByRole("link", { name: "Workspaces" }).click();
    await expect(workspaceRow(page, "delta-web")).toContainText("Stopped");
    await page.getByRole("link", { name: "delta-web" }).click();
    await expect(vmCard(page, "vm-delta-web").getByRole("button", { name: "Start VM", exact: true })).toBeEnabled();
    await page.getByRole("tab", { name: "Storage & costs" }).click();
    await expect(page.getByText("1.60 EUR", { exact: true })).toBeVisible();
    await expect(page.getByRole("cell", { name: "Not allocated" })).toBeVisible();
    assert.equal(api.mutations().length, stopped);
    assert.deepEqual(api.mutations().slice(2), [
      "POST /api/development-workspaces/ws-delta-web/vms/vm-delta-web/launch#1",
      "POST /api/development-workspaces/ws-delta-web/vms/vm-delta-web/launch#1",
      "POST /api/development-workspaces/ws-delta-web/vms/vm-delta-web/stop#1",
    ]);
    await context.close();
  },

  async function ephemeralSiblingsGitSaveDiscardAndStopAll() {
    const { api, context, page } = await open();
    await page.goto(`${origin}/workspaces/ws-alpha`);
    await page.getByRole("button", { name: "Launch temporary VM" }).click();
    const dialog = page.getByRole("dialog", { name: "Launch temporary VM" });
    await dialog.getByRole("combobox", { name: "Image" }).selectOption(option.imageId);
    await dialog.getByRole("combobox", { name: "Size" }).selectOption(option.profile);
    await dialog.getByRole("combobox", { name: "Plan" }).selectOption(option.planId);
    await dialog.getByRole("textbox", { name: "Repository URL" }).fill("https://github.com/example/alpha.git");
    await dialog.getByRole("textbox", { name: "Base ref" }).fill("main");
    await dialog.getByRole("textbox", { name: "Resolved base commit SHA" }).fill(sha);
    const launch = dialog.getByRole("button", { name: "Launch temporary VM" });
    await expect(launch).toBeDisabled();
    await dialog.getByRole("checkbox", { name: /authorize task-scoped pushes/ }).check();
    await expect(launch).toBeDisabled();
    await dialog.getByRole("checkbox", { name: /deleted with this VM/ }).check();
    await launch.click();
    await expect(dialog).toHaveCount(0);
    const launched = api.state.mutations.at(-1);
    assert.equal(launched.path, "/api/development-workspaces/ws-alpha/vms/ephemeral");
    assert.equal(launched.body.pushAuthorized, true);
    assert.equal(launched.body.acknowledgeIgnoredDataLoss, true);
    assert.match(launched.body.idempotencyKey, /^[0-9a-f-]{36}$/);
    const vms = page.getByRole("region", { name: "Virtual machines" });
    await expect(vms.getByRole("button", { name: /^Work branch pending, https:\/\/github\.com\/example\/alpha\.git, / })).toBeVisible();

    // Git save blocked: retry is rejected, discard confirmation can be cancelled, then repair and retry succeeds.
    await vms.getByRole("button", { name: /^municode\/task-2, .*Git save blocked$/ }).click();
    const blocked = vmCard(page, "vm-task-2");
    const recovery = blocked.getByRole("region", { name: "Git save recovery for VM vm-task-2" });
    await expect(recovery).toContainText("The remote rejected the work branch push.");
    await recovery.getByRole("button", { name: "Retry stop" }).click();
    await expect(alertWith(blocked, "still rejects")).toBeVisible();
    await recovery.getByRole("button", { name: "Discard local data…" }).click();
    const discard = page.getByRole("dialog", { name: "Discard VM vm-task-2 generation 3" });
    await discard.getByRole("textbox").fill("discard vm-task-2 2");
    await discard.getByRole("checkbox").check();
    await expect(discard.getByRole("button", { name: "Confirm irreversible discard" })).toBeDisabled();
    await discard.getByRole("button", { name: "Cancel discard" }).click();
    await expect(discard).toHaveCount(0);
    api.state.gitRepaired = true;
    await recovery.getByRole("button", { name: "Retry stop" }).click();
    await expect(vms.getByRole("button", { name: /^municode\/task-2, .*Stopped$/ })).toBeVisible();

    // Discarding a sibling is a separate, typed confirmation bound to its ID and generation.
    await vms.getByRole("button", { name: /^municode\/task-1, / }).click();
    const sibling = vmCard(page, "vm-task-1");
    await expect(sibling.getByRole("button", { name: "Connect to T3" })).toBeEnabled();
    const more = sibling.getByRole("button", { name: "More actions for municode/task-1" });
    await more.click();
    await page.getByRole("menuitem", { name: "Discard local data…" }).click();
    const confirm = page.getByRole("dialog", { name: "Discard VM vm-task-1 generation 2" });
    await confirm.getByRole("textbox").fill("discard vm-task-1 2");
    await expect(confirm.getByRole("button", { name: "Confirm irreversible discard" })).toBeDisabled();
    await confirm.getByRole("checkbox").check();
    await confirm.getByRole("button", { name: "Confirm irreversible discard" }).click();
    await expect(confirm).toHaveCount(0);
    await expect(vms.getByRole("button", { name: /^municode\/task-1, .*deleting$/ })).toBeVisible();

    // Stop all: cancel sends nothing and returns focus; confirm sends one environment-level request.
    const actions = page.getByRole("button", { name: "Workspace actions" });
    await actions.focus();
    await page.keyboard.press("Enter");
    await page.getByRole("menuitem", { name: "Stop all VMs" }).focus();
    await page.keyboard.press("Enter");
    const stopAll = page.getByRole("dialog", { name: "Stop all VMs?" });
    await expect(stopAll.getByRole("listitem")).toHaveCount(4);
    await expect(stopAll).toContainText("vm-alpha · generation 4");
    const before = api.mutations().length;
    await stopAll.getByRole("button", { name: "Cancel stop all" }).click();
    await expect(stopAll).toHaveCount(0);
    await expect(actions).toBeFocused();
    assert.equal(api.mutations().length, before);
    await page.keyboard.press("Enter");
    await page.getByRole("menuitem", { name: "Stop all VMs" }).focus();
    await page.keyboard.press("Enter");
    await stopAll.getByRole("button", { name: "Confirm stop all" }).click();
    await expect(stopAll).toHaveCount(0);
    await expect(vms.getByRole("button", { name: /^Primary VM, draining$/ })).toBeVisible();
    assert.deepEqual(api.mutations(), [
      "POST /api/development-workspaces/ws-alpha/vms/ephemeral",
      "POST /api/development-workspaces/ws-alpha/vms/vm-task-2/stop#3",
      "POST /api/development-workspaces/ws-alpha/vms/vm-task-2/stop#3",
      "POST /api/development-workspaces/ws-alpha/vms/vm-task-1/discard#2",
      "POST /api/development-workspaces/ws-alpha/stop-all",
    ]);
    assert.deepEqual(api.state.mutations.at(-2).body, { generation: 2, discardLocalData: true, acknowledgeIgnoredDataLoss: true });
    assert.equal(api.state.mutations.some(entry => entry.path.includes("vm-alpha")), false);
    await page.screenshot({ path: join(artifacts, "ephemeral-stop-all.png"), fullPage: true });
    await context.close();
  },

  async function launchGenerationRejectionAndExpiry() {
    const { api, context, page } = await open();
    await page.goto(`${origin}/workspaces/ws-alpha`);
    const card = vmCard(page, "vm-alpha");
    await expect(card.getByRole("button", { name: "Open instance T3" })).toBeEnabled();
    // Another actor replaced the VM; the stale generation is rejected and nothing starts.
    Object.assign(api.find("ws-alpha").vms[0], { generation: 5 });
    let popupPromise = context.waitForEvent("page");
    await card.getByRole("button", { name: "Open instance T3" }).click();
    let popup = await popupPromise;
    await expect(alertWith(card, "Couldn't get a secure link. This didn't start the VM.")).toBeVisible();
    await expect.poll(() => popup.isClosed()).toBe(true);
    await page.getByRole("button", { name: "Workspace actions" }).click();
    await page.getByRole("menuitem", { name: "Refresh status" }).click();
    const fresh = vmCard(page, "vm-alpha");
    await expect(fresh.getByRole("alert")).toHaveCount(0);
    api.state.once.push({ method: "POST", path: /\/vms\/vm-alpha\/launch$/,
      respond: () => apiError(403, "forbidden", "Launch rejected by policy.") });
    popupPromise = context.waitForEvent("page");
    await fresh.getByRole("button", { name: "Open instance T3" }).click();
    popup = await popupPromise;
    await expect(alertWith(fresh, "Couldn't get a secure link.")).toBeVisible();
    await expect.poll(() => popup.isClosed()).toBe(true);
    api.state.once.push({ method: "POST", path: /\/vms\/vm-alpha\/launch$/,
      respond: () => ({ json: { url: "https://api.preview.example.test/", expiresAt: iso(1) } }) });
    await fresh.getByRole("button", { name: "Open preview" }).click();
    await fresh.getByRole("textbox", { name: "Preview name" }).fill("api");
    await fresh.getByRole("button", { name: "Get preview link" }).click();
    await expect(fresh.getByRole("status").filter({ hasText: "Link expired" })).toBeVisible();
    await fresh.getByRole("button", { name: "Get a new link" }).click();
    await expect(fresh.getByRole("link", { name: "Open preview" })).toHaveAttribute("href", /^https:\/\/api\.preview\.example\.test\//);
    assert.deepEqual(api.state.mutations.map(({ body }) => [body.generation, body.service]),
      [[4, "t3"], [5, "t3"], [5, "api"], [5, "api"]]);
    assert.equal(api.state.mutations.every(entry => entry.path.endsWith("/vms/vm-alpha/launch")), true);
    assert.equal(context.pages().length, 1);
    await context.close();
  },

  async function reconcilingFailedRetryAndStaleStart() {
    const { api, context, page } = await open();
    // The original start is still being retried by the worker: it reads as starting, never as failed.
    api.find("ws-gamma").vms[0].startupOperationStatus = "running";
    await page.goto(`${origin}/workspaces`);
    await expect(workspaceRow(page, "gamma-ml")).toContainText("Starting");
    await page.getByRole("link", { name: "gamma-ml" }).click();
    const card = vmCard(page, "vm-gamma");
    await expect(card.getByText("Still starting…", { exact: true })).toBeVisible();
    await expect(card.getByRole("status").filter({ hasText: "Still starting: reconciling…" })).toBeVisible();
    await expect(card.getByText("VM startup failed")).toHaveCount(0);
    await expect(card.getByRole("button", { name: "Retry start" })).toHaveCount(0);
    const reads = api.state.requests.filter(entry => entry.path === "/api/development-workspaces/ws-gamma").length;
    // Automatic status checks then observe the worker giving up; they never start the VM themselves.
    api.find("ws-gamma").vms[0].startupOperationStatus = "failed";
    await expect(card.getByText("VM startup failed").first()).toBeVisible({ timeout: 15_000 });
    assert.ok(api.state.requests.filter(entry => entry.path === "/api/development-workspaces/ws-gamma").length > reads);
    await expect(card.getByText("Still starting…", { exact: true })).toHaveCount(0);
    await expect(card.getByRole("list", { name: "VM startup stages" })).toContainText("Last reported: Setting up VM");
    await expect(alertWith(card, "bootstrap_failed: Cloud-init reported a failed unit.")).toBeVisible();
    await page.waitForTimeout(3_500);
    assert.deepEqual(api.mutations(), []);
    await card.getByRole("button", { name: "Retry start" }).click();
    await expect(card.getByRole("button", { name: "Retry start" })).toHaveCount(0);
    await expect(card.getByRole("list", { name: "VM startup stages" })).toContainText("Current:");
    await expect.poll(() => api.mutations()).toEqual([
      "POST /api/development-workspaces/ws-gamma/vms/vm-gamma/stop#6",
      "POST /api/development-workspaces/ws-gamma/vms/vm-gamma/start#6",
    ]);
    // A start against a generation replaced elsewhere is rejected; refreshing picks up the new generation.
    await page.goto(`${origin}/workspaces/ws-beta`);
    const beta = vmCard(page, "vm-beta");
    await expect(beta.getByRole("button", { name: "Start VM", exact: true })).toBeEnabled();
    api.find("ws-beta").vms[0].generation = 3;
    await beta.getByRole("button", { name: "Start VM", exact: true }).click();
    await expect(alertWith(beta, "now at generation 3")).toBeVisible();
    assert.equal(api.find("ws-beta").vms[0].desiredState, "stopped");
    await page.getByRole("button", { name: "Workspace actions" }).click();
    await page.getByRole("menuitem", { name: "Refresh status" }).click();
    await vmCard(page, "vm-beta").getByRole("button", { name: "Start VM", exact: true }).click();
    await expect(vmCard(page, "vm-beta").getByRole("list", { name: "VM startup stages" })).toBeVisible();
    assert.deepEqual(api.mutations().slice(2), [
      "POST /api/development-workspaces/ws-beta/vms/vm-beta/start#2",
      "POST /api/development-workspaces/ws-beta/vms/vm-beta/start#3",
    ]);
    await context.close();
  },

  async function readOnlyRole() {
    const { api, context, page } = await open({ role: "viewer" });
    await page.goto(`${origin}/workspaces`);
    await expect(page.getByRole("table", { name: "Workspaces" }).getByRole("link")).toHaveCount(3);
    await expect(page.getByRole("button", { name: "Create workspace" })).toHaveCount(0);
    await page.getByRole("link", { name: "alpha-api" }).click();
    await expect(page.getByText("You can view this workspace but can't change it.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Launch temporary VM" })).toHaveCount(0);
    const card = vmCard(page, "vm-alpha");
    await expect(card.getByRole("button", { name: "Open instance T3" })).toBeDisabled();
    await expect(card.getByRole("button", { name: "Open preview" })).toBeDisabled();
    await expect(card.getByText("You can view this VM but can't start, stop or open it.")).toBeVisible();
    await expect(card.getByRole("button", { name: "Connect T3 account" })).toHaveCount(0);
    assert.deepEqual(await menuItems(page, page.getByRole("button", { name: "Workspace actions" })), workspaceActions.viewer);
    await page.getByRole("button", { name: "Workspace actions" }).click();
    await page.getByRole("menuitem", { name: "Refresh status" }).click();
    await page.goto(`${origin}/workspaces/ws-beta`);
    await expect(vmCard(page, "vm-beta").getByRole("button", { name: "Start VM", exact: true })).toBeDisabled();
    assert.deepEqual(api.mutations(), []);
    await context.close();
    const empty = await open({ role: "viewer", workspaces: [] });
    await empty.page.goto(`${origin}/workspaces`);
    await expect(empty.page.getByText("No workspaces yet. Ask an organization administrator to create one.")).toBeVisible();
    await empty.context.close();
  },

  async function emptyUnavailableAndRefreshErrors() {
    const { api, context, page } = await open({ workspaces: [], selections: { available: false,
      unavailableReason: "Workspaces are paused for maintenance.", options: [] } });
    api.state.once.push({ method: "GET", path: /^\/api\/development-workspaces$/,
      respond: () => apiError(503, "unavailable", "Control plane unavailable.") });
    await page.goto(`${origin}/workspaces`);
    await expect(alertWith(page, "Control plane unavailable.")).toBeVisible();
    await expect(page.getByText("No workspaces yet.")).toHaveCount(0);
    await page.getByRole("button", { name: "Retry", exact: true }).click();
    await expect(page.getByText("No workspaces yet.", { exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Create workspace" }).click();
    const dialog = page.getByRole("dialog", { name: "Create workspace" });
    await expect(dialog.getByText("You can't create workspaces right now. Workspaces are paused for maintenance.")).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Create workspace" })).toBeDisabled();
    await page.keyboard.press("Escape");
    api.state.workspaces.push(...seed());
    await page.getByRole("button", { name: "Refresh workspaces" }).click();
    await expect(page.getByText("3 of 3 workspaces")).toBeVisible();
    api.state.once.push({ method: "GET", path: /^\/api\/development-workspaces$/,
      respond: () => apiError(503, "unavailable", "Control plane unavailable.") });
    await page.getByRole("button", { name: "Refresh workspaces" }).click();
    await expect(alertWith(page, "Control plane unavailable.")).toBeVisible();
    await expect(page.getByRole("table", { name: "Workspaces" }).getByRole("link")).toHaveCount(3);
    await page.getByRole("link", { name: "beta-docs" }).click();
    await expect(vmCard(page, "vm-beta").getByRole("button", { name: "Start VM", exact: true })).toBeVisible();
    api.state.once.push({ method: "GET", path: /^\/api\/development-workspaces\/ws-beta$/,
      respond: () => apiError(503, "unavailable", "Status service unavailable.") });
    await page.getByRole("button", { name: "Workspace actions" }).click();
    await page.getByRole("menuitem", { name: "Refresh status" }).click();
    await expect(alertWith(page, "Status refresh failed. Updates are delayed; showing the last reported status.")).toBeVisible();
    await expect(vmCard(page, "vm-beta").getByRole("button", { name: "Start VM", exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Retry status check" }).click();
    await expect(page.getByRole("alert")).toHaveCount(0);
    assert.deepEqual(api.mutations(), []);
    await context.close();
  },

  async function narrowViewports() {
    for (const width of [390, 320]) {
      const { api, context, page } = await open({ viewport: { width, height: 844 } });
      await page.goto(`${origin}/workspaces`);
      await expect(page.getByRole("link", { name: "alpha-api" })).toBeVisible();
      await assertNoHorizontalOverflow(page);
      const search = page.getByRole("textbox", { name: "Search workspaces" });
      const controls = [search, page.getByRole("button", { name: "Filter & sort" }),
        page.getByRole("button", { name: "Create workspace" }), page.getByRole("button", { name: "More", exact: true })];
      const boxes = await Promise.all(controls.map(control => control.boundingBox()));
      assert.ok(boxes[0].width >= 120, `search box is ${boxes[0].width}px wide`);
      for (const [index, box] of boxes.entries()) {
        assert.ok(box.x >= 0 && box.x + box.width <= width, `control ${index} leaves the viewport`);
        for (const other of boxes.slice(index + 1)) assert.ok(box.x + box.width <= other.x || other.x + other.width <= box.x
          || box.y + box.height <= other.y || other.y + other.height <= box.y, "toolbar controls overlap");
      }
      await search.fill("beta");
      await expect(page.getByText("1 of 3 workspaces")).toBeVisible();
      await search.fill("");
      await page.getByRole("button", { name: "Filter & sort" }).click();
      await page.getByRole("combobox", { name: "Temporary VMs" }).click();
      await page.getByRole("option", { name: "Needs attention", exact: true }).click();
      await page.keyboard.press("Escape");
      await expect(page.getByText("1 of 3 workspaces")).toBeVisible();
      await page.screenshot({ path: join(artifacts, `list-${width}.png`), fullPage: true });
      await page.getByRole("link", { name: "alpha-api" }).click();
      const card = vmCard(page, "vm-alpha");
      await expect(card.getByRole("button", { name: "Open instance T3" })).toBeVisible();
      await expect(page.getByRole("button", { name: /^Primary VM, Running$/ })).toBeVisible();
      await assertNoHorizontalOverflow(page);
      await page.emulateMedia({ forcedColors: "active" });
      await page.screenshot({ path: join(artifacts, `detail-${width}-forced-colors.png`), fullPage: true });
      await page.emulateMedia({ forcedColors: "none" });
      const actions = page.getByRole("button", { name: "Workspace actions" });
      await actions.click();
      await page.getByRole("menuitem", { name: "Stop all VMs" }).click();
      const stopAll = page.getByRole("dialog", { name: "Stop all VMs?" });
      const bounds = await stopAll.boundingBox();
      assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width);
      await page.keyboard.press("Escape");
      await expect(actions).toBeFocused();
      await page.getByRole("navigation", { name: "Breadcrumb" }).getByRole("link", { name: "Workspaces" }).click();
      await expect(page.getByText("1 of 3 workspaces")).toBeVisible();
      assert.deepEqual(api.mutations(), []);
      await context.close();
    }
  },
];

try {
  assert.ok(existsSync(join(dist, "index.html")), "Build the dashboard first: npm run build --prefix modules/dashboard");
  await mkdir(artifacts, { recursive: true });
  await new Promise(done => server.listen(0, "127.0.0.1", done));
  origin = `http://127.0.0.1:${server.address().port}`;
  browser = await chromium.launch({ headless: true, ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
  for (const scenario of scenarios) {
    try { await scenario(); }
    catch (error) { error.message = `${scenario.name}: ${error.message}`; throw error; }
    console.log(`ok - ${scenario.name}`);
  }
  assert.deepEqual(failures, []);
  console.log(`Dashboard browser acceptance passed: ${scenarios.length} scenarios; only fixture APIs contacted. Evidence: ${artifacts}`);
} finally {
  await browser?.close();
  await new Promise(done => server.close(done));
}
