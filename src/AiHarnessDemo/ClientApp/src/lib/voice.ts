import { toast } from "./toast";

/**
 * Direct port of the voice recognition/synthesis helpers from wwwroot/app.js.
 * Only one recognition session is active at a time (module-level singleton, matching
 * the original `state.recognition` / `state.activeVoiceButton`), and it is driven by
 * DOM element ids exactly like the vanilla implementation so the same markup ids
 * (`intake-message`, `intake-mic`, `feedback-message`, ...) keep working unchanged.
 */

interface SpeechRecognitionLike extends EventTarget {
  lang: string;
  continuous: boolean;
  interimResults: boolean;
  onstart: (() => void) | null;
  onresult: ((event: SpeechRecognitionEventLike) => void) | null;
  onerror: ((event: { error: string }) => void) | null;
  onend: (() => void) | null;
  start(): void;
  stop(): void;
}

interface SpeechRecognitionEventLike {
  results: ArrayLike<ArrayLike<{ transcript: string }> & { isFinal: boolean }>;
}

type SpeechRecognitionConstructor = new () => SpeechRecognitionLike;

declare global {
  interface Window {
    SpeechRecognition?: SpeechRecognitionConstructor;
    webkitSpeechRecognition?: SpeechRecognitionConstructor;
  }
}

let activeRecognition: SpeechRecognitionLike | null = null;

export function isVoiceRecognitionAvailable(): boolean {
  return Boolean(window.SpeechRecognition || window.webkitSpeechRecognition);
}

export function toggleVoice(
  textareaId: string,
  buttonId: string,
  onTranscript?: (transcript: string) => void
): void {
  if (activeRecognition) {
    activeRecognition.stop();
    return;
  }

  const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (!Recognition) {
    toast(
      "Voice recognition is not available in this browser. Use Edge or Chrome, or type the message.",
      "error",
      6000
    );
    document.querySelector<HTMLElement>(`#${textareaId}`)?.focus();
    return;
  }

  const textarea = document.querySelector<HTMLTextAreaElement>(`#${textareaId}`);
  const button = document.querySelector<HTMLButtonElement>(`#${buttonId}`);
  if (!textarea || !button) return;

  const recognition = new Recognition();
  const original = textarea.value.trim();
  recognition.lang = "en-US";
  recognition.continuous = true;
  recognition.interimResults = true;
  activeRecognition = recognition;

  recognition.onstart = () => {
    button.classList.add("recording");
    document.querySelector("#intake-signal")?.classList.add("listening");
    button.setAttribute("aria-label", "Stop voice recording");
  };
  recognition.onresult = event => {
    let finalText = "";
    let interimText = "";
    for (let index = 0; index < event.results.length; index++) {
      const transcript = event.results[index]![0]!.transcript;
      if (event.results[index]!.isFinal) finalText += transcript;
      else interimText += transcript;
    }
    const prefix = original ? `${original} ` : "";
    const transcript = `${prefix}${finalText}${interimText}`.trim();
    if (onTranscript) {
      onTranscript(transcript);
    } else {
      textarea.value = transcript;
    }
  };
  recognition.onerror = event => {
    if (event.error !== "aborted" && event.error !== "no-speech") {
      toast(`Voice recognition stopped: ${event.error}.`, "error");
    }
  };
  recognition.onend = () => {
    button.classList.remove("recording");
    document.querySelector("#intake-signal")?.classList.remove("listening");
    button.setAttribute("aria-label", "Start voice recording");
    activeRecognition = null;
  };
  recognition.start();
}

export function speak(text: string): void {
  if (!("speechSynthesis" in window) || !text) return;
  window.speechSynthesis.cancel();
  const utterance = new SpeechSynthesisUtterance(text.split("\n\n")[0]);
  utterance.rate = 1.02;
  utterance.pitch = 0.96;
  const voices = window.speechSynthesis.getVoices();
  utterance.voice =
    voices.find(voice => voice.lang.startsWith("en") && /natural|aria|guy|jenny/i.test(voice.name)) ||
    voices.find(voice => voice.lang.startsWith("en")) ||
    null;
  window.speechSynthesis.speak(utterance);
}
