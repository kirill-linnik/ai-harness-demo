# Third-party notices

## OpenAI Symphony

This project contains a modified, independent .NET 10 implementation of concepts and normative
invariants from [OpenAI Symphony](https://github.com/openai/symphony), including:

- repository-owned `WORKFLOW.md` policy;
- one authoritative orchestration authority;
- bounded concurrent dispatch and restart reconciliation;
- explicit run-attempt phases and failure classes;
- exponential retry with jitter;
- isolated, reusable per-work-item workspaces;
- workspace path containment and sanitized keys;
- lifecycle hooks;
- pluggable agent runners;
- operator-visible state and runtime APIs.

The implementation is changed for customer voice intake, dynamic multi-role engineering handoffs,
GitHub Copilot CLI execution, SQLite history, handoff trust gates, and customer feedback loops.

OpenAI Symphony is licensed under the Apache License, Version 2.0. A copy is included at
`LICENSES\OpenAI-Symphony-Apache-2.0.txt`, and its attribution is retained in the root `NOTICE`.
