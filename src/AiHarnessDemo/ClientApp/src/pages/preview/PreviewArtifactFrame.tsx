import { useEffect, useRef, useState } from "react";

export function PreviewArtifactFrame({ url, label }: { url: string; label: string }) {
  const frame = useRef<HTMLIFrameElement>(null);
  const outboundLink = useRef<HTMLAnchorElement>(null);
  const [destination, setDestination] = useState<string | null>(null);
  useEffect(() => {
    if (destination) outboundLink.current?.focus();
  }, [destination]);

  useEffect(() => {
    function receive(event: MessageEvent<unknown>) {
      if (event.source !== frame.current?.contentWindow || event.origin !== "null") return;
      const data = event.data;
      if (
        !data || typeof data !== "object" ||
        !("type" in data) || data.type !== "ai-harness-preview-outbound" ||
        !("href" in data) || typeof data.href !== "string" || data.href.length > 8192
      ) return;
      let target: URL;
      try {
        target = new URL(data.href);
      } catch {
        return;
      }
      if (
        !["https:", "http:", "mailto:"].includes(target.protocol) ||
        target.username || target.password || target.origin === window.location.origin
      ) return;
      setDestination(target.href);
    }
    window.addEventListener("message", receive);
    return () => window.removeEventListener("message", receive);
  }, []);

  return (
    <>
      {destination && (
        <section className="pushback-callout preview-outbound-confirmation" aria-label="External destination" aria-live="polite">
          <p>This link leaves the offline preview. Open only if you trust this destination.</p>
          <a ref={outboundLink} className="button small preview-outbound-link"
            href={destination} target="_blank" rel="noopener noreferrer">
            {destination}
          </a>
          <button className="button small" type="button" onClick={() => {
            setDestination(null);
            frame.current?.focus();
          }}>Dismiss</button>
        </section>
      )}
      <iframe ref={frame} className="preview-frame" src={url}
        title={`${label} interactive customer preview`}
        sandbox="allow-scripts" referrerPolicy="no-referrer" />
    </>
  );
}
