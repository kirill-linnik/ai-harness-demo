import { useEffect, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { AppShell } from "../../components/AppShell";
import { BootScreen } from "../../components/BootScreen";
import { FatalScreen } from "../../components/FatalScreen";
import {
  queryKeys,
  useBootstrapQuery,
  useContinueIntakeMutation,
  useFlowQuery
} from "../../api/queries";
import { ApiError } from "../../api/client";
import type { FlowMessageDto, FlowStatus } from "../../api/types";
import { flowRoute } from "../../lib/flowRoute";
import { MicIcon, SendIcon } from "../../lib/icons";
import { consumeDraftPrompt, consumeStartVoiceHint } from "../../lib/session";
import { speak, toggleVoice } from "../../lib/voice";
import { useToast } from "../../lib/toast";
import { MessageBubble } from "./MessageBubble";
import { FactoryLockedPage } from "../factory/FactoryLockedPage";

interface PendingCustomerMessage {
  message: FlowMessageDto;
  existingMessageIds: ReadonlySet<string>;
}

const MAX_FILES_PER_MESSAGE = 8;
const MAX_FILE_BYTES = 8 * 1024 * 1024;
const MAX_FLOW_BYTES = 16 * 1024 * 1024;

export function IntakePage() {
  const params = useParams<{ id?: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const toast = useToast();

  const bootstrapQuery = useBootstrapQuery();
  const flowQuery = useFlowQuery(params.id);
  const flow = flowQuery.data ?? null;
  const continueIntake = useContinueIntakeMutation();

  const [message, setMessage] = useState("");
  const [files, setFiles] = useState<File[]>([]);
  const [recording, setRecording] = useState(false);
  const [pendingMessage, setPendingMessage] = useState<PendingCustomerMessage | null>(null);
  const logRef = useRef<HTMLDivElement | null>(null);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const initialized = useRef(false);
  const submitting = useRef(false);
  const messages = flow?.messages ?? [];
  const pendingMessagePersisted = pendingMessage
    ? messages.some(item =>
        !pendingMessage.existingMessageIds.has(item.id) &&
        item.role === pendingMessage.message.role &&
        item.content === pendingMessage.message.content
      )
    : false;
  const visibleMessages =
    pendingMessage && !pendingMessagePersisted
      ? [...messages, pendingMessage.message]
      : messages;

  useEffect(() => {
    if (initialized.current) return;
    initialized.current = true;
    const draft = consumeDraftPrompt();
    if (draft) setMessage(draft);
    if (consumeStartVoiceHint()) {
      setTimeout(
        () => toggleVoice("intake-message", "intake-mic", setMessage, setRecording),
        250
      );
    }
  }, []);

  useEffect(() => {
    const log = logRef.current;
    if (log) log.scrollTop = log.scrollHeight;
  }, [visibleMessages.length]);

  useEffect(() => {
    if (flow && flow.status !== "Intake") {
      navigate(`/factory/${flow.id}`, { replace: true });
    }
  }, [flow?.id, flow?.status, navigate]);

  // Existing flows (already queued/running/etc.) remain reachable even if the factory has since
  // been disabled; only a brand-new intake conversation is gated, mirroring the original
  // `route.page === "intake" && !state.bootstrap.factoryEnabled` check in renderRoute().
  if (!params.id && bootstrapQuery.data && !bootstrapQuery.data.factoryEnabled) {
    return <FactoryLockedPage />;
  }
  if (params.id && flowQuery.isLoading) return <BootScreen />;
  if (params.id && flowQuery.isError) {
    const error = flowQuery.error;
    return (
      <FatalScreen
        title="The intake could not load"
        message={error instanceof Error ? error.message : String(error)}
        onRetry={() => void flowQuery.refetch()}
      />
    );
  }

  const latestIntakeEvent = flow?.events.find(item => item.type.startsWith("intake."));
  const confirmed = latestIntakeEvent?.type === "intake.confirmed";
  const awaitingConfirmation =
    latestIntakeEvent?.type === "intake.confirmation_requested" ||
    latestIntakeEvent?.type === "intake.ready";
  const proposedKind =
    flow && (awaitingConfirmation || confirmed)
      ? flow.kind
      : null;

  async function submitIntake() {
    if (submitting.current) return;

    const trimmed = message.trim() ||
      (files.length > 0 && flow ? "I attached the requested files." : "");
    if (!trimmed) {
      toast("Describe the change before sending.", "error");
      return;
    }

    submitting.current = true;
    setPendingMessage({
      message: {
        id: "pending-customer-message",
        role: "Customer",
        content: trimmed,
        isQuestion: false,
        createdAt: new Date().toISOString(),
        attachments: files.map((file, index) => ({
          id: `pending-file-${index}`,
          fileName: file.name,
          contentType: file.type,
          length: file.size
        }))
      },
      existingMessageIds: new Set(messages.map(item => item.id))
    });
    setMessage("");

    try {
      const response = await continueIntake.mutateAsync({
        flowId: flow?.id ?? params.id ?? null,
        message: trimmed,
        ...(files.length ? { files } : {})
      });
      queryClient.setQueryData(queryKeys.flow(response.flow.id), response.flow);
      setPendingMessage(null);
      setFiles([]);
      if (fileInputRef.current) fileInputRef.current.value = "";
      if (response.shouldSpeak) speak(response.reply);
      if (response.readyToStart || response.flow.status !== "Intake") {
        navigate(`/factory/${response.flow.id}`, { replace: true });
        return;
      }
      if (!params.id || params.id !== response.flow.id) {
        navigate(`/intake/${response.flow.id}`, { replace: true });
      }
    } catch (error) {
      setPendingMessage(null);
      setMessage(current => current || trimmed);
      if (error instanceof ApiError) {
        const recoveredFlowId = error.extensions.flowId;
        const recoveredStatus = error.extensions.flowStatus;
        const retryMessage = error.extensions.retryMessage;
        if (typeof recoveredFlowId === "string" && recoveredFlowId.length > 0) {
          navigate(
            flowRoute(
              recoveredFlowId,
              typeof recoveredStatus === "string"
                ? recoveredStatus as FlowStatus
                : undefined
            ),
            { replace: true }
          );
        }
        toast(
          typeof retryMessage === "string" && retryMessage.length > 0
            ? retryMessage
            : error.message,
          "error"
        );
      } else {
        toast(error instanceof Error ? error.message : String(error), "error");
      }
    } finally {
      submitting.current = false;
    }
  }

  const sending = continueIntake.isPending || pendingMessage !== null;
  const usedBytes = messages.reduce(
    (total, item) =>
      total + (item.attachments ?? []).reduce((sum, file) => sum + file.length, 0),
    0
  );

  function addFiles(selected: FileList | null) {
    if (!selected) return;
    const next = [...files, ...Array.from(selected)];
    if (next.length > MAX_FILES_PER_MESSAGE ||
        next.some(file => file.size > MAX_FILE_BYTES) ||
        usedBytes + next.reduce((total, file) => total + file.size, 0) > MAX_FLOW_BYTES) {
      toast("Attach up to 8 files (8 MB each, 16 MB per flow).", "error");
    } else {
      setFiles(next);
    }
    if (fileInputRef.current) fileInputRef.current.value = "";
  }

  return (
    <AppShell
      active="factory"
      title="Customer intake"
      subtitle="Voice dialogue · explicit customer confirmation · durable brief"
    >
      <div className="page-head">
        <div>
          <div className="eyebrow">Customer intake</div>
          <h2>Talk to your Account Manager</h2>
          <p>Your voice becomes a durable brief. Clarifications stay attached to the flow and follow every specialist.</p>
        </div>
      </div>
      <section className="intake-layout">
        <div className="card conversation">
          <div className="card-header">
            <div>
              <h3>Live brief</h3>
              <p>
                {flow
                  ? `Flow ${flow.id.slice(0, 8)} · ${visibleMessages.length} dialogue turns`
                  : "A new flow starts with your first message."}
              </p>
            </div>
            {confirmed ? (
              <span className="status-pill approved">Brief confirmed</span>
            ) : awaitingConfirmation ? (
              <span className="status-pill intake">Confirm brief</span>
            ) : (
              <span className="status-pill intake">Clarifying</span>
            )}
          </div>
          <div className="conversation-log" id="conversation-log" ref={logRef}>
            {visibleMessages.length ? (
              visibleMessages.map(item => <MessageBubble key={item.id} message={item} />)
            ) : (
              <div className="message accountmanager">
                <div className="message-avatar">AM</div>
                <div className="message-bubble">
                  <strong>Account Manager</strong>
                  Tell me what you want the team to change. I already have the selected repository context.
                </div>
              </div>
            )}
          </div>
          {proposedKind && (
            <section
              className="intake-proposal"
              aria-labelledby="proposed-flow-kind"
              aria-live="polite"
            >
              <h4 id="proposed-flow-kind">Proposed as {proposedKind}</h4>
              <p>
                {proposedKind === "Advisory"
                  ? "The team will inspect the repository and return recommendations without changing or publishing source code."
                  : "The team will implement the change in an isolated workspace. Nothing is published until you approve the reviewed result."}
              </p>
            </section>
          )}
          <div className="composer">
            <div className="composer-attachments">
              <label htmlFor="intake-files">Attach files for the Account Manager</label>
              <input
                id="intake-files"
                type="file"
                multiple
                ref={fileInputRef}
                disabled={sending}
                onChange={event => addFiles(event.target.files)}
              />
              {files.length > 0 && (
                <ul aria-label="Files ready to send">
                  {files.map((file, index) => (
                    <li key={`${file.name}-${index}`}>
                      <span>{file.name} ({Math.ceil(file.size / 1024)} KB)</span>
                      <button
                        type="button"
                        aria-label={`Remove ${file.name}`}
                        disabled={sending}
                        onClick={() => setFiles(current =>
                          current.filter((_, itemIndex) => itemIndex !== index))}
                      >
                        Remove
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
            <div className="composer-row">
              <button
                id="intake-mic"
                className={`icon-button intake-mic-toggle${recording ? " recording" : ""}`}
                aria-label={recording ? "Stop voice recording" : "Start voice recording"}
                aria-pressed={recording}
                onClick={() =>
                  toggleVoice("intake-message", "intake-mic", setMessage, setRecording)
                }
              >
                <MicIcon />
                <span>{recording ? "Recording" : "Mic off"}</span>
              </button>
              <textarea
                id="intake-message"
                placeholder={
                  awaitingConfirmation
                    ? "Reply yes, or tell me what to change..."
                    : "Describe the customer outcome..."
                }
                aria-label="Customer request"
                value={message}
                onChange={event => setMessage(event.target.value)}
                onKeyDown={event => {
                  if (event.key === "Enter" && !event.shiftKey) {
                    event.preventDefault();
                    void submitIntake();
                  }
                }}
              />
              <button className="button primary" disabled={sending} onClick={() => void submitIntake()}>
                <SendIcon /> {sending ? "Thinking..." : "Send"}
              </button>
            </div>
            <div className="composer-hint">Upload files outside the selected project instead of granting folder access. Use voice in Edge or Chrome on localhost, or type when the room is noisy.</div>
          </div>
        </div>
      </section>
    </AppShell>
  );
}
