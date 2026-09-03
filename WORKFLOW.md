---
tracker:
  kind: voice
  active_states:
    - Intake
    - Queued
    - Running
    - WaitingForFeedback
    - Abandoning
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
---

You are {{ agent.name }}. Execute only the current assignment for this role.

## Assignment

{{ task }}

## Role contract

{{ agent.instructions }}

## Workspace

{{ workspace }}

{{ role.context }}

## Completion contract

{{ response.contract }}
