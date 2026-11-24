import { LoaderCircle } from "lucide-react";
import type { DevelopmentWorkspaceVm } from "@/models/controlPlane";

const stages = [
  { id: "preparing_vm", label: "Preparing VM" },
  { id: "preparing_storage", label: "Preparing storage" },
  { id: "setting_up", label: "Setting up VM" },
  { id: "checking_readiness", label: "Checking readiness" },
  { id: "ready", label: "Ready" },
];

export function vmStartup(vm: DevelopmentWorkspaceVm) {
  const ready = vm.phase === "ready" && vm.desiredState === "running" && !vm.errorCode;
  const stopping = ["draining", "detaching", "deleting", "awaiting_git_save", "stopping"].includes(vm.phase);
  const unsettled = vm.desiredState === "running" && ["blocked", "failed"].includes(vm.phase);
  // The worker requeues a start whose step couldn't be confirmed, so it can still reach Ready without another start.
  const reconciling = unsettled && (vm.startupOperationStatus === "pending" || vm.startupOperationStatus === "running");
  const failed = unsettled && !reconciling;
  const starting = reconciling
    || (vm.desiredState === "running" && !vm.errorCode && !ready && !failed && !stopping && vm.phase !== "stopped");
  const applicable = stages.filter(stage => vm.kind === "primary" || stage.id !== "preparing_storage");
  const current = applicable.findIndex(stage => stage.id !== "ready" && stage.id === vm.startupStage);
  return { ready, failed, starting, reconciling, stages: applicable, current };
}

export function WorkspaceVmStartup({ vm, pending = false }: { vm: DevelopmentWorkspaceVm; pending?: boolean }) {
  const { ready, starting, reconciling, failed, stages, current } = vmStartup(vm);
  const stage = stages[current];
  const status = pending || starting
    ? (reconciling && !pending ? "Still starting: reconciling…" : starting && stage ? `${stage.label}…` : "Starting VM…")
    : failed ? "VM startup failed" : ready ? "Ready" : "";

  return <div className={pending || starting || failed ? "space-y-2 border-t px-5 py-3 text-sm" : undefined}>
    <p role="status" className={ready ? "sr-only" : failed ? "font-medium text-destructive" : "flex items-center gap-2 font-medium"}>
      {(pending || starting) && <LoaderCircle aria-hidden="true" className="size-4 motion-safe:animate-spin" />}
      {status}
    </p>
    {(starting || failed) && stage && <ol aria-label="VM startup stages" className="flex flex-wrap gap-2">
      {stages.map(({ id, label }, index) => <li key={id} aria-current={index === current ? "step" : undefined}
        className={`rounded-md border px-2 py-1 text-xs ${index < current ? "bg-success-surface text-success"
          : index === current ? "border-current font-medium" : "text-muted-foreground"}`}>
        <span className="sr-only">{index < current ? "Completed: " : index > current ? "Upcoming: " : failed ? "Last reported: " : "Current: "}</span>
        {label}
      </li>)}
    </ol>}
    {failed && <p className="text-muted-foreground">
      {stage ? `Last reported stage: ${stage.label}. ` : ""}
      Select Refresh status to check again, or contact your operator if startup stays blocked. Checking status doesn't start another VM.
    </p>}
    {reconciling && !pending && <p className="text-muted-foreground">
      {stage ? `Last reported stage: ${stage.label}. ` : ""}
      This start is still in progress: a step couldn't be confirmed yet, so it's being checked and retried automatically.
      Resources remain reserved and billed meanwhile. You don't need to start the VM again; contact your operator if it stays here.
      {vm.errorCode && <span className="mt-1 block break-words">Reported reason: {vm.errorCode}: {vm.errorMessage ?? "No additional details reported."}</span>}
    </p>}
    {(pending || starting) && <p className="text-muted-foreground">T3 and previews become available when the VM is ready.</p>}
  </div>;
}
