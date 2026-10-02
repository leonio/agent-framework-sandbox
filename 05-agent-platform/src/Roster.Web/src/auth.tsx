import { createContext, useContext, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api, isUnauthorized, registerUrl, signInUrl } from './api/client';
import type { Me } from './api/types';
import { ErrorNote, Spinner } from './components/ui';

/**
 * Who is signed in. The app asks /api/auth/me on load: a 401 means nobody is, so it shows the sign-in page instead of
 * the app. Signing in itself happens on Keycloak's pages; the buttons are plain links into the API, which runs the
 * round trip and sends the person back to where they were. The browser only ever holds a session cookie.
 */

const MeContext = createContext<Me | null>(null);

export function useMe(): Me {
  const me = useContext(MeContext);
  if (!me) throw new Error('useMe outside the signed-in part of the app.');
  return me;
}

export const isAdmin = (me: Me) => me.roles.includes('admin');

/** "Priya Nair · Security Engineer", how people appear everywhere. */
export const nameAndTitle = (name: string, title: string | null | undefined) => (title ? `${name} · ${title}` : name);

export function AuthGate({ children }: { children: ReactNode }) {
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/api/auth/me'), retry: false });

  if (me.isPending) {
    return (
      <div className="flex h-screen items-center justify-center">
        <Spinner />
      </div>
    );
  }

  if (isUnauthorized(me.error)) return <SignIn />;
  if (me.error) {
    return (
      <div className="mx-auto max-w-md p-8">
        <ErrorNote error={me.error} />
      </div>
    );
  }

  return <MeContext.Provider value={me.data}>{children}</MeContext.Provider>;
}

function SignIn() {
  const here = window.location.pathname + window.location.search;
  return (
    <div className="flex min-h-screen items-center justify-center p-6">
      <div className="w-full max-w-md rounded-2xl border border-slate-200 bg-white p-8 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <h1 className="text-2xl font-semibold tracking-tight">Roster</h1>
        <p className="mt-2 text-sm text-slate-600 dark:text-slate-400">
          A team of agents reviews your pull requests. You decide what stands, and then tell them, in a short retro, how they did.
        </p>
        <div className="mt-6 flex flex-col gap-3">
          <a href={signInUrl(here)} className="rounded-lg bg-indigo-600 px-4 py-2 text-center text-sm font-medium text-white hover:bg-indigo-500">
            Sign in
          </a>
          <a
            href={registerUrl(here)}
            className="rounded-lg border border-slate-300 px-4 py-2 text-center text-sm font-medium text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
          >
            Create an account
          </a>
        </div>
        <p className="mt-6 text-xs text-slate-500">Accounts live in Keycloak; Roster keeps only your title and your settings.</p>
      </div>
    </div>
  );
}
