import { RefreshIcon } from "../lib/icons";

export function FatalScreen({
  message,
  title = "The harness could not start",
  onRetry = () => location.reload()
}: {
  message: string;
  title?: string;
  onRetry?: () => void;
}) {
  return (
    <div className="boot-screen">
      <div className="brand-mark large">
        <span></span>
        <span></span>
        <span></span>
      </div>
      <h2>{title}</h2>
      <p>{message}</p>
      <button className="button" onClick={onRetry}>
        <RefreshIcon /> Try again
      </button>
    </div>
  );
}
