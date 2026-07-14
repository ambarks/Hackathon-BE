// Tracks how many API calls are currently in flight, app-wide. client.ts's
// central request() function increments/decrements this on every call, so any
// backend call anywhere in the app — not just ones a page remembered to wire
// up its own spinner for — drives the global blocking overlay in Layout.tsx.
type Listener = (count: number) => void;

let activeRequests = 0;
const listeners = new Set<Listener>();

export function subscribeLoading(listener: Listener): () => void {
  listeners.add(listener);
  listener(activeRequests);
  return () => {
    listeners.delete(listener);
  };
}

export function beginRequest(): void {
  activeRequests += 1;
  listeners.forEach((listener) => listener(activeRequests));
}

export function endRequest(): void {
  activeRequests = Math.max(0, activeRequests - 1);
  listeners.forEach((listener) => listener(activeRequests));
}
