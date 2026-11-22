#!/usr/bin/env node
// Loopback-only browser acceptance for the standalone gateway and its runtime contract.
import assert from "node:assert/strict";
import { execFileSync, spawn } from "node:child_process";
import { once } from "node:events";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { createServer as httpServer, request } from "node:http";
import { createServer as httpsServer } from "node:https";
import { connect } from "node:net";
import { createRequire } from "node:module";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../", import.meta.url));
const artifacts = join(root, ".local/artifacts/browser");
await mkdir(artifacts, { recursive: true });
process.env.PLAYWRIGHT_BROWSERS_PATH = join(artifacts, "chromium");
process.env.TMPDIR = artifacts;
const require = createRequire(join(artifacts, "fixture.cjs"));
const { chromium } = require("playwright");
const { WebSocketServer } = require("ws");
const domain = "workspace.example.test";
const t3Host = `vm-${"a".repeat(32)}-g1.${domain}`;
const previewHost = `vm-${"a".repeat(32)}-g1-s-web.${domain}`;
const sessionCookie = "__Host-workspaces-workspace";
const serviceCredential = "c".repeat(64);
const t3Bearer = "synthetic-t3-bearer";
const routes = new Map([
  [t3Host, { code: "a".repeat(64), session: "d".repeat(64), bearer: t3Bearer }],
  [previewHost, { code: "b".repeat(64), session: "e".repeat(64), bearer: null }],
]);
const servers = [];
const sockets = new Set();
let gateway;
let browser;

async function listen(server) {
  servers.push(server);
  server.on("connection", socket => {
    sockets.add(socket);
    socket.once("close", () => sockets.delete(socket));
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  return server.address().port;
}

function json(response, payload) {
  response.writeHead(200, { "content-type": "application/json" });
  response.end(JSON.stringify(payload));
}

try {
  const upstream = httpServer((req, res) => {
    const route = routes.get(req.headers.host);
    if (!route || req.headers.authorization !== (route.bearer ? `Bearer ${route.bearer}` : undefined) ||
        req.headers.cookie?.includes(sessionCookie)) {
      res.writeHead(401).end();
      return;
    }
    if (req.url === "/api/auth/websocket-ticket") {
      json(res, { ticket: "synthetic-ticket" });
      return;
    }
    res.setHeader("content-type", "text/html; charset=utf-8");
    res.end(`<!doctype html><html><body><h1>${route.bearer ? "Synthetic T3" : "Synthetic preview"}</h1></body></html>`);
  });
  const ws = new WebSocketServer({ noServer: true });
  upstream.on("upgrade", (req, socket, head) => {
    if (req.headers.host !== t3Host || req.headers.authorization !== `Bearer ${t3Bearer}` ||
        req.url !== "/ws?wsTicket=synthetic-ticket&orchestrationProtocol=2") {
      socket.destroy();
      return;
    }
    ws.handleUpgrade(req, socket, head, client => client.send("synthetic-websocket-ready"));
  });
  const upstreamPort = await listen(upstream);

  const controlPort = await listen(httpServer(async (req, res) => {
    if (req.headers["x-workspaces-gateway-credential"] !== serviceCredential) {
      res.writeHead(401).end();
      return;
    }
    const url = new URL(req.url, "http://localhost");
    if (url.pathname.endsWith("/certificate-authorization")) {
      res.writeHead(routes.has(url.searchParams.get("domain")) ? 200 : 404).end();
      return;
    }
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const { host, receipt } = JSON.parse(Buffer.concat(chunks).toString());
    const route = routes.get(host);
    if (route && !route.revoked && url.pathname.endsWith("/redeem") && receipt === route.code && !route.redeemed) {
      route.redeemed = true;
      json(res, { session: route.session, expiresAt: new Date(Date.now() + 60000) });
      return;
    }
    if (route && !route.revoked && url.pathname.endsWith("/resolve") && receipt === route.session) {
      json(res, { loopbackPort: upstreamPort, bearer: route.bearer });
      return;
    }
    res.writeHead(401).end();
  }));

  gateway = spawn("dotnet", [join(root, ".local/artifacts/bin/Workspaces.Gateway/debug/Workspaces.Gateway.dll")], {
    cwd: artifacts,
    env: {
      PATH: process.env.PATH,
      ASPNETCORE_URLS: "http://127.0.0.1:0",
      DevelopmentWorkspaceGateway__Enabled: "true",
      DevelopmentWorkspaceGateway__GatewayDomain: domain,
      DevelopmentWorkspaceGateway__ServiceCredential: serviceCredential,
      DevelopmentWorkspaceGateway__ControlPlaneUrl: `http://127.0.0.1:${controlPort}`,
      DevelopmentWorkspaceGateway__RelayHost: "127.0.0.1",
      DevelopmentWorkspaceGateway__MinimumRelayPort: String(upstreamPort),
      DevelopmentWorkspaceGateway__MaximumRelayPort: String(upstreamPort),
      DevelopmentWorkspaceGateway__TrustedProxyAddress: "127.0.0.1",
      DevelopmentWorkspaceGateway__ReauthorizationSeconds: "1",
    },
    stdio: ["ignore", "pipe", "inherit"],
  });
  const gatewayPort = await new Promise((resolve, reject) => {
    const deadline = setTimeout(() => reject(new Error("Gateway did not start")), 10000);
    const complete = result => {
      clearTimeout(deadline);
      resolve(result);
    };
    gateway.on("error", reject);
    gateway.on("exit", code => { clearTimeout(deadline); reject(new Error(`Gateway exited (${code})`)); });
    gateway.stdout.on("data", data => {
      const address = data.toString().match(/Now listening on: http:\/\/127\.0\.0\.1:(\d+)/);
      if (address) complete(Number(address[1]));
    });
  });

  assert.equal((await fetch(`http://127.0.0.1:${gatewayPort}/internal/certificate?domain=${t3Host}`)).status, 200);
  assert.equal((await fetch(`http://127.0.0.1:${gatewayPort}/internal/certificate?domain=unknown.${domain}`)).status, 404);

  const keyFile = join(artifacts, "fixture.key");
  const certFile = join(artifacts, "fixture.pem");
  execFileSync("openssl", ["req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
    "-subj", `/CN=*.${domain}`, "-addext", `subjectAltName=DNS:*.${domain}`,
    "-keyout", keyFile, "-out", certFile], { stdio: "ignore" });
  const forward = req => request({
    host: "127.0.0.1", port: gatewayPort, method: req.method, path: req.url,
    headers: { ...req.headers, "x-forwarded-proto": "https" },
  });
  const tls = httpsServer({ key: await readFile(keyFile), cert: await readFile(certFile) }, (req, res) => {
    const outgoing = forward(req);
    outgoing.on("response", incoming => {
      res.writeHead(incoming.statusCode, incoming.headers);
      incoming.pipe(res);
    });
    outgoing.on("error", () => res.destroy());
    req.pipe(outgoing);
  });
  tls.on("upgrade", (req, client, clientHead) => {
    const outgoing = forward(req);
    outgoing.on("upgrade", (res, remote, remoteHead) => {
      client.write(`HTTP/1.1 ${res.statusCode} ${res.statusMessage}\r\n${res.rawHeaders.flatMap((header, index, headers) =>
        index % 2 === 0 ? [`${header}: ${headers[index + 1]}\r\n`] : []).join("")}\r\n`);
      if (remoteHead.length) client.write(remoteHead);
      if (clientHead.length) remote.write(clientHead);
      remote.on("error", () => client.destroy()).on("close", () => client.destroy());
      client.on("error", () => remote.destroy()).on("close", () => remote.destroy());
      client.pipe(remote).pipe(client);
    });
    outgoing.on("response", () => client.destroy());
    outgoing.on("error", () => client.destroy());
    outgoing.end();
  });
  const tlsPort = await listen(tls);
  // Preserve default HTTPS ports and distinct browser origins while using an ephemeral listener.
  const proxy = httpServer((req, res) => res.writeHead(403).end());
  proxy.on("connect", (req, client, head) => {
    if (![t3Host, previewHost].some(host => req.url === `${host}:443`)) {
      client.destroy();
      return;
    }
    const tunnel = connect(tlsPort, "127.0.0.1", () => {
      client.write("HTTP/1.1 200 Connection Established\r\n\r\n");
      if (head.length) tunnel.write(head);
      client.pipe(tunnel).pipe(client);
    });
    tunnel.on("error", () => client.destroy()).on("close", () => client.destroy());
    client.on("error", () => tunnel.destroy()).on("close", () => tunnel.destroy());
  });
  const proxyPort = await listen(proxy);

  browser = await chromium.launch({ proxy: { server: `http://127.0.0.1:${proxyPort}` } });
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  const preview = await context.newPage();
  const launch = host => `https://${host}/__workspaces/launch?code=${routes.get(host).code}`;
  assert.equal((await page.goto(launch(t3Host))).status(), 200);
  assert.equal(page.url(), `https://${t3Host}/`);
  assert.equal(await page.locator("h1").textContent(), "Synthetic T3");
  const cookies = await context.cookies(`https://${t3Host}/`);
  assert.equal(cookies.length, 1);
  assert.deepEqual(cookies.map(({ name, secure, httpOnly, sameSite, domain }) =>
    ({ name, secure, httpOnly, sameSite, domain })),
    [{ name: sessionCookie, secure: true, httpOnly: true, sameSite: "Lax", domain: t3Host }]);
  assert.equal(await page.evaluate(() => document.cookie), "");
  assert.equal((await preview.goto(`https://${previewHost}/`)).status(), 401);
  assert.equal((await preview.goto(launch(previewHost))).status(), 200);
  assert.equal(await preview.locator("h1").textContent(), "Synthetic preview");
  assert.equal((await context.request.get(launch(t3Host))).status(), 401);

  const opening = page.evaluate(async () => {
    const { ticket } = await (await fetch("/api/auth/websocket-ticket", { method: "POST" })).json();
    return new Promise((resolve, reject) => {
      const upstream = new URL(`/ws?wsTicket=${ticket}&orchestrationProtocol=2`, location.href);
      upstream.protocol = "wss:";
      const socket = new WebSocket(upstream);
      window.runtimeClosed = new Promise(close => socket.addEventListener("close", close, { once: true }));
      socket.onmessage = message => resolve(message.data);
      socket.onerror = () => reject(new Error("Synthetic T3 websocket failed"));
    });
  });
  assert.equal(await opening, "synthetic-websocket-ready");
  await page.screenshot({ path: join(artifacts, "t3.png") });
  await preview.screenshot({ path: join(artifacts, "preview.png") });
  routes.get(t3Host).revoked = true;
  await page.waitForFunction(() => Promise.race([
    window.runtimeClosed.then(() => true), new Promise(resolve => setTimeout(() => resolve(false), 100)),
  ]), null, { timeout: 6000 });
  assert.equal((await page.reload()).status(), 401);
  assert.equal((await preview.reload()).status(), 200);
  await writeFile(join(artifacts, "report.json"), JSON.stringify({
    passed: ["certificate authorization", "single-use launch", "host-only secure cookie", "preview isolation",
      "bearer and ticket relay", "websocket revocation"],
  }, null, 2) + "\n");
  console.log("Synthetic browser acceptance passed.");
} finally {
  await browser?.close();
  if (gateway?.exitCode === null && gateway.pid) {
    const exited = once(gateway, "exit");
    gateway.kill();
    await exited;
  }
  for (const socket of sockets) socket.destroy();
  for (const server of servers) server.close();
}
