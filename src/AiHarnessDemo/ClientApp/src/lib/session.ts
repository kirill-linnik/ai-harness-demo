/**
 * sessionStorage-backed handoff flags used when navigating from the AI Factory overview
 * page into intake, mirroring the original wwwroot/app.js behavior:
 * - A quick-prompt chip pre-fills the composer textarea with its prompt text.
 * - "Listen to the next task" / the orbiting mic button starts voice capture shortly
 *   after the intake page mounts.
 */

const DRAFT_PROMPT_KEY = "harness-draft-prompt";
const START_VOICE_KEY = "harness-start-voice";

export function setDraftPrompt(prompt: string): void {
  sessionStorage.setItem(DRAFT_PROMPT_KEY, prompt);
}

/** Reads and clears the pending draft prompt, if any. */
export function consumeDraftPrompt(): string {
  const draft = sessionStorage.getItem(DRAFT_PROMPT_KEY) || "";
  sessionStorage.removeItem(DRAFT_PROMPT_KEY);
  return draft;
}

export function setStartVoiceHint(): void {
  sessionStorage.setItem(START_VOICE_KEY, "1");
}

/** Reads and clears the pending "start voice capture" hint, if any. */
export function consumeStartVoiceHint(): boolean {
  const shouldStart = sessionStorage.getItem(START_VOICE_KEY) === "1";
  sessionStorage.removeItem(START_VOICE_KEY);
  return shouldStart;
}
