import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import type { FlowDetailDto } from "../../api/types";
import { queryKeys, useDecideFlowMutation, useSendFeedbackMutation } from "../../api/queries";
import { hasFeedbackBeenSent, markFeedbackSent } from "../../lib/feedbackTracker";
import { CheckIcon, ExternalIcon, MicIcon, RefreshIcon, SendIcon } from "../../lib/icons";
import { speak, toggleVoice } from "../../lib/voice";
import { useToast } from "../../lib/toast";
import { AbandonFlowButton } from "./AbandonFlowButton";

export function FeedbackCard({ flow }: { flow: FlowDetailDto }) {
  const queryClient = useQueryClient();
  const toast = useToast();
  const sendFeedback = useSendFeedbackMutation();
  const decideFlow = useDecideFlowMutation();
  const [message, setMessage] = useState("");

  const reviewed = hasFeedbackBeenSent(flow.id) || flow.messages.some(item => item.role === "ProductManager");
  const productReplies = flow.messages.filter(item => item.role === "ProductManager" || item.role === "Harness");
  const lastReply = productReplies.at(-1);

  async function submitFeedback() {
    const trimmed = message.trim();
    if (!trimmed) {
      toast("Record or type feedback first.", "error");
      return;
    }
    try {
      const response = await sendFeedback.mutateAsync({ flowId: flow.id, body: { message: trimmed } });
      markFeedbackSent(flow.id);
      queryClient.setQueryData(queryKeys.flow(flow.id), response.flow);
      setMessage("");
      if (response.shouldSpeak) speak(response.reply);
    } catch (error) {
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  async function decide(approve: boolean) {
    try {
      const updated = await decideFlow.mutateAsync({ flowId: flow.id, approve });
      queryClient.setQueryData(queryKeys.flow(flow.id), updated);
      toast(
        approve
          ? "Approval recorded. Publishing the pull request now."
          : "Feedback retained. New iteration started.",
        "success"
      );
    } catch (error) {
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  return (
    <section className="card feedback-card">
      <div className="card-header">
        <div>
          <h3>Customer acceptance loop</h3>
          <p>Open the preview, then speak feedback to Product Manager with the full execution ledger attached.</p>
        </div>
        <a className="button small" href={flow.outcomeUrl}>
          <ExternalIcon /> Open preview
        </a>
      </div>
      <div className="card-body">
        {lastReply && (
          <div className="message productmanager" style={{ maxWidth: "100%", marginBottom: 16 }}>
            <div className="message-avatar">PM</div>
            <div className="message-bubble">
              <strong>Product Manager</strong>
              {lastReply.content}
            </div>
          </div>
        )}
        <div className="feedback-layout">
          <div className="field">
            <label htmlFor="feedback-message">Customer feedback</label>
            <textarea
              id="feedback-message"
              rows={3}
              placeholder="Tell Product Manager what worked or what should change..."
              value={message}
              onChange={event => setMessage(event.target.value)}
            />
          </div>
          <button
            id="feedback-mic"
            className="mic-button"
            style={{ width: 58, height: 58 }}
            aria-label="Record feedback"
            onClick={() => toggleVoice("feedback-message", "feedback-mic", setMessage)}
          >
            <MicIcon />
          </button>
        </div>
        <div className="feedback-actions">
          <AbandonFlowButton flowId={flow.id} />
          <button className="button" disabled={sendFeedback.isPending} onClick={() => void submitFeedback()}>
            <SendIcon /> {sendFeedback.isPending ? "Product Manager..." : "Discuss feedback"}
          </button>
          <button
            className="button danger"
            disabled={!reviewed || decideFlow.isPending}
            onClick={() => void decide(false)}
          >
            <RefreshIcon /> {decideFlow.isPending ? "Queuing..." : "Re-do with feedback"}
          </button>
          <button className="button success" disabled={decideFlow.isPending} onClick={() => void decide(true)}>
            <CheckIcon /> {decideFlow.isPending ? "Queuing publication..." : "Approve and publish"}
          </button>
        </div>
      </div>
    </section>
  );
}
