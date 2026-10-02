import type { FlowMessageDto } from "../../api/types";
import { splitWords } from "../../lib/format";

export function MessageBubble({ message }: { message: FlowMessageDto }) {
  const customer = message.role === "Customer";
  const initials =
    message.role === "Customer"
      ? "YOU"
      : message.role === "ProductManager"
        ? "PM"
        : message.role === "Harness"
          ? "AI"
          : "AM";
  const label = splitWords(message.role).join(" ");

  return (
    <div className={`message ${customer ? "customer" : message.role.toLowerCase()}`}>
      {customer ? (
        <>
          <div className="message-bubble">
            <strong>{label}</strong>
            {message.content}
            {message.attachments && message.attachments.length > 0 && (
              <ul className="message-attachments" aria-label="Uploaded files">
                {message.attachments.map(file => (
                  <li key={file.id}>{file.fileName} ({Math.ceil(file.length / 1024)} KB)</li>
                ))}
              </ul>
            )}
          </div>
          <div className="message-avatar">{initials}</div>
        </>
      ) : (
        <>
          <div className="message-avatar">{initials}</div>
          <div className="message-bubble">
            <strong>{label}</strong>
            {message.content}
          </div>
        </>
      )}
    </div>
  );
}
