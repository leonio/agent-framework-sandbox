/**
 * The one way the web app talks to the API.
 *
 * - Same origin: Vite's dev server proxies /api to the API, so the session cookie just goes along (`same-origin`).
 * - Every request that changes something sends `X-Roster: 1`. The API refuses unsafe requests without it, which is
 *   what stops another site from posting forms at it with the person's cookie (see the design doc, section 10).
 * - Errors come back as RFC 9457 problem details; they become an `ApiError` whose message is the `detail`, written
 *   by the platform for people, so pages can show it as is.
 */

export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

/** True for "not signed in": the API answers 401, and the app shows the sign-in page. */
export const isUnauthorized = (error: unknown): boolean => error instanceof ApiError && error.status === 401;

export async function api<T>(path: string, options: { method?: string; body?: unknown } = {}): Promise<T> {
  const method = options.method ?? 'GET';
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (method !== 'GET') headers['X-Roster'] = '1';
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';

  const response = await fetch(path, {
    method,
    credentials: 'same-origin',
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  });

  const text = await response.text();
  const data = text ? safeJson(text) : undefined;
  if (!response.ok) {
    const problem = data as { detail?: string; title?: string } | undefined;
    throw new ApiError(response.status, problem?.detail ?? problem?.title ?? `${response.status} ${response.statusText}`);
  }

  return data as T;
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

/** Where the sign-in and register buttons go: the API starts the Keycloak round trip and brings the person back here. */
export const signInUrl = (returnUrl: string) => `/api/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`;
export const registerUrl = (returnUrl: string) => `/api/auth/register?returnUrl=${encodeURIComponent(returnUrl)}`;
