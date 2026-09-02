import { createContext, useCallback, useContext, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { createPortal } from "react-dom";

/**
 * Toast notification system. Ports the original `toast(message, type, duration)` helper
 * from wwwroot/app.js, which appended a transient `<div class="toast {type}">` into
 * `#toast-region` (declared once in index.html) and removed it after `duration` ms.
 */

export type ToastType = "" | "success" | "error";

interface ToastItem {
  id: number;
  message: string;
  type: ToastType;
}

type ToastFn = (message: string, type?: ToastType, duration?: number) => void;

const ToastContext = createContext<ToastFn | null>(null);

let nextId = 0;

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastItem[]>([]);
  const [portalTarget, setPortalTarget] = useState<Element | null>(null);
  const timers = useRef(new Map<number, ReturnType<typeof setTimeout>>());

  useEffect(() => {
    setPortalTarget(document.getElementById("toast-region"));
    const activeTimers = timers.current;
    return () => {
      activeTimers.forEach(timer => clearTimeout(timer));
      activeTimers.clear();
    };
  }, []);

  const show = useCallback<ToastFn>((message, type = "", duration = 4200) => {
    const id = nextId++;
    setToasts(current => [...current, { id, message, type }]);
    const timer = setTimeout(() => {
      setToasts(current => current.filter(item => item.id !== id));
      timers.current.delete(id);
    }, duration);
    timers.current.set(id, timer);
  }, []);

  return (
    <ToastContext.Provider value={show}>
      {children}
      {portalTarget &&
        createPortal(
          <>
            {toasts.map(item => (
              <div key={item.id} className={`toast ${item.type}`}>
                {item.message}
              </div>
            ))}
          </>,
          portalTarget
        )}
    </ToastContext.Provider>
  );
}

export function useToast(): ToastFn {
  const context = useContext(ToastContext);
  if (!context) {
    throw new Error("useToast must be used within a ToastProvider.");
  }
  return context;
}

/**
 * Module-level escape hatch for non-component code (e.g. lib/voice.ts) that needs to
 * surface a toast outside of React's render tree, mirroring the original global
 * `toast()` function. Set once by <ToastProvider> on mount.
 */
let globalToast: ToastFn = () => {};

export function registerGlobalToast(fn: ToastFn): void {
  globalToast = fn;
}

export function toast(message: string, type: ToastType = "", duration = 4200): void {
  globalToast(message, type, duration);
}

export function useRegisterGlobalToast(): void {
  const show = useToast();
  useEffect(() => registerGlobalToast(show), [show]);
}
