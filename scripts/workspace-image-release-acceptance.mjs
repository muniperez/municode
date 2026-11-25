#!/usr/bin/env node
import { randomUUID } from "node:crypto";
import { constants } from "node:fs";
import { mkdir, open, rename, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";

const collection = "/api/development-workspaces";
const cookieName = "__Host-workspaces-workspace";
const steps = ["create-ready", "t3-write", "stop", "start-t3-verify", "delete"];
const defaults = { "create-ready": 600_000, "t3-write": 120_000, stop: 600_000,
  "start-t3-verify": 720_000, delete: 900_000, recover: 30_000 };
const id = value => typeof value === "string" && /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$/.test(value);

class Failure extends Error {
  constructor(code, status) {
    super(code);
    this.status = status;
  }
}

function requireThat(condition, code) {
  if (!condition) throw new Failure(code);
}

function failureCode(error, signal) {
  if (signal?.aborted) return signal.reason?.name === "TimeoutError" ? "TIMEOUT" : "INTERRUPTED";
  return error instanceof Failure ? error.message : "REQUEST_OR_PROTOCOL_ERROR";
}

async function json(response) {
  if (!response.ok) {
    await response.body?.cancel();
    throw new Failure(`HTTP_${response.status}`, response.status);
  }
  return response.json();
}

async function poll(action, interval, signal) {
  for (;;) {
    signal.throwIfAborted();
    const result = await action();
    if (result) return result;
    await delay(interval, undefined, { signal });
  }
}

async function readKey(path) {
  const file = await open(path, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK);
  try {
    const info = await file.stat();
    requireThat(info.isFile() && info.uid === process.getuid() && !(info.mode & 0o077)
      && info.size > 0 && info.size <= 8192, "OWNER_ONLY_KEY_FILE_REQUIRED");
    const bytes = Buffer.alloc(8193);
    const { bytesRead } = await file.read(bytes);
    const key = bytes.subarray(0, bytesRead).toString("utf8").trim();
    requireThat(bytesRead <= 8192 && /^[\x21-\x7e]+$/.test(key), "INVALID_KEY_FILE");
    return key;
  } finally {
    await file.close();
  }
}

async function withSocket(url, cookie, signal, action) {
  signal.throwIfAborted();
  const socket = new WebSocket(url, { headers: { Cookie: cookie, Origin: url.origin.replace(/^ws/, "http") } });
  const pending = new Map();
  let nextId = 0;
  const rejectAll = error => {
    for (const request of pending.values()) request.reject(error);
    pending.clear();
  };
  const aborted = () => {
    rejectAll(signal.reason);
    socket.close();
  };
  signal.addEventListener("abort", aborted, { once: true });

  function send(message) {
    socket.send(JSON.stringify(message));
  }

  socket.addEventListener("message", event => {
    try {
      const message = JSON.parse(event.data);
      for (const value of Array.isArray(message) ? message : [message]) {
        if (value._tag === "Ping") { send({ _tag: "Pong" }); continue; }
        const requestId = String(value.requestId);
        if (value._tag === "Chunk") {
          send({ _tag: "Ack", requestId });
          const request = pending.get(requestId);
          if (request?.observe?.(value.values)) {
            pending.delete(requestId);
            request.resolve();
          }
        } else if (value._tag === "Exit") {
          const request = pending.get(requestId);
          if (!request) continue;
          pending.delete(requestId);
          if (!request.observe && value.exit?._tag === "Success") request.resolve(value.exit.value);
          else request.reject(new Failure("T3_RPC_REJECTED"));
        }
      }
    } catch {
      rejectAll(new Failure("INVALID_T3_MESSAGE"));
      socket.close();
    }
  });
  const closed = new Promise(done => socket.addEventListener("close", done, { once: true }));
  socket.addEventListener("close", () => rejectAll(new Failure("T3_SOCKET_CLOSED")));
  socket.addEventListener("error", () => rejectAll(new Failure("T3_SOCKET_FAILED")));
  const deadline = new Promise((_, reject) => {
    if (signal.aborted) reject(signal.reason);
    else signal.addEventListener("abort", () => reject(signal.reason), { once: true });
  });
  try {
    const opened = new Promise((done, reject) => {
      socket.addEventListener("open", done, { once: true });
      socket.addEventListener("close", () => reject(new Failure("T3_SOCKET_CLOSED")), { once: true });
      socket.addEventListener("error", () => reject(new Failure("T3_SOCKET_FAILED")), { once: true });
    });
    await Promise.race([opened, deadline]);
    await action((tag, payload, observe) => new Promise((resolve, reject) => {
      signal.throwIfAborted();
      requireThat(socket.readyState === WebSocket.OPEN, "T3_SOCKET_CLOSED");
      const requestId = String(++nextId);
      pending.set(requestId, { resolve, reject, observe });
      send({ _tag: "Request", id: requestId, tag, payload, headers: [] });
    }));
  } finally {
    signal.removeEventListener("abort", aborted);
    socket.close();
    await Promise.race([closed, deadline]);
  }
}

async function t3File(api, workspace, apiOrigin, gatewayDomain, contents, writing, signal) {
  const [vm] = workspace.vms;
  const { url } = await api("POST", `/${workspace.id}/vms/${vm.id}/launch`, signal,
    { generation: vm.generation, service: "t3" });
  const launch = new URL(url);
  const code = launch.searchParams.get("code");
  requireThat(launch.hostname.endsWith(`.${gatewayDomain}`) && !launch.username && !launch.password &&
    !launch.hash && launch.pathname === "/__workspaces/launch" &&
    [...launch.searchParams].length === 1 && /^[a-f0-9]{64}$/.test(code), "INVALID_T3_LAUNCH");
  requireThat(launch.protocol === new URL(apiOrigin).protocol, "INVALID_T3_LAUNCH");
  const redemption = await fetch(launch, { redirect: "manual", signal });
  const receipt = redemption.headers.getSetCookie().map(value => value.split(";", 1)[0])
    .find(value => new RegExp(`^${cookieName}=[a-f0-9]{64}$`).test(value));
  await redemption.body?.cancel();
  requireThat(redemption.status === 302 && redemption.headers.get("location") === "/" && receipt,
    "T3_LAUNCH_REDEMPTION_FAILED");
  const ticket = await json(await fetch(new URL("/api/auth/websocket-ticket", launch), {
    method: "POST", headers: { Cookie: receipt, "x-t3-orchestration-protocol": "2" }, redirect: "error", signal,
  }));
  requireThat(typeof ticket.ticket === "string" && ticket.ticket.length > 0, "INVALID_T3_TICKET");
  const socketUrl = new URL("/ws", launch);
  socketUrl.protocol = launch.protocol === "https:" ? "wss:" : "ws:";
  socketUrl.searchParams.set("wsTicket", ticket.ticket);
  socketUrl.searchParams.set("orchestrationProtocol", "2");
  await withSocket(socketUrl, receipt, signal, async rpc => {
    const terminal = { threadId: `image-release-${randomUUID()}`, terminalId: "image-release" };
    const file = `/workspace/home/.image-release-${contents}`;
    const nonce = randomUUID().replaceAll("-", "");
    const result = `image-release-${nonce}`;
    const resultLine = new RegExp(`\\n${result}(-failed)?\\r?\\n`);
    // Split the result marker so echoed input cannot satisfy the output check.
    const print = `printf '\\n%s%s\\n' '${result.slice(0, 20)}' '${result.slice(20)}'`;
    const expected = `printf '%s\\n' '${contents}'`;
    const fileCheck = writing ? `(umask 077; set -C; ${expected} > '${file}')` :
      `${expected} | cmp -s -- - '${file}'`;
    const command = `if [ "$(id -u)" = 2000 ] && mountpoint -q /workspace && ${fileCheck};` +
      ` then ${print}; else ${print}-failed; fi\n`;
    let startWrite;
    const snapshot = new Promise(done => { startWrite = done; });
    let output = "";
    let openedTerminal = false;
    try {
      const opened = await rpc("terminal.open", { ...terminal, cwd: "/workspace", cols: 120, rows: 32 });
      openedTerminal = true;
      requireThat(opened?.status === "running" && Number.isInteger(opened.pid), "T3_TERMINAL_NOT_RUNNING");
      const observed = rpc("terminal.observe", terminal, events => {
        for (const event of events) {
          if (event.type === "snapshot") startWrite();
          if (event.type !== "output") continue;
          if (typeof event.data !== "string") throw new Failure("INVALID_T3_MESSAGE");
          output = (output + event.data).slice(-4096);
          if (resultLine.test(output)) return true;
        }
        return false;
      });
      await Promise.all([observed, snapshot.then(() => rpc("terminal.write", { ...terminal, data: command }))]);
      const observedLine = resultLine.exec(output);
      requireThat(observedLine && !observedLine[1],
        writing ? "T3_FILE_WRITE_FAILED" : "T3_FILE_CONTENT_MISMATCH");
    } finally {
      if (openedTerminal && !signal.aborted) await rpc("terminal.close", { ...terminal, deleteHistory: true });
    }
  });
}

export function newReport(imageId, providerImageId) {
  return { schemaVersion: 1, type: "WorkspaceImageReleaseAcceptance", imageId, providerImageId,
    workspaceName: `image-release-${randomUUID()}`, workspaceId: null, status: "running",
    steps: Object.fromEntries(steps.map(step => [step, { status: "not-run" }])), failedStep: null,
    cleanup: { status: "not-needed" }, providerInventoryCheck: "required-before-promotion" };
}

export async function runAcceptance(config, report, { key, interrupt, timeouts = defaults, interval = 2000 }) {
  const { apiOrigin, organizationId, profile, planId, volumeSizeGb, gatewayDomain } = config;
  const query = `?organizationId=${encodeURIComponent(organizationId)}`;
  const path = suffix => `/${report.workspaceId}${suffix}`;
  const workspaceRequest = { organizationId, name: report.workspaceName, imageId: report.imageId,
    profile, planId, volumeSizeGb, idempotencyKey: report.workspaceName, startPrimaryVm: true };
  let createSent = false;
  let primaryId;
  const providerServerIds = new Set();
  let providerVolumeId;
  let initialGeneration;

  async function api(method, route, signal, body) {
    return json(await fetch(apiOrigin + collection + route, {
      method, signal, redirect: "error",
      headers: { Authorization: `Bearer ${key}`, ...(body !== undefined ? { "content-type": "application/json" } : {}) },
      body: body === undefined ? undefined : JSON.stringify(body),
    }));
  }

  function remember(workspace) {
    requireThat(workspace?.id === report.workspaceId && workspace.name === report.workspaceName &&
      workspace.organizationId === organizationId, "UNEXPECTED_WORKSPACE_IDENTITY");
    requireThat(id(workspace.primaryVmId) && Array.isArray(workspace.vms) && workspace.vms.length === 1 &&
      workspace.vms[0].id === workspace.primaryVmId && workspace.vms[0].kind === "primary" &&
      (!primaryId || primaryId === workspace.primaryVmId), "UNEXPECTED_WORKSPACE_VMS");
    primaryId = workspace.primaryVmId;
    if (id(workspace.providerServerId)) providerServerIds.add(workspace.providerServerId);
    if (id(workspace.providerVolumeId)) providerVolumeId ??= workspace.providerVolumeId;
    report.resources = { primaryVmId: primaryId, providerVolumeId: providerVolumeId ?? null,
      providerServerIds: [...providerServerIds] };
    return workspace;
  }

  async function get(signal) {
    return remember(await api("GET", path(query), signal));
  }

  function exactSelection(workspace) {
    const matches = resource => resource.imageId === report.imageId &&
      resource.profile === profile && resource.planId === planId;
    requireThat(matches(workspace) && matches(workspace.vms[0]), "CANDIDATE_SELECTION_MISMATCH");
    return workspace;
  }

  function waitFor(phase, signal) {
    return poll(async () => {
      const workspace = exactSelection(await get(signal));
      // Drain/checkpoint blockers can be advisory while the normal stop keeps progressing.
      requireThat(!["blocked", "failed", "delete_failed", "deleted"].includes(workspace.phase) &&
        !(workspace.errorCode && ["ready", "stopped"].includes(workspace.phase)), "WORKSPACE_LIFECYCLE_FAILED");
      return workspace.phase === phase ? workspace : null;
    }, interval, signal);
  }

  function checkReady(workspace) {
    const [vm] = workspace.vms;
    requireThat(workspace.desiredState === "running" && vm.phase === "ready" &&
      Number.isSafeInteger(workspace.generation) && workspace.generation > 0 &&
      vm.generation === workspace.generation && id(workspace.providerServerId) &&
      vm.providerServerId === workspace.providerServerId && id(providerVolumeId) &&
      workspace.providerVolumeId === providerVolumeId, "READY_RESOURCES_NOT_RECORDED");
  }

  async function remove(signal, stopBudget) {
    const stopSignal = AbortSignal.any([signal, AbortSignal.timeout(stopBudget)]);
    let stopFailure;
    try {
      await poll(async () => {
        const workspace = await get(stopSignal);
        if (workspace.phase === "stopped" || ["deleting", "deleted"].includes(workspace.volumeStatus)) return true;
        if (workspace.desiredState !== "stopped" || workspace.phase === "blocked") {
          try { await api("POST", path(`/stop${query}`), stopSignal); }
          catch (error) { if (error.status !== 409) throw error; }
        }
        return false;
      }, interval, stopSignal);
    } catch (error) {
      stopFailure = failureCode(error, stopSignal);
    }
    // The API refuses deletion unless all VMs have stopped and their tracked resources are gone.
    let deleted;
    try { deleted = remember(await api("DELETE", path(`${query}&confirmPermanentDeletion=true`), signal)); }
    catch (error) {
      if (error.status === 409 && stopFailure) throw new Failure(`CLEANUP_STOP_${stopFailure}`);
      throw error;
    }
    if (deleted.phase === "delete_failed") throw new Failure("WORKSPACE_DELETION_FAILED");
    await poll(async () => {
      const workspace = await get(signal);
      requireThat(workspace.phase !== "delete_failed", "WORKSPACE_DELETION_FAILED");
      return workspace.phase === "deleted" && workspace.volumeStatus === "deleted" &&
        workspace.providerVolumeId === null && workspace.providerServerId === null &&
        workspace.vms.every(vm => vm.phase === "deleted" && vm.providerServerId === null && vm.computeReserved === false);
    }, interval, signal);
    report.cleanup = { status: "confirmed", evidence: "control-plane-deletion-tombstone" };
  }

  const actions = [
    async signal => {
      createSent = true;
      const created = await api("POST", "", signal, workspaceRequest);
      requireThat(id(created?.id) && created.name === report.workspaceName &&
        created.organizationId === organizationId, "UNEXPECTED_CREATE_RESPONSE");
      report.workspaceId = created.id;
      exactSelection(remember(created));
      const ready = await waitFor("ready", signal);
      checkReady(ready);
      initialGeneration = ready.generation;
    },
    async signal => {
      const workspace = exactSelection(await get(signal));
      requireThat(workspace.phase === "ready" && workspace.generation === initialGeneration,
        "WORKSPACE_NOT_INITIAL_READY_GENERATION");
      checkReady(workspace);
      await t3File(api, workspace, apiOrigin, gatewayDomain, report.workspaceName, true, signal);
    },
    async signal => {
      await api("POST", path(`/stop${query}`), signal);
      const stopped = await waitFor("stopped", signal);
      requireThat(stopped.desiredState === "stopped" && stopped.generation === initialGeneration &&
        stopped.providerServerId === null && stopped.volumeStatus === "retained" &&
        stopped.providerVolumeId === providerVolumeId, "STOP_DID_NOT_RETAIN_VOLUME");
    },
    async signal => {
      await api("POST", path(`/start${query}`), signal);
      const ready = await waitFor("ready", signal);
      requireThat(ready.generation > initialGeneration && ready.providerVolumeId === providerVolumeId,
        "RESTART_DID_NOT_REUSE_VOLUME");
      checkReady(ready);
      await t3File(api, ready, apiOrigin, gatewayDomain, report.workspaceName, false, signal);
    },
    signal => remove(signal, Math.floor(timeouts.delete * 2 / 3)),
  ];

  for (const [index, step] of steps.entries()) {
    const timeout = AbortSignal.timeout(timeouts[step]);
    const signal = interrupt ? AbortSignal.any([interrupt, timeout]) : timeout;
    try {
      signal.throwIfAborted();
      await actions[index](signal);
      report.steps[step] = { status: "passed" };
    } catch (error) {
      report.failedStep = step;
      report.steps[step] = { status: "failed", code: failureCode(error, signal) };
      break;
    }
  }

  if (report.failedStep && createSent) {
    const signal = AbortSignal.timeout(timeouts.delete);
    try {
      if (!report.workspaceId) {
        const recovery = AbortSignal.any([signal, AbortSignal.timeout(timeouts.recover)]);
        await poll(async () => {
          const matches = (await api("GET", query, recovery)).filter(item =>
            item.name === report.workspaceName && item.organizationId === organizationId);
          requireThat(matches.length <= 1, "AMBIGUOUS_WORKSPACE_RECOVERY");
          if (!matches.length) return false;
          requireThat(id(matches[0].id), "INVALID_WORKSPACE_ID");
          report.workspaceId = matches[0].id;
          return true;
        }, interval, recovery);
      }
      await remove(signal, Math.floor(timeouts.delete * 2 / 3));
    } catch (error) {
      report.cleanup = { status: "unconfirmed", code: report.workspaceId ?
        failureCode(error, signal) : "CREATE_OUTCOME_UNKNOWN" };
    }
  }
  report.status = report.failedStep ? "failed" : "passed";
  return report;
}

async function main() {
  const names = ["api", "gateway-domain", "key-file", "organization", "image", "provider-image", "profile", "plan", "volume-gb"];
  const { values } = parseArgs({ options: { help: { type: "boolean" },
    ...Object.fromEntries(names.map(name => [name, { type: "string" }])) } });
  if (values.help) {
    console.log("Usage: node scripts/workspace-image-release-acceptance.mjs --api HTTPS_ORIGIN " +
      "--gateway-domain DOMAIN --key-file PATH --organization ID --image ID --provider-image ID " +
      "--profile ID --plan ID [--volume-gb 10]");
    return;
  }
  const api = URL.parse(values.api);
  const gatewayDomain = values["gateway-domain"];
  const volumeSizeGb = Number(values["volume-gb"] ?? "10");
  requireThat(api?.protocol === "https:" && api.href === `${api.origin}/` && !api.username && !api.password &&
    /^[a-z0-9-]+(?:\.[a-z0-9-]+)+$/.test(gatewayDomain) && values["key-file"] &&
    ["organization", "image", "provider-image", "profile", "plan"].every(name => id(values[name])) &&
    Number.isInteger(volumeSizeGb) && volumeSizeGb >= 10 && volumeSizeGb <= 10000, "INVALID_OPTIONS");
  const key = await readKey(values["key-file"]);
  const report = newReport(values.image, values["provider-image"]);
  const artifacts = fileURLToPath(new URL("../.local/artifacts/workspace-image-release/", import.meta.url));
  await mkdir(artifacts, { recursive: true, mode: 0o700 });
  const filename = resolve(artifacts, `${report.workspaceName}.json`);
  const save = path => writeFile(path, JSON.stringify(report, null, 2) + "\n", { flag: "wx", mode: 0o600 });
  await save(filename);
  console.log(filename);
  const interrupt = new AbortController();
  const cancel = () => interrupt.abort();
  process.on("SIGINT", cancel);
  process.on("SIGTERM", cancel);
  try {
    await runAcceptance({ apiOrigin: api.origin, organizationId: values.organization,
      profile: values.profile, planId: values.plan, gatewayDomain, volumeSizeGb }, report,
    { key, interrupt: interrupt.signal });
    await save(`${filename}.pending`);
    await rename(`${filename}.pending`, filename);
    if (report.failedStep) {
      console.error(`Acceptance failed at ${report.failedStep}; cleanup ${report.cleanup.status}.`);
      process.exitCode = 1;
    } else {
      console.log("Lifecycle passed; complete the documented provider inventory check before promotion.");
    }
  } finally {
    process.off("SIGINT", cancel);
    process.off("SIGTERM", cancel);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(error => {
    console.error(`Could not complete image release acceptance: ${failureCode(error)}.`);
    process.exitCode = 1;
  });
}
