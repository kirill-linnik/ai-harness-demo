import type { JSX } from "react";
import { Link } from "react-router-dom";

export function NavItem({
  page,
  label,
  icon,
  active,
  count,
  disabled = false,
  disabledReason
}: {
  page: string;
  label: string;
  icon: JSX.Element;
  active: string;
  count?: number | null;
  disabled?: boolean;
  disabledReason?: string;
}) {
  if (disabled) {
    return (
      <span className="nav-item disabled" aria-disabled="true" title={disabledReason}>
        <span className="nav-icon">{icon}</span>
        <span>{label}</span>
      </span>
    );
  }

  return (
    <Link className={`nav-item ${active === page ? "active" : ""}`} to={`/${page}`}>
      <span className="nav-icon">{icon}</span>
      <span>{label}</span>
      {Boolean(count) && <span className="nav-count">{count}</span>}
    </Link>
  );
}
