# Agent quality contracts

The team must solve the confirmed customer problem, not merely satisfy a convenient technical
proxy. Role judgment and reusable working methods live in `.github\agents\*.agent.md`.
Studio response protocols, budgets, evidence namespaces, artifact locations, and execution
policy belong to harness-owned assignments and shared contracts. Editable roles do not need
to know Studio's JSON schemas or lifecycle enums.

## Outcome-oriented delivery

Intake preserves the problem without prescribing additions. Analysis establishes the actual
baseline and existing content/behavior. Planning asks whether a cheap faithful implementation
could pass while leaving the customer's complaint true. The requirements-first pre-mortem
challenges that loophole from supplied evidence, without inspecting implementation.

For a visual change, design supplies a selected composition, concrete tokens, annotated
desktop/mobile layouts, factual copy provenance, and a **Keep/Move/Merge/Replace/Remove**
content inventory. Refinement preserves the incumbent identity; a requested redesign may
replace its composition without losing facts or function. Implementation owns the complete
composition, not an isolated decorative addition.

When work changes visual design, the Team Lead schedules a separate Product Designer review
after implementation and before final independent QA. The same role receives a distinct
assignment to compare the unchanged rendered candidate and customer-facing review surface with
the chosen direction and baseline. Coverage includes the applicable viewports, themes, locales,
and states; findings address readability, hierarchy, spacing, coherence, and the requested
transformation. This is a planning responsibility, not an automatically inserted runtime step.
Work without design changes does not need this review. Designer approval never substitutes for
independent QA, and unavailable visual coverage is a limitation rather than approval.

Final QA separates three questions:

| Question | Necessary evidence |
| --- | --- |
| Does the product work? | Completed builds/checks, relevant assertions, real user journeys and failure states. |
| Does it match the design? | Rendered candidate versus the concrete handoff, including content disposition and shared consumers. |
| Does it solve the customer problem? | Matched baseline/candidate views and concrete observations of the requested visible improvement. |

Build success, CSS presence, artifact existence, a heading's coordinate, and zero overflow
cannot answer all three. Duplicate explanations, lost facts, displaced primary actions, or a
cosmetic add-on to an explicitly rejected experience must not be approved as a transformation.
First-viewport checks identify the actual essential explanation, actions, and summary elements;
the bottom of a content-rich section does not establish that those elements are hidden.
Supporting detail below the fold is not itself a failed requirement or a consent-dependent risk.
Unavailable suites are coverage limitations, not passing tests. A missing goal-level acceptance
obligation belongs in `PlanGaps`; a failed criterion remains a failure with owned remediation.
The harness planning assignment requires the Team Lead to keep assignments below the configured
ceiling with headroom and measure decoded string lengths before emission, including on correction.
Acceptance details belong in criteria,
not repeated copies of the entire shared QA contract.
The exact final JSON is checked with a counting tool, not estimated from a draft; without that
tool, assignments use a conservative budget no greater than 350 characters, reduced for smaller
configured limits. Explicit acceptance coverage includes
content uniqueness across composed empty/populated states and every reachable language.
The complete reply also needs headroom: target 6,500 characters, and 5,000 on correction,
including criteria and profiles. An interrupted response must be regenerated as one complete
handoff with literal, unformatted markers, never emitted as a JSON continuation.
Calling a defect pre-existing requires baseline evidence, not an upstream label. New copy must
cover every already-reachable locale. Newly broken existing journeys are failed obligations or
plan gaps, not automatically waivable risks.

Verification stays bounded: collect the relevant variant/viewport matrix together, fix observed
defects in one coherent implementation pass, and confirm the changed checks. Independent QA
never edits the candidate. Packaging stays faithful to the implementation and never rewrites
sealed bytes. Publication still requires durable customer approval.
Offline packaging derives factual content and binary encodings programmatically from the
product. QA independently checks complete rendered copy and decoded, visibly populated media;
image dimensions and successful load events cannot prove valid pixels. A new fidelity checker
must reject a negative corruption fixture as well as accept the real candidate.

### Browser tooling

Studio follows Symphony's execution boundary: the coding runtime owns MCP connectivity.
`copilot.mcp_config_file` in `WORKFLOW.md` names the repository-owned JSON configuration, using
Copilot CLI's native `mcpServers` shape. The host snapshots the selected server definitions and
tool names in each step's effective permission document, stages a native configuration for the
attempt, and passes it through `--additional-mcp-config`. Copilot CLI starts and operates the
servers; there is no browser implementation or role-name-based MCP router in the execution host.

Server configuration is provider-agnostic. Follow each server author's setup instructions and
declare its native tool names explicitly. Transport, launch arguments, environment, and URLs
pass through to Copilot CLI without a provider-specific engine schema. Configure only trusted
servers and tools appropriate to the execution boundary. Browser providers must use isolated
sessions rather than personal tabs or profiles.

Delivery worker assignments, including Design-only reviews, can use the configured MCP tools
without receiving shell or source-write permission. Intake, planning, pre-mortem, Advisory,
response-only corrections, and publication retain their existing restricted tool surfaces.
Additional workflow tool denials still apply. Do not add another Verify duty to obtain browser
access: Delivery reserves that duty for the sole final verification owner.

MCP is a tool channel, not proof of inspection or a network sandbox. Browser access must remain
within authorized project/review surfaces; provider options and CLI tool approvals do not
establish an OS-level security boundary. Inspect actual screenshots for visual judgments, not
just accessibility snapshots. Keep generated evidence outside candidate source using explicit
artifact filenames, and report missing browser installations or failed tool calls as coverage
limitations. Structured MCP errors are unsuccessful evidence; image payloads are hashed but
not copied into textual result summaries.

Candidate sealing respects Git's ignore rules instead of guessing output directories or report
names. Ignored untracked files are neither published nor included in the source fingerprint;
this includes local environment files, verification reports, and custom build-output folders.
Tracked files remain candidate content even under an ignore rule and cannot change unnoticed.
Unignored source changes, index integrity, canonical Git blobs, workspace boundaries, trusted
scaffold, separately sealed previews, and customer approval still receive their existing checks.
QA must test the deliverable's actual dependencies; an ignored local dependency is not silently
promoted into delivery. A legacy ignored-path sealing failure retries host finalization from
the accepted outcome rather than repeating substantive QA when no product repair was requested.

Test orchestration failures at their persisted boundary before another model-driven replay.
`IgnoredOutputSealFailure_RetriesOnlyHostFinalizationWithoutRepeatingAgents` uses a scripted
fixture to inject a seal failure after accepted QA, retries finalization, and asserts unchanged
agent invocation counts, one pending review, no waiver/publication, and idempotent re-entry.
Git fixtures separately cover arbitrary ignored outputs, secrets, and tracked files under
ignore rules. These checks run without Copilot calls; a full customer rerun is reserved for
changes to agent judgment or product behavior, not a host-only packaging correction.

A pending customer waiver can be declined through bound `RequestRefinement`. The harness
atomically resolves that waiver gate without consent, supersedes the reviewed readiness, and
queues a new iteration. This never records a waiver, accepts the product, or authorizes publication.

## Research basis

Primary-source review on **2026-10-02** informed these independently written role contracts.
No external prompt collection, installer, script, or plugin was copied or executed.
Popularity is context, not a quality benchmark; repository counts are point-in-time observations
from the GitHub API and apply to whole repositories, not individual skills.

| Source | Stars / forks | License inspected | Useful practice and limitation |
| --- | --- | --- | --- |
| [Anthropic skills](https://github.com/anthropics/skills/tree/8a1541c4a3ffa5a20a5a91de0dcf3f0bab1d1ef4/skills/frontend-design) | 179,381 / 21,207 | Frontend-design `LICENSE.txt`: Apache-2.0; repository API has no single detected license | Subject-specific visual decisions, concrete type/palette/layout, critique against the brief. Design prose alone does not establish fidelity or customer success. |
| [Impeccable](https://github.com/pbakaus/impeccable/tree/508d7e8955de3b3caf2d8676e85206723d41a887) | 74,065 / 4,466 | Apache-2.0 | Separate product truth from visual direction; content hierarchy, intentional composition, browser critique plus deterministic checks. Detector cleanliness is not evidence of excellent design. |
| [Superpowers](https://github.com/obra/superpowers/tree/8ca22dba9a94f28898bbce59f2537ff4d87c747d) | 294,271 / 26,309 | MIT | Root-cause debugging, focused regression checks, evidence before completion. Its branching/commit and agent orchestration conventions must not override Studio governance. |
| [Agency Agents](https://github.com/msitarzewski/agency-agents/tree/d3f71c4bb8922d3eea7576237a870dd59b3cdd52) | 155,748 / 25,128 | MIT | Role-specific handoffs and Reality Checker's comparison of claims with rendered evidence. Reject stack-specific mandatory commands, arbitrary grade/performance thresholds, and automatic rejection of every first pass. |

Comparable specialist contracts were reviewed, not just repository descriptions:

| Example | Useful principle | Deliberately not imported |
| --- | --- | --- |
| [UI Finish-Gate Reviewer](https://github.com/msitarzewski/agency-agents/blob/d3f71c4bb8922d3eea7576237a870dd59b3cdd52/design/design-ui-finish-gate-reviewer.md) | Judge implemented screens through product legibility, hierarchy, responsive priorities, and observable finish conditions. | A second final gate/schema or mandatory reference-catalogue dependency. Studio's sole Verify owner retains authority. |
| [Product Manager](https://github.com/msitarzewski/agency-agents/blob/d3f71c4bb8922d3eea7576237a870dd59b3cdd52/product/product-manager.md) | Lead with the problem and measurable outcome, make tradeoffs explicit, and distinguish impact from feature output. | Invented personas, mandatory press releases, repeated questioning, or blanket treatment of corrective feedback as scope creep. |
| [Software Architect](https://github.com/msitarzewski/agency-agents/blob/d3f71c4bb8922d3eea7576237a870dd59b3cdd52/engineering/engineering-software-architect.md) | Justify abstraction, document decisions and tradeoffs, and choose boundaries appropriate to the actual domain. | Mandatory DDD, new architecture paperwork for routine changes, or generic scalability claims. |
| [Data Engineer](https://github.com/msitarzewski/agency-agents/blob/d3f71c4bb8922d3eea7576237a870dd59b3cdd52/engineering/engineering-data-engineer.md) | Explicit producer/consumer contracts, idempotency, deliberate null handling, quality checks, and lineage. | Universal lakehouse architecture, mandatory soft deletes, or analytics infrastructure unrelated to the customer's storage. |
| [Technical Writer](https://github.com/msitarzewski/agency-agents/blob/d3f71c4bb8922d3eea7576237a870dd59b3cdd52/engineering/engineering-technical-writer.md) | Runnable examples, prerequisites, accurate versioned behavior, and one clear job per section. | Mandatory documentation-platform adoption, unobserved usage metrics, or generic README filler. |
| [Code Reviewer](https://github.com/msitarzewski/agency-agents/blob/d3f71c4bb8922d3eea7576237a870dd59b3cdd52/engineering/engineering-code-reviewer.md) | Specific correctness findings with impact and complete, prioritized remediation. | Its severity taxonomy or stylistic encouragement overriding host contracts; the risk of missing validation depends on reachability, not a fixed suggestion label. |

Substantive public feedback was considered alongside READMEs:

- [Superpowers: a scope cut defeated the user's stated goal](https://github.com/obra/superpowers/issues/2434).
  Keep minimalism subordinate to the confirmed outcome.
- [Superpowers: task reports entered commits and passed review](https://github.com/obra/superpowers/issues/2443).
  Account for changed paths and keep scratch material out of delivery.
- [Impeccable: Windows hook commands failed before running](https://github.com/pbakaus/impeccable/issues/859).
  Do not assume installed tooling or generic instructions actually executed.
- [Agency Agents: installer and metadata failure cases](https://github.com/msitarzewski/agency-agents/issues/917)
  and [Windows installation support](https://github.com/msitarzewski/agency-agents/issues/153).
  Adopt relevant methods, not unverified platform automation.

These issue reports are user/maintainer evidence of limitations, not independent controlled
reviews or proof of universal failure. Stars and testimonials do not establish comparative
quality. The decisive check is a real same-submission rerun and comparison of actual outcomes.

## Maintaining the contracts

Keep definitions project-agnostic. Customer names, repository paths, incident specifics, and
comparison artifacts belong in flow inputs or evidence, never role definitions. Do not add new
response schemas that compete with the host's strict plan, pre-mortem, QA, or outcome contracts.

Use **Reload catalog** after edits. Existing flows retain their immutable definitions; start a
new flow to exercise the revised team. Restart the runtime after changing compiled host contracts
before loading definitions that depend on those changes. Check the returned catalog revision and the new flow's
snapshot revision. Never approve or publish a comparison run merely to obtain a preview.

Focused catalog tests protect syntax, genericness, and critical quality obligations. Text
assertions detect accidental removal; they do not prove an agent follows its contract or that
future work will be good. Only observed execution can establish that.

The catalog regression check also rejects Studio protocol/storage tokens in role definitions.
Planning tests exercise schemas, conservative response budgets, and complete correction
assignments independently of role prose. Strict plan validation still rejects invalid output;
the harness never repairs an invalid result into a success or relaxes customer approval.
