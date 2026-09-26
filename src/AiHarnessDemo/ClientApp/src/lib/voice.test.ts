import { afterEach, describe, expect, it, vi } from "vitest";
import { toggleVoice } from "./voice";

type RecognitionResult = ArrayLike<{ transcript: string }> & { isFinal: boolean };

interface RecognitionEvent {
  results: ArrayLike<RecognitionResult>;
}

class MockSpeechRecognition extends EventTarget {
  static current: MockSpeechRecognition | null = null;

  lang = "";
  continuous = false;
  interimResults = false;
  onstart: (() => void) | null = null;
  onresult: ((event: RecognitionEvent) => void) | null = null;
  onerror: ((event: { error: string }) => void) | null = null;
  onend: (() => void) | null = null;

  constructor() {
    super();
    MockSpeechRecognition.current = this;
  }

  start(): void {
    this.onstart?.();
  }

  stop(): void {
    this.onend?.();
  }
}

describe("toggleVoice", () => {
  afterEach(() => {
    MockSpeechRecognition.current?.onend?.();
    MockSpeechRecognition.current = null;
    document.body.replaceChildren();
    Object.defineProperty(window, "SpeechRecognition", {
      configurable: true,
      value: undefined
    });
  });

  it("publishes transcripts through the controlled-input callback", () => {
    document.body.innerHTML = `
      <textarea id="message">Existing request</textarea>
      <button id="microphone">Record</button>
    `;
    Object.defineProperty(window, "SpeechRecognition", {
      configurable: true,
      value: MockSpeechRecognition
    });
    const onTranscript = vi.fn();

    toggleVoice("message", "microphone", onTranscript);

    const result = Object.assign([{ transcript: "with more detail" }], { isFinal: true });
    MockSpeechRecognition.current?.onresult?.({ results: [result] });

    expect(onTranscript).toHaveBeenCalledWith("Existing request with more detail");
    expect(document.querySelector<HTMLTextAreaElement>("#message")?.value).toBe("Existing request");
  });

  it("reports microphone activation, stopping, and natural completion", () => {
    document.body.innerHTML = `
      <textarea id="message"></textarea>
      <button id="microphone" aria-pressed="false">Mic off</button>
    `;
    Object.defineProperty(window, "SpeechRecognition", {
      configurable: true,
      value: MockSpeechRecognition
    });
    const onRecordingChange = vi.fn();
    const button = document.querySelector<HTMLButtonElement>("#microphone")!;

    toggleVoice("message", "microphone", undefined, onRecordingChange);
    expect(button).toHaveClass("recording");
    expect(button).toHaveAttribute("aria-pressed", "true");
    expect(button).toHaveAttribute("aria-label", "Stop voice recording");

    toggleVoice("message", "microphone", undefined, onRecordingChange);
    expect(button).not.toHaveClass("recording");
    expect(button).toHaveAttribute("aria-pressed", "false");

    toggleVoice("message", "microphone", undefined, onRecordingChange);
    MockSpeechRecognition.current?.onend?.();
    expect(button).not.toHaveClass("recording");
    expect(button).toHaveAttribute("aria-pressed", "false");
    expect(onRecordingChange.mock.calls.map(([recording]) => recording))
      .toEqual([true, false, true, false]);
  });
});
