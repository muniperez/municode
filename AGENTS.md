# Municode repository instructions

This repository contains Municode, the standalone coding-workspace product: API, worker, gateway, dashboard, CLI, and release delivery.
Keep changes focused. Never push code unless explicitly asked.

- Start with the README and the operator and user documentation under `docs/`.
- Keep behavior and tests together. Run the affected .NET, dashboard, CLI, template, and delivery checks from the README.
- Keep hosted operations and operator credentials out of this repo. Use synthetic fixtures and example configuration.
- Keep release package paths in `packaging/targets.json`, installer paths, and CLI package names compatible with existing callers.

## GitHub issues and agent coordination

### Work ledger

- Track every actionable item as a GitHub issue in the owning repository before assigning implementation. Search for an existing issue first.
- For a feature, write a spec under `docs/` describing its behavior, scope, and acceptance criteria. Then open a feature coordinator issue with the spec path, and create native GitHub sub-issues for implementation.
- For a bug, open an issue with the observed and expected behavior, reproduction steps when known, and acceptance criteria. A small bug uses one issue. Split a larger fix into native GitHub sub-issues under the bug issue.
- Write each issue so an agent can pick it up without the original chat. Include the expected outcome, current behavior and relevant code or evidence, scope and boundaries, dependencies, and observable acceptance criteria with verification steps. Keep small issues short.
- For small or bounded work, include an effort and complexity note. For example, **Small / low complexity** means one scoped edit and a focused check. Call out factors that materially change the assignment, such as multiple components, migration, visual verification, or live operations.
- Keep assignments, blockers, decisions, status, PR links, and verification results on the issues. GitHub issues are the source of truth for coordinating work. Repository specs provide durable design context.

### Issue status comments

- **Started.** When any agent or subagent picks up an issue, comment on the issue before beginning implementation. Say that work has started and identify yourself with your agent name or role and a machine label or host alias. Use a stable session or run ID if a shareable host label is unavailable.
- **PR ready.** When your work is ready to merge, comment on the issue with the PR link, verification results, and any remaining merge prerequisites.
- **Coordinator takeover.** When you close a subagent's PR and take ownership of the work, comment on the subagent's issue. Link the closed PR, state that you are taking over, and say what happens next. Keep the issue open while implementation remains.

### Coordinator and implementation PRs

1. The coordinator owns the feature coordinator issue or bug issue. For a feature, commit the spec to the integration branch and open a draft feature PR against the repository's default branch or the base branch the user specifies. Link the spec from the coordinator issue once it is available on the branch. For a bug, use a draft bug PR and its branch for integration. Link the integrated PR and the issues in scope to each other.
2. The coordinator assigns implementation to a swarm of subagents. For a small bug, one implementation agent can take the whole issue. Give each agent its issue, file or component ownership, verification expectations, and the integration branch. Define scopes to maximize independent parallel work, and dispatch dependent work as prerequisites land. Select the lowest-cost available model suited to the issue’s effort and complexity. For example, use Luna for a bounded edit if available, Sol for implementation that requires more reasoning, and Terra when visual or UI inspection is central.
3. Each implementation agent starts its own branch or worktree from the integration branch and opens a focused PR with that branch as its base. Link the issue with a reference such as `Refs #123` and include the verification results. Record blockers and newly discovered follow-up work on GitHub.
4. The coordinator reviews each child PR, verifies its checks and acceptance criteria, and merges it into the integration branch. Record the merge on the linked issue. The integrated PR contains the combined work. Put closing references on the integrated PR for issues it completes.

### Independent validation

1. After implementation is integrated, the coordinator opens a separate
   `Work validation` issue and links it to the parent issue and integrated PR.
   Summarize what was implemented and list the acceptance criteria, scenarios
   to exercise, and expected results.
2. Before final merge, the coordinator assigns that issue to a validator
   subagent independent of implementation. Using the issue as the test plan,
   the validator runs the platform locally, runs the relevant automated code
   tests, exercises the affected flows in a browser, deploys the integrated PR
   revision to the configured staging environment, and checks it there. Record
   the tested commit, test results, browser evidence, and staging deployment
   result on the validation issue. Report missing staging access or setup as a
   blocker and keep validation open until staging checks can run.
3. For each finding, the validator opens a linked GitHub bug issue with
   reproduction steps and evidence and informs the coordinator. The coordinator
   assigns the fix to another implementation agent and verifies and merges its
   PR into the integration branch. The coordinator then sends the updated
   revision back to the validator to confirm the fix and resume or rerun the
   affected checks, including staging. Repeat until the validator records
   acceptance of the final integrated revision.
4. After the validator accepts the final revision, mark the integrated PR ready
   for review. Once required PR checks pass, the coordinator hands the PR to
   the user for final merge with links to the tracking and validation issues.
   The coordinator may merge it only if the user has explicitly authorized
   automatic merge after acceptance.

## Generated artifacts

- Put all intermediate files and generated execution output under the owning repository's `.local/artifacts/` directory, which must be git-ignored before writing outputs.
- This includes screenshots and browser recordings, benchmark results, SQL dumps and execution plans, logs, traces, coverage/build reports, and agent implementation/validation/handoff reports. Never put these outputs in `docs/`, source folders, or the Git index; never force-add ignored artifacts.
- Keep reusable scripts/tests and concise, maintained documentation in source control. Deliberately authored product assets and explicitly requested deliverables are not intermediate execution output.
- Before finishing, check `git status` and `git check-ignore` to ensure generated outputs cannot enter the change set. Move any misplaced outputs into `.local/artifacts/` and update producers and references.

## Disk cleanup

Agent worktrees share one limited disk volume. Each checkout's `node_modules`, `bin`/`obj`, and artifacts can grow to several gigabytes.

- Reuse one fixed directory per check, such as `.local/artifacts/<check>/`, and clear it before rerunning. Don't create a new artifact directory for every attempt.
- Delete bulky intermediate output, such as publish folders, package archives, extracted install trees, and copied build output, once its result is recorded. Keep only the small logs and reports cited on the issue or PR.
- Check free space with `df -h` before heavy builds, test runs, or package smokes. If less than about 5 GB is free, clean up your own artifacts first. If that isn't enough, record a blocker on the issue instead of deleting other agents' checkouts.
- When your PR is merged or your assignment ends, delete `.local/artifacts/`, `bin/`, `obj/`, and `node_modules/` in your worktree.
- After a child PR is merged or closed and its agent has stopped, the coordinator removes the child's worktree with `git worktree remove` and runs `git worktree prune`. Never remove a worktree that has uncommitted or unpushed work. Push it or record it on the issue first.
