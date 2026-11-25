import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { once } from "node:events";
import { createServer } from "node:http";
import test from "node:test";
import { newReport, runAcceptance } from "./workspace-image-release-acceptance.mjs";

const key = "synthetic-api-key";
const receipt = "a".repeat(64);
const launchCode = "b".repeat(64);
const cookie = `__Host-workspaces-workspace=${receipt}`;
const collection = "/api/development-workspaces";
const workspaceId = "workspace_synthetic";
const vmId = "workspacevm_synthetic";
const deadlines = { "create-ready": 2000, "t3-write": 2000, stop: 2000,
  "start-t3-verify": 2000, delete: 2000, recover: 500 };

function frame(text, opcode = 1) {
  const data = Buffer.from(text);
  const header = data.length < 126 ? Buffer.from([128 + opcode, data.length]) :
    Buffer.from([128 + opcode, 126, data.length >> 8, data.length & 255]);
  return Buffer.concat([header, data]);
}

function attachRpc(socket, receive) {
  let buffer = Buffer.alloc(0);
  socket.on("data", bytes => {
    buffer = Buffer.concat([buffer, bytes]);
    while (buffer.length >= 6) {
      const opcode = buffer[0] & 15;
      const extended = (buffer[1] & 127) === 126;
      const offset = extended ? 4 : 2;
      if (buffer.length < offset + 4) return;
      const size = extended ? buffer.readUInt16BE(2) : buffer[1] & 127;
      if (buffer.length < offset + 4 + size) return;
      const mask = buffer.subarray(offset, offset + 4);
      const data = buffer.subarray(offset + 4, offset + 4 + size).map((value, i) => value ^ mask[i % 4]);
      buffer = buffer.subarray(offset + 4 + size);
      if (opcode === 8) { socket.end(frame(data, 8)); return; }
      assert.equal(opcode, 1);
      receive(JSON.parse(data.toString()), value => socket.write(frame(JSON.stringify(value))));
    }
  });
}

async function fixture(t, failure, timeouts = deadlines) {
  const report = newReport("candidate-test", "snapshot-test");
  let phase;
  let generation = 1;
  let stored;
  let faultUsed = false;
  let opens = 0;
  let terminalClosed = false;
  let transitions = [];
  let deletingVolume = false;
  const operations = [];
  function fault(name) {
    if (failure !== name || faultUsed) return false;
    faultUsed = true;
    return true;
  }
  const server = createServer(async (req, res) => {
    const url = new URL(req.url, origin);
    const answer = (value, status = 200) =>
      res.writeHead(status, { "content-type": "application/json" }).end(JSON.stringify(value));
    const workspace = () => {
      const selection = { imageId: report.imageId, profile: "test-profile", planId: "test-plan" };
      const providerServerId = !deletingVolume && ["ready", "draining", "deleting"].includes(phase) ?
        `server-${generation}` : null;
      return { id: workspaceId, name: report.workspaceName, organizationId: "test-org",
        ...selection, primaryVmId: vmId, generation, phase, providerServerId,
        volumeStatus: phase === "deleted" ? "deleted" : deletingVolume ? "deleting" :
          phase === "stopped" ? "retained" : "attached",
        providerVolumeId: phase === "deleted" ? null : "volume-test",
        desiredState: phase === "ready" ? "running" : "stopped",
        errorCode: phase === "draining" ? "workspace_checkpoint_in_progress" : null,
        vms: [{ id: vmId, kind: "primary", ...selection, phase, providerServerId, generation,
          computeReserved: phase === "ready" }] };
    };
    if (url.pathname.startsWith(collection)) {
      assert.equal(req.headers.authorization, `Bearer ${key}`);
      if (url.pathname === collection && req.method === "POST") {
        const chunks = [];
        for await (const chunk of req) chunks.push(chunk);
        assert.deepEqual(JSON.parse(Buffer.concat(chunks)), { organizationId: "test-org", name: report.workspaceName,
          imageId: report.imageId, profile: "test-profile", planId: "test-plan", volumeSizeGb: 10,
          idempotencyKey: report.workspaceName, startPrimaryVm: true });
        operations.push("create");
        phase = "ready";
        if (fault("create-timeout")) return;
        if (fault("create-ready")) return answer({ error: `do not print ${key}` }, 503);
        if (fault("wrong-image")) return answer({ ...workspace(), imageId: "wrong-candidate" }, 201);
        return answer(workspace(), 201);
      }
      if (url.pathname === collection) return answer([workspace()]);
      if (url.pathname.endsWith("/launch")) {
        const chunks = [];
        for await (const chunk of req) chunks.push(chunk);
        assert.equal(url.pathname, `${collection}/${workspaceId}/vms/${vmId}/launch`);
        assert.deepEqual(JSON.parse(Buffer.concat(chunks)), { generation, service: "t3" });
        operations.push(`launch-${generation}`);
        terminalClosed = false;
        if (fault("t3-write")) return answer({}, 503);
        return answer({ url: `${origin}/__workspaces/launch?code=${launchCode}` });
      }
      assert.equal(url.searchParams.get("organizationId"), "test-org");
      if (url.pathname.endsWith("/start")) {
        operations.push("start");
        assert.equal(phase, "stopped");
        phase = "ready";
        generation++;
      } else if (url.pathname.endsWith("/stop")) {
        operations.push("stop");
        if (fault("stop")) return answer({}, 503);
        if (opens > 0) assert.equal(terminalClosed, true);
        phase = "draining";
        transitions = ["deleting", "stopped"];
      } else if (req.method === "DELETE") {
        operations.push("delete");
        assert.equal(phase === "stopped" || phase === "deleted", true);
        assert.equal(url.searchParams.get("confirmPermanentDeletion"), "true");
        if (failure === "delete-not-found") return answer({}, 404);
        if (fault("delete")) return answer({}, 503);
        phase = "deleting";
        deletingVolume = true;
        transitions = ["deleted"];
        return answer(workspace(), 202);
      }
      const state = workspace();
      if (req.method === "GET" && transitions.length) phase = transitions.shift();
      return answer(state);
    }
    assert.equal(req.headers.authorization, undefined);
    if (url.pathname === "/__workspaces/launch") {
      assert.equal(url.searchParams.get("code"), launchCode);
      return res.writeHead(302, { location: "/", "set-cookie": `${cookie}; Path=/; Secure; HttpOnly` }).end();
    }
    assert.equal(url.pathname, "/api/auth/websocket-ticket");
    assert.equal(req.headers.cookie, cookie);
    assert.equal(req.headers["x-t3-orchestration-protocol"], "2");
    assert.equal(req.method, "POST");
    answer({ ticket: `ticket-${generation}` });
  });
  const sockets = new Set();
  server.on("connection", socket => {
    sockets.add(socket);
    socket.once("close", () => sockets.delete(socket));
  });
  server.on("upgrade", (req, socket, head) => {
    assert.equal(req.url, `/ws?wsTicket=ticket-${generation}&orchestrationProtocol=2`);
    assert.equal(req.headers.cookie, cookie);
    assert.equal(req.headers.origin, origin);
    assert.equal(req.headers.authorization, undefined);
    assert.equal(head.length, 0);
    const accept = createHash("sha1").update(req.headers["sec-websocket-key"] +
      "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest("base64");
    socket.write("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
      `Sec-WebSocket-Accept: ${accept}\r\n\r\n`);
    let stream;
    attachRpc(socket, (request, send) => {
      if (request._tag === "Ack") return;
      assert.equal(request._tag, "Request");
      const exit = value => send({ _tag: "Exit", requestId: request.id, exit: { _tag: "Success", value } });
      const chunk = (...values) => send({ _tag: "Chunk", requestId: stream, values });
      switch (request.tag) {
        case "terminal.open":
          opens++;
          assert.equal(request.payload.cwd, "/workspace");
          exit({ status: "running", pid: 2000 });
          break;
        case "terminal.observe":
          stream = request.id;
          chunk({ type: "snapshot" });
          break;
        case "terminal.write": {
          const command = request.payload.data;
          const path = `/workspace/home/.image-release-${report.workspaceName}`;
          assert.ok(command.includes(path));
          assert.ok(command.includes('id -u)" = 2000'));
          assert.ok(command.includes("mountpoint -q /workspace"));
          const [, markerA, markerB] = command.match(/then printf '\\n%s%s\\n' '([^']+)' '([^']+)'/);
          const content = command.match(/printf '%s\\n' '([^']+)'/)[1] + "\n";
          exit(null);
          if (generation === 1) {
            assert.ok(command.includes("> "));
            stored = content;
          } else {
            assert.ok(command.includes("cmp -s -- - "));
            if (fault("start-t3-verify")) stored += "extra";
          }
          chunk({ type: "output", data: command }, { type: "output", data: `\r\n${markerA}` });
          chunk({ type: "output", data: `${markerB}${stored === content ? "" : "-failed"}\r\n` });
          break;
        }
        case "terminal.close":
          assert.equal(request.payload.deleteHistory, true);
          terminalClosed = true;
          exit(null);
          break;
        default: assert.fail(`Unexpected synthetic RPC ${request.tag}`);
      }
    });
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const origin = `http://127.0.0.1:${server.address().port}`;
  t.after(() => {
    for (const socket of sockets) socket.destroy();
    server.close();
  });
  await runAcceptance({ apiOrigin: origin, organizationId: "test-org", gatewayDomain: "0.0.1",
    profile: "test-profile", planId: "test-plan", volumeSizeGb: 10 }, report,
  { key, interval: 5, timeouts });
  assert.equal(phase, failure === "delete-not-found" ? "stopped" : "deleted");
  assert.equal(operations.filter(x => x === "create").length, 1);
  assert.equal(report.workspaceId, workspaceId);
  for (const secret of [key, receipt, launchCode, "ticket-1", origin]) {
    assert.ok(!JSON.stringify(report).includes(secret));
  }
  return { report, operations };
}

test("creates exact candidate, uses separate T3 generations, and confirms permanent deletion", async t => {
  const { report, operations } = await fixture(t);
  assert.equal(report.status, "passed");
  assert.equal(report.failedStep, null);
  assert.equal(report.cleanup.status, "confirmed");
  assert.deepEqual(Object.values(report.steps).map(value => value.status), Array(5).fill("passed"));
  assert.deepEqual(operations, ["create", "launch-1", "stop", "start", "launch-2", "stop", "delete"]);
  assert.deepEqual(report.resources.providerServerIds, ["server-1", "server-2"]);
  assert.equal(report.resources.providerVolumeId, "volume-test");
  assert.equal(report.providerInventoryCheck, "required-before-promotion");
});

for (const [failure, step, code] of [
  ["create-ready", "create-ready", "HTTP_503"],
  ["wrong-image", "create-ready", "CANDIDATE_SELECTION_MISMATCH"],
  ["t3-write", "t3-write", "HTTP_503"],
  ["stop", "stop", "HTTP_503"],
  ["start-t3-verify", "start-t3-verify", "T3_FILE_CONTENT_MISMATCH"],
  ["delete", "delete", "HTTP_503"],
  ["create-timeout", "create-ready", "TIMEOUT"],
]) {
  test(`${failure} preserves the failed step and still deletes`, async t => {
    const timeouts = failure === "create-timeout" ? { ...deadlines, "create-ready": 100 } : deadlines;
    const { report } = await fixture(t, failure, timeouts);
    assert.equal(report.status, "failed");
    assert.equal(report.failedStep, step);
    assert.deepEqual(report.steps[step], { status: "failed", code });
    assert.equal(report.cleanup.status, "confirmed");
  });
}

test("a missing delete response does not prove provider removal", async t => {
  const { report, operations } = await fixture(t, "delete-not-found");
  assert.equal(report.failedStep, "delete");
  assert.deepEqual(report.cleanup, { status: "unconfirmed", code: "HTTP_404" });
  assert.equal(operations.filter(x => x === "delete").length, 2);
});
