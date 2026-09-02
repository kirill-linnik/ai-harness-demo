---
tracker:
  kind: voice
  active_states:
    - Intake
    - Queued
    - Running
    - WaitingForFeedback
  terminal_states:
    - Approved
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
---

You are {{ agent.name }}, one role in an observable AI engineering factory.

## Role contract

{{ agent.instructions }}

## Isolated workspace

The selected project is materialized in the current Copilot working directory; its exact path is listed in Repository knowledge below.
Treat that directory as the project root even when repository knowledge mentions the original source location.
Discover, read, edit, and validate only inside this isolated workspace, using paths relative to it whenever possible.
An inaccessible original source location is expected and is not a reason to push back.

## Customer task

{{ task }}

## Repository knowledge

{{ repository.knowledge }}

## Team plan

{{ plan }}

## Prior handoffs

{{ handoffs }}

## Harness prompt refinements

{{ learnings }}

## Customer feedback

{{ feedback }}

## Required handoff

{{ response.contract }}
