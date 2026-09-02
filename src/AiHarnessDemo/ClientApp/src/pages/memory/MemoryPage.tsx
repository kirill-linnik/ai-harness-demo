import { useLearningsQuery } from "../../api/queries";
import { AppShell } from "../../components/AppShell";
import { BootScreen } from "../../components/BootScreen";
import { FatalScreen } from "../../components/FatalScreen";
import { StatCard } from "../../components/StatCard";
import { MemoryIcon } from "../../lib/icons";
import type { LearningDto } from "../../api/types";

function MemoryCard({ item }: { item: LearningDto }) {
  return (
    <article className="card memory-card">
      <div className="flow-card-top">
        <span className="status-pill approved">{item.category}</span>
        <span className="muted">
          {item.timesObserved} observed · {item.timesApplied} applied
        </span>
      </div>
      <h3>{item.lesson}</h3>
      <p>{item.trigger}</p>
      <div className="memory-refinement">{item.promptRefinement}</div>
    </article>
  );
}

export function MemoryPage() {
  const learningsQuery = useLearningsQuery();
  const learnings = learningsQuery.data;

  if (learningsQuery.isLoading) return <BootScreen />;
  if (learningsQuery.isError) {
    const error = learningsQuery.error;
    return (
      <FatalScreen
        title="Harness memory could not load"
        message={error instanceof Error ? error.message : String(error)}
        onRetry={() => void learningsQuery.refetch()}
      />
    );
  }
  if (!learnings) return null;

  const applied = learnings.reduce((sum, item) => sum + item.timesApplied, 0);
  const sourceFlows = new Set(learnings.map(item => item.sourceFlowId).filter(Boolean)).size;

  return (
    <AppShell
      active="memory"
      title="Harness memory"
      subtitle={`${learnings.length} learned prompt refinements · ${applied} applications`}
    >
      <div className="page-head">
        <div>
          <div className="eyebrow">Cross-flow learning</div>
          <h2>The harness remembers the correction</h2>
          <p>When a handoff is pushed back, the lesson becomes a prompt refinement for later agents and later flows.</p>
        </div>
      </div>
      <section className="stats-grid">
        <StatCard label="Learned refinements" value={learnings.length} detail="Persisted prompt changes" glow="rgba(155,135,245,.14)" />
        <StatCard label="Times applied" value={applied} detail="Prevented repeated omissions" glow="rgba(66,214,164,.13)" />
        <StatCard label="Source flows" value={sourceFlows} detail="Independent execution signals" glow="rgba(81,168,255,.13)" />
        <StatCard label="Memory scope" value={1} detail="Shared across the entire harness" glow="rgba(247,185,85,.13)" />
      </section>
      {learnings.length ? (
        <section className="memory-grid">
          {learnings.map(item => (
            <MemoryCard item={item} key={item.id} />
          ))}
        </section>
      ) : (
        <section className="card empty-state">
          <div>
            <div className="empty-icon">
              <MemoryIcon />
            </div>
            <h3>No corrections learned yet</h3>
            <p>The first observed pushback will create a durable refinement here. Later executions receive it automatically.</p>
          </div>
        </section>
      )}
    </AppShell>
  );
}
