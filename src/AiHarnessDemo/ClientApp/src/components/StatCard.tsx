export function StatCard({
  label,
  value,
  detail,
  glow
}: {
  label: string;
  value: number;
  detail: string;
  glow: string;
}) {
  return (
    <article className="stat-card" style={{ ["--stat-glow" as string]: glow }}>
      <span>{label}</span>
      <strong>{Number(value || 0).toLocaleString()}</strong>
      <small>{detail}</small>
    </article>
  );
}
