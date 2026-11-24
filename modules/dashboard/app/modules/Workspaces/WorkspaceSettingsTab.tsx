import type { DevelopmentWorkspace } from "@/models/controlPlane";
import { WorkspaceCodingAgents } from "./WorkspaceCodingAgents";
import { WorkspaceDeleteEnvironment } from "./WorkspaceDeleteEnvironment";
import { WorkspaceIdleTimeoutSettings } from "./WorkspaceIdleTimeoutSettings";
import type { permanentDeletion } from "./workspaceCollection";
import type { WorkspaceRefresh } from "./workspaceVmLabels";

type Props = {
  item: DevelopmentWorkspace;
  organizationId: string;
  refresh: WorkspaceRefresh;
  // Reports refresh failures after an accepted mutation instead of throwing.
  refreshAfterMutation: WorkspaceRefresh;
  // False once permanent deletion is requested.
  canManage: boolean;
  // The member's role allows management; deletion stays retryable after a failed attempt.
  canManageRole: boolean;
  deletion: ReturnType<typeof permanentDeletion>;
};

export function WorkspaceSettingsTab({
  item, organizationId, refresh, refreshAfterMutation, canManage, canManageRole, deletion,
}: Props) {
  const vms = item.vms ?? [];
  return <div className="space-y-6">
    <WorkspaceIdleTimeoutSettings item={item} organizationId={organizationId} refresh={refreshAfterMutation} canManage={canManage} />
    <WorkspaceCodingAgents agents={item.agents} />
    {canManageRole && deletion !== "deleted" && <WorkspaceDeleteEnvironment item={item} organizationId={organizationId}
      vms={vms} failed={deletion === "failed"} deleting={deletion === "deleting"} refresh={refresh} />}
  </div>;
}
