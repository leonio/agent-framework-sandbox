import { useCallback, useEffect, useRef, useState } from "react";

/**
 * Loads data and reloads it whenever the server pushes a change over Server-Sent Events.
 * WHY SSE + refetch instead of streaming full state? The events are tiny "something changed" signals and
 * the API stays the single source of truth; the UI can never drift from the database.
 * A slow poll is kept as a safety net in case the event stream drops (e.g. behind some proxies).
 */
export function useLiveData<T>(load: () => Promise<T>, eventsUrl: string, deps: unknown[] = []) {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<string>();
  const loadRef = useRef(load);
  loadRef.current = load;

  const reload = useCallback(async () => {
    try {
      setData(await loadRef.current());
      setError(undefined);
    } catch (e) {
      setError((e as Error).message);
    }
  }, []);

  useEffect(() => {
    void reload();
    let timer: number | undefined;
    // Coalesce bursts of events (a review emits several) into one refetch.
    const schedule = () => {
      window.clearTimeout(timer);
      timer = window.setTimeout(() => void reload(), 250);
    };
    const source = new EventSource(eventsUrl);
    source.addEventListener("run", schedule);
    const poll = window.setInterval(() => void reload(), 10_000);
    return () => {
      source.close();
      window.clearInterval(poll);
      window.clearTimeout(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [eventsUrl, reload, ...deps]);

  return { data, error, reload };
}

/** Wraps an async action with a busy flag and error message, for buttons. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const run = useCallback(async (action: () => Promise<unknown>) => {
    setBusy(true);
    setError(undefined);
    try {
      await action();
      return true;
    } catch (e) {
      setError((e as Error).message);
      return false;
    } finally {
      setBusy(false);
    }
  }, []);
  return { busy, error, run };
}
