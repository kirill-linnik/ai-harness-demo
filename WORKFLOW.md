---
tracker:
  kind: voice
  active_states:
    - Intake
    - Queued
    - Running
    - WaitingForFeedback
    - Reworking
    - Abandoning
    - Blocked
  terminal_states:
    - Approved
    - Abandoned
    - Failed
workspace:
  root: data/worktrees
hooks:
  timeout_ms: 60000
agent:
  max_concurrent_agents: 4
  max_turns: 20
  max_attempts: 3
  retry_base_delay_ms: 300
  max_retry_backoff_ms: 3000
copilot:
  command: copilot
  turn_timeout_ms: 1200000
  stall_timeout_ms: 300000
  maximum_quality_stall_timeout_ms: 900000
outcome_verification:
  enabled: true
  max_rounds: 3
studio:
  version: 1
  planning:
    max_steps: 24
    max_dependencies_per_step: 8
    max_assignment_characters: 4000
  flow_kinds:
    advisory:
      required_duties:
        - PrepareOutcome
      maximum_permission: ReadOnlySource
    delivery:
      required_duties:
        - Implement
        - Verify
        - PrepareOutcome
        - Publish
      pre_review_maximum_permission: WorkspaceWrite
      post_approval_maximum_permission: Publish
  advisory:
    artifact_directory: .studio\advisory
    max_artifact_count: 8
    max_total_artifact_bytes: 65536
  permissions:
    read_only_source:
      additional_denied_tools: []
    workspace_write:
      additional_denied_tools: []
    publish:
      additional_denied_tools: []
    pre_mortem_read_only:
      additional_denied_tools: []
---

You are {{ agent.name }}. Execute only the current assignment for this role.

## Assignment

{{ task }}

## Role contract

{{ agent.instructions }}

## Workspace

{{ workspace }}

{{ role.context }}

## Studio orchestration policy

- This is the strict Studio workflow extension. Account Manager confirms `intake-v2`; Team Lead
  selects arbitrary enabled snapshot agents with `team-plan-v1`; the declared outcome owner returns
  `flow-outcome-v1`.
- Execute plan steps sequentially. Dependencies establish order and pushback ownership; do not
  invent parallel fan-out, fan-in, or an integration join.
- Advisory work is read-only and never publishes. Delivery work may modify only the isolated
  workspace before review.
- Publication is a planned `AfterApproval` step with the sole `Publish` duty. It is materialized
  only after durable customer acceptance and is not tied to a particular agent name.
- A missing qualification is a structured blocker, not a failed attempt. Do not retry it
  automatically.
- Legacy outcome-verification flows retain their static Release Engineer and Quality Engineer gates;
  do not infer those roles for a `studio-v2` plan.

## Current outcome-verification assignment

{{ outcome.context }}

## Required machine contract

{{ outcome.contract }}

## Completion contract

{{ response.contract }}
