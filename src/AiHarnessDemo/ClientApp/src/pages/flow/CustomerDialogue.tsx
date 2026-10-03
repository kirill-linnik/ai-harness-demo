import type { FlowDetailDto } from "../../api/types";
import { MessageBubble } from "../intake/MessageBubble";

export function CustomerDialogue({
  flow
}: {
  flow: Pick<FlowDetailDto, "messages">;
}) {
  const messages = [...flow.messages].sort(
    (left, right) =>
      new Date(left.createdAt).getTime() - new Date(right.createdAt).getTime()
  );

  return (
    <section className="card customer-dialogue" aria-labelledby="customer-dialogue-title">
      <div className="card-header">
        <div>
          <h3 id="customer-dialogue-title">Customer dialogue</h3>
          <p>
            Read-only saved customer-facing messages,
            including confirmations and later refinements.
          </p>
        </div>
      </div>
      <div className="card-body">
        <details className="operator-details">
          <summary>
            Recorded dialogue ({messages.length} {messages.length === 1 ? "message" : "messages"})
          </summary>
          {messages.length ? (
            <ol className="conversation-log" aria-label="Recorded dialogue" tabIndex={0}>
              {messages.map(message => (
                <li
                  className={`dialogue-turn ${message.role === "Customer" ? "customer" : ""}`}
                  key={message.id}
                >
                  <MessageBubble message={message} />
                  <time dateTime={message.createdAt}>
                    {new Date(message.createdAt).toLocaleString()}
                  </time>
                </li>
              ))}
            </ol>
          ) : (
            <p>No customer dialogue was recorded for this flow.</p>
          )}
        </details>
      </div>
    </section>
  );
}
