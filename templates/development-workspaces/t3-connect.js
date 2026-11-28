const statusHeading = document.getElementById("status");
const message = document.getElementById("message");
const steps = [...document.getElementById("steps").children];
const primary = document.getElementById("primary");
const openT3 = document.getElementById("open-t3");
const approval = document.getElementById("approval");
const approvalLink = document.getElementById("approval-link");
const code = document.getElementById("code");
const access = document.getElementById("access");
const accessDetails = document.getElementById("access-details");
const troubleshooting = document.getElementById("troubleshooting");
const help = document.getElementById("help");
const management = document.getElementById("management");
const unlink = document.getElementById("unlink");

const removeFromAccount = "To revoke remote access, sign in to your T3 account and remove this machine from T3 Connect.";
const actions = { link: "Link my T3 account", activate: "Activate and restart T3", status: "Check link status" };
const confirmations = {
  link: "Grant your T3 account independent relay access to this VM? This bypasses Workspaces session controls.",
  activate: "Restart T3 now? Active turns will be interrupted and T3 terminals will close. The VM stays running.",
  unlink: "Disable T3 Connect and clear the stored T3 account sign-in? Coding-provider sign-ins are not removed.",
};
const stages = {
  notLinked: {
    step: 0, title: "Not linked", action: "link",
    message: "Link your T3 account to open this workspace from another device. This does not start or create a VM.",
  },
  preparing: {
    step: 0, title: "Preparing account approval…", pending: true,
    message: "Keep this page open while T3 prepares your private approval code.",
  },
  waiting: {
    step: 0, title: "Waiting for account approval", pending: true,
    message: "Open the T3 approval page, check that the code matches, and approve with your own T3 account. This page updates automatically.",
  },
  expired: {
    step: 0, title: "Approval expired", action: "link", actionLabel: "Start approval again",
    message: "The old code is no longer available. Start again to request a new one.",
  },
  approvalFailed: {
    step: 0, title: "Approval did not finish", action: "link", actionLabel: "Start approval again",
    message: "T3 did not complete account approval. Start again if you still want to link.",
  },
  approved: {
    step: 1, title: "Account approved", action: "activate", manage: true,
    message: "Finish active turns and save your work before restarting T3. T3 terminals will close. The VM stays running, and your projects, threads, and sign-ins are kept.",
  },
  restarting: {
    step: 1, title: "Restarting T3…",
    message: "This page checks the link automatically. A brief loss of connection is expected.",
  },
  activationFailed: {
    step: 1, title: "T3 Connect activation failed", action: "activate", actionLabel: "Retry activation", manage: true,
    message: "Activation did not finish. Retry activation after checking T3 and relay connectivity.",
  },
  activationRejected: {
    step: 1, title: "Activation request not accepted", action: "status",
    message: "Check that the primary workspace is running and that you still have management access, then check link status.",
  },
  ready: {
    step: 2, title: "Ready to connect", action: "openT3", manage: true,
    message: "On another device, open the T3 web app or a supported T3 client and sign in to the same T3 account. Choose this workspace, then open an existing project or thread.",
  },
  offline: {
    step: 1, title: "Linked but not reachable", action: "status", manage: true,
    message: "The account link exists, but T3 could not reach this workspace. Check again once the workspace is running and T3 is ready.",
    help: `If the VM is stopped, start it from Workspaces when you need it. If you no longer have this VM: ${removeFromAccount}`,
  },
  revoked: {
    step: 0, title: "Link revoked", action: "link", manage: true,
    message: "T3 revoked this link. If you want to restore access, link your account again.",
  },
  unlinking: {
    title: "Unlinking T3 Connect…", message: "Checking whether T3 can remove the remote registration.",
  },
  unlinked: {
    step: 0, title: "T3 Connect unlinked", action: "link",
    message: "This workspace is no longer linked. Coding-provider sign-ins are unchanged.",
  },
  unlinkedRemoteUnknown: {
    step: 0, title: "Unlinked here; remote removal unconfirmed", action: "link",
    message: `Local T3 Connect access was disabled, but remote removal was not confirmed. ${removeFromAccount} Do this before linking again.`,
    help: "Separately paired routes and already-issued sessions may still need to be revoked in T3’s Sharing & access settings.",
  },
  unlinkUnknown: {
    title: "Unlink not confirmed", action: "status",
    message: `T3 Connect could not confirm that the link was removed. ${removeFromAccount}`,
    help: "If you can still access this workspace, check link status before trying to unlink again.",
  },
  unavailable: {
    title: "Link status unavailable", action: "status", actionLabel: "Check again",
    message: "Status checks could not confirm the result. This does not mean activation failed. Check that the primary workspace is running and that you still have management access, then check again.",
  },
  linkUnknown: {
    step: 0, title: "Could not confirm account linking", action: "status",
    message: "Check link status before starting another account approval.",
  },
};

let stage;
let busy = false;
const requestTimeout = 50_000;
const pollInterval = 3000;
// Cover the guest's 240s deadline plus a final status request (up to 43s).
const activationCheckWindow = 300_000;
const maxActivationChecks = 100;
let activationChecks;
let resumeStatus = false;
const activationFailures = {
  interrupted: "Activation was interrupted or timed out. Check that T3 is running, then retry activation.",
  approval_required: "T3 could not read a saved approval. Check link status and approve the account again if needed.",
  t3_inactive: "T3 is not running. Start the workspace from Workspaces, then retry activation.",
  restart_failed: "The managed T3 restart failed. Check the T3 service, then retry activation.",
  status_unavailable: "T3 restarted but did not answer the status check. Check the T3 service, then retry activation.",
  relay_unavailable: "The link was saved but the relay is not reachable. Check outbound network access, then retry activation.",
  relay_not_pinned: "T3 could not resolve the pinned relay client. The workspace image needs repair before retrying activation.",
  link_not_provisioned: "T3 did not provision the account link. Check relay connectivity and the secret-free activation diagnostics, then retry activation.",
  link_revoked: "T3 revoked the link. Unlink here and approve the account again.",
  queue_failed: "The activation service could not be queued. Check the managed activation service, then retry activation.",
  workspace_fenced: "Workspace management admissions are fenced. Wait until the workspace is Ready, then retry activation.",
};
const accountLinkLimit = "Your T3 account has reached its environment link limit. In T3 Connect, deregister an unused T3 environment, then retry activation here.";

function render(next, prompt) {
  const focused = document.activeElement;
  stage = next;
  if (statusHeading.textContent !== next.title) statusHeading.textContent = next.title;
  message.textContent = next.message;
  steps.forEach((element, index) => {
    if (index === next.step) element.setAttribute("aria-current", "step");
    else element.removeAttribute("aria-current");
    element.classList.toggle("complete", index < next.step);
  });
  access.hidden = accessDetails.hidden = next.action !== "link";
  approval.hidden = !prompt;
  code.textContent = prompt?.code ?? "";
  if (prompt) approvalLink.href = prompt.url;
  else approvalLink.removeAttribute("href");
  openT3.hidden = next.action !== "openT3";
  primary.hidden = !actions[next.action];
  primary.textContent = next.actionLabel ?? actions[next.action] ?? "";
  troubleshooting.hidden = !next.help;
  help.textContent = next.help ?? "";
  management.hidden = !next.pending && !next.manage;
  if (management.hidden) management.open = false;
  unlink.textContent = next.pending ? "Cancel approval" : "Unlink T3 Connect";
  if (focused?.closest("[hidden]")) {
    (prompt ? approvalLink : !primary.hidden ? primary : !openT3.hidden ? openT3 : statusHeading.parentElement).focus();
  }
}

function readStatus(value) {
  if (value.activation?.state === "pending" || value.activation?.state === "restarting") return [stages.restarting];
  if (value.activation?.state === "failed") {
    const events = value.activation.diagnostics?.events;
    const failure = Array.isArray(events) && events.includes("account_link_limit")
      ? accountLinkLimit : activationFailures[value.activation.reason] ?? stages.activationFailed.message;
    return [{
      ...stages.activationFailed,
      message: `${failure} Your saved approval is kept and the VM stays running.`,
    }];
  }
  if (value.statusAvailable === false) return [stages.offline];
  switch (value.operation) {
    case "awaiting_approval": {
      const { url, code } = value.approval ?? {};
      if (typeof url === "string" && URL.canParse(url) && new URL(url).protocol === "https:" &&
          typeof code === "string" && /^[A-Za-z0-9-]{4,32}$/.test(code)) {
        return [stages.waiting, { url, code }];
      }
      return [stages.preparing];
    }
    case "approval_expired": return [stages.expired];
    case "approval_failed": return [stages.approvalFailed];
    case "unlinked": return [value.remoteRemovalConfirmed === false ? stages.unlinkedRemoteUnknown : stages.unlinked];
    case "unlink_not_confirmed": return [stages.unlinkUnknown];
    case "idle":
    case "approval_saved_activate_required":
      if (value.revoked === true) return [stages.revoked];
      if (value.linked === true) return [value.reachable === true ? stages.ready : stages.offline];
      return [value.desired === true ? stages.approved : stages.notLinked];
    default: throw new Error("Unrecognized Connect status");
  }
}

function startActivationChecks() {
  activationChecks = { deadline: Date.now() + activationCheckWindow, remaining: maxActivationChecks };
}

function updateStatus(next, prompt, terminal = false) {
  if (next === stages.restarting && !activationChecks) startActivationChecks();
  if (activationChecks) {
    if (terminal || next === stages.ready || next === stages.revoked) {
      activationChecks = undefined;
    } else if (activationChecks.remaining <= 0 ||
        Date.now() >= activationChecks.deadline && !document.hidden && !resumeStatus) {
      activationChecks = undefined;
      render(stages.unavailable);
      return;
    } else {
      render(stages.restarting);
      return;
    }
  }
  render(next, prompt);
}

function shouldPoll() {
  return stage?.pending || activationChecks;
}

function pollStatus() {
  // Even after a hidden-tab pause passes the deadline, read once before giving up.
  if (!document.hidden && shouldPoll()) void request("status");
}

async function request(action) {
  if (busy || (confirmations[action] && !confirm(confirmations[action]))) return;
  busy = true;
  primary.setAttribute("aria-disabled", "true");
  unlink.setAttribute("aria-disabled", "true");
  if (action === "activate") {
    startActivationChecks();
    render(stages.restarting);
  } else if (action === "unlink") {
    render(stages.unlinking);
  }
  if (action === "status" && activationChecks) activationChecks.remaining--;
  try {
    const reading = action === "status";
    const response = await fetch(`/${action}`, {
      method: reading ? "GET" : "POST",
      headers: reading ? {} : { "X-Municode-Connect": "1" },
      cache: "no-store",
      credentials: "same-origin",
      redirect: "error",
      signal: AbortSignal.timeout(requestTimeout),
    });
    if (action === "activate" && (response.status === 403 || response.status === 409)) {
      activationChecks = undefined;
      render(stages.activationRejected);
    } else {
      if (!response.ok) throw new Error("Connect request was not accepted");
      const value = await response.json();
      const [next, prompt] = readStatus(value);
      updateStatus(next, prompt, value.activation?.state === "failed" || value.activation?.state === "linked");
    }
  } catch {
    updateStatus(action === "unlink" ? stages.unlinkUnknown
      : action === "link" ? stages.linkUnknown : stages.unavailable);
  } finally {
    busy = false;
    primary.removeAttribute("aria-disabled");
    unlink.removeAttribute("aria-disabled");
    if (resumeStatus) {
      resumeStatus = false;
      pollStatus();
    }
  }
}

primary.addEventListener("click", () => { if (!primary.hidden) void request(stage.action); });
unlink.addEventListener("click", () => { if (management.open && !management.hidden) void request("unlink"); });
void request("status");
setInterval(pollStatus, pollInterval);
document.addEventListener("visibilitychange", () => {
  if (document.hidden || !shouldPoll()) return;
  if (busy) resumeStatus = true;
  else pollStatus();
});
