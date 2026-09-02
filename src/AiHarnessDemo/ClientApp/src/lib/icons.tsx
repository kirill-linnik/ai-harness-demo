import type { JSX, SVGProps } from "react";

/** Direct ports of the inline SVG icon strings from the original wwwroot/app.js `icons` map. */

type IconProps = SVGProps<SVGSVGElement>;

function icon(children: JSX.Element, props: IconProps = {}): JSX.Element {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" {...props}>
      {children}
    </svg>
  );
}

export const FactoryIcon = (props: IconProps = {}) =>
  icon(
    <>
      <path d="M4 20V9l5 3V8l5 3V4h3v7l3 2v7H4Z" />
      <path d="M8 20v-3h3v3m3 0v-3h3v3" />
    </>,
    props
  );

export const SettingsIcon = (props: IconProps = {}) =>
  icon(
    <>
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6v.2h-4V21a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14H2.8v-4H3a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1A1.7 1.7 0 0 0 9 4.6a1.7 1.7 0 0 0 1-1.6v-.2h4V3a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1h.2v4H21a1.7 1.7 0 0 0-1.6 1Z" />
    </>,
    props
  );

export const HistoryIcon = (props: IconProps = {}) =>
  icon(
    <>
      <path d="M3 12a9 9 0 1 0 3-6.7L3 8" />
      <path d="M3 3v5h5m4-2v6l4 2" />
    </>,
    props
  );

export const MemoryIcon = (props: IconProps = {}) =>
  icon(
    <path d="M9 4.5A3 3 0 0 0 4.7 8a3.5 3.5 0 0 0 .6 6.6A3.3 3.3 0 0 0 9 19.5M15 4.5A3 3 0 0 1 19.3 8a3.5 3.5 0 0 1-.6 6.6 3.3 3.3 0 0 1-3.7 4.9M9 3v18m6-18v18M9 8H7m8 4h3M9 16H6m9 2h2" />,
    props
  );

export const MicIcon = (props: IconProps = {}) =>
  icon(
    <>
      <rect x="9" y="3" width="6" height="12" rx="3" />
      <path d="M5 11a7 7 0 0 0 14 0m-7 7v3m-4 0h8" />
    </>,
    props
  );

export const SendIcon = (props: IconProps = {}) =>
  icon(
    <>
      <path d="m21 3-8 18-2-8-8-2 18-8Z" />
      <path d="m11 13 4-4" />
    </>,
    props
  );

export const PlusIcon = (props: IconProps = {}) => icon(<path d="M12 5v14M5 12h14" />, props);

export const CopyIcon = (props: IconProps = {}) =>
  icon(
    <>
      <rect x="8" y="8" width="11" height="11" rx="2" />
      <path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3" />
    </>,
    props
  );

export const FolderIcon = (props: IconProps = {}) =>
  icon(<path d="M3 6a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6Z" />, props);

export const CloseIcon = (props: IconProps = {}) =>
  icon(<path d="m6 6 12 12M18 6 6 18" />, props);

export const ArrowIcon = (props: IconProps = {}) =>
  icon(<path d="M5 12h14m-5-5 5 5-5 5" />, props);

export const CheckIcon = (props: IconProps = {}) => icon(<path d="m5 12 4 4L19 6" />, props);

export const BackIcon = (props: IconProps = {}) => icon(<path d="M19 12H5m5 5-5-5 5-5" />, props);

export const RefreshIcon = (props: IconProps = {}) =>
  icon(
    <>
      <path d="M20 11a8 8 0 1 0-2.3 5.7L20 14" />
      <path d="M20 6v5h-5" />
    </>,
    props
  );

export const ExternalIcon = (props: IconProps = {}) =>
  icon(
    <>
      <path d="M14 5h5v5M19 5l-9 9" />
      <path d="M19 13v5a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5" />
    </>,
    props
  );

export const MoreIcon = (props: IconProps = {}) =>
  icon(
    <>
      <circle cx="5" cy="12" r="1" />
      <circle cx="12" cy="12" r="1" />
      <circle cx="19" cy="12" r="1" />
    </>,
    props
  );

export const accentColors: Record<string, string> = {
  amber: "#f7b955",
  violet: "#9b87f5",
  indigo: "#7c8cff",
  pink: "#ef88d0",
  cyan: "#41d9e8",
  blue: "#51a8ff",
  orange: "#ff9f68",
  emerald: "#42d6a4",
  teal: "#4bd3c4",
  lime: "#a7dd67",
  rose: "#ff8197"
};

export function waveMarkup(): JSX.Element {
  return (
    <div className="wave">
      {Array.from({ length: 17 }, (_, index) => (
        <span key={index} />
      ))}
    </div>
  );
}
