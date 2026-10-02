import { useState, type FormEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { useCredentials, useEndpoints } from '../../api/queries';
import type { ModelEndpoint, Tier } from '../../api/types';
import { isAdmin, useMe } from '../../auth';
import { Badge, Button, Card, ConfirmButton, Empty, ErrorNote, field, Label, Spinner } from '../ui';

const tiers: Tier[] = ['fast', 'balanced', 'reasoning'];

/** What the add form holds; strings for the text boxes, turned into the API's shape on submit. */
type Draft = {
  name: string;
  kind: 'openai' | 'fake';
  baseUrl: string;
  defaultModel: string;
  tierModels: Record<Tier, string>;
  credentialId: string;
  nativeStructuredOutput: boolean;
  maxConcurrency: string;
  shared: boolean;
};

const emptyDraft: Draft = {
  name: '',
  kind: 'openai',
  baseUrl: '',
  defaultModel: '',
  tierModels: { fast: '', balanced: '', reasoning: '' },
  credentialId: '',
  nativeStructuredOutput: true,
  maxConcurrency: '4',
  shared: false,
};

/**
 * Model endpoints, as data (design doc section 8): where agents send their model calls.
 *
 * You see the shared endpoints (set up by an admin, used by everyone) and your own. Your own can use one of your
 * keys; the key never shows here, only its hint, and only to you. Each endpoint has a default model and, optionally,
 * a model per tier: an agent asks for a tier (fast, balanced or reasoning) and gets that endpoint's model for it.
 *
 * To change an endpoint, delete it and add it again; assignments that named it fall back to the routing defaults.
 */
export function EndpointsCard() {
  const me = useMe();
  const admin = isAdmin(me);
  const queryClient = useQueryClient();
  const endpoints = useEndpoints();

  const remove = useMutation({
    mutationFn: (id: string) => api(`/api/endpoints/${id}`, { method: 'DELETE' }),
    // Your default endpoint may have been the one deleted, so `me` can change too.
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['endpoints'] });
      queryClient.invalidateQueries({ queryKey: ['me'] });
    },
  });

  // Your own endpoints are yours to delete; shared ones are an admin's.
  const editable = (e: ModelEndpoint) => e.mine || (e.shared && admin);

  return (
    <Card title="Model endpoints">
      <div className="space-y-5">
        {endpoints.isPending ? (
          <Spinner />
        ) : endpoints.error ? (
          <ErrorNote error={endpoints.error} />
        ) : endpoints.data.length === 0 ? (
          <Empty>No endpoints yet.{admin ? ' Add a shared one so everyone has somewhere to run.' : ' Ask an admin for a shared one, or add your own.'}</Empty>
        ) : (
          <ul className="divide-y divide-slate-100 dark:divide-slate-800" data-testid="endpoints">
            {endpoints.data.map(e => (
              <li key={e.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-3 text-sm">
                <span className="font-medium">{e.name}</span>
                <Badge tone={e.shared ? 'sky' : 'indigo'}>{e.shared ? 'Shared' : 'Yours'}</Badge>
                <Badge>{e.kind}</Badge>
                {!e.nativeStructuredOutput && <Badge tone="amber">JSON by prompt</Badge>}
                <span className="ml-auto">
                  {editable(e) && <ConfirmButton label="Delete" confirmLabel="Really delete?" disabled={remove.isPending} onConfirm={() => remove.mutate(e.id)} />}
                </span>
                <EndpointDetails endpoint={e} />
              </li>
            ))}
          </ul>
        )}
        <ErrorNote error={remove.error} />
        <AddEndpointForm admin={admin} />
      </div>
    </Card>
  );
}

/** The second line of an endpoint: its models, where it points, which key, and how hard it may be pushed. */
function EndpointDetails({ endpoint: e }: { endpoint: ModelEndpoint }) {
  const tierText = tiers
    .filter(t => e.tierModels[t])
    .map(t => `${t}: ${e.tierModels[t]}`)
    .join(', ');

  return (
    <p className="w-full text-xs text-slate-500">
      default model <code>{e.defaultModel}</code>
      {tierText && <> · {tierText}</>}
      {e.baseUrl && <> · {e.baseUrl}</>}
      {e.credentialHint && <> · key {e.credentialHint}</>}
      {' · '}up to {e.maxConcurrency} calls at once
    </p>
  );
}

/**
 * Adding an endpoint: your own, or (for admins) a shared one.
 *
 * The form offers what slice 1 acts on. The API also takes tool and reasoning-model flags and a requests-per-minute
 * cap, which nothing reads yet (rate limiting is still open in the design doc), so they keep their defaults; so does
 * streaming, which the retro chat uses and every OpenAI-compatible server supports.
 */
function AddEndpointForm({ admin }: { admin: boolean }) {
  const queryClient = useQueryClient();
  const credentials = useCredentials();
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const set = (patch: Partial<Draft>) => setDraft(d => ({ ...d, ...patch }));

  // Only API keys can sign model calls; GitHub tokens are for Copilot, in slice 2.
  const keys = credentials.data?.filter(c => c.kind === 'api-key') ?? [];

  const add = useMutation({
    mutationFn: () => {
      return api<ModelEndpoint>('/api/endpoints', {
        method: 'POST',
        body: {
          name: draft.name.trim(),
          kind: draft.kind,
          baseUrl: draft.kind === 'openai' ? draft.baseUrl.trim() || null : null,
          defaultModel: draft.defaultModel.trim(),
          // Blank tiers are left out; the API drops them anyway.
          tierModels: Object.fromEntries(tiers.filter(t => draft.tierModels[t].trim()).map(t => [t, draft.tierModels[t].trim()])),
          nativeStructuredOutput: draft.nativeStructuredOutput,
          maxConcurrency: Number(draft.maxConcurrency) || 4,
          // A shared endpoint cannot use your key: everyone would be spending it.
          credentialId: draft.kind === 'openai' && !draft.shared ? draft.credentialId || null : null,
          shared: admin && draft.shared,
        },
      });
    },
    onSuccess: () => {
      setDraft(emptyDraft);
      queryClient.invalidateQueries({ queryKey: ['endpoints'] });
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    add.mutate();
  };

  return (
    <details className="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
      <summary className="cursor-pointer text-sm font-semibold">Add an endpoint</summary>
      <form onSubmit={submit} className="mt-4 space-y-4">
        <div className="grid gap-4 sm:grid-cols-[1fr_10rem]">
          <Label text="Name">
            <input id="endpoint-name" className={field} maxLength={80} placeholder="e.g. My OpenAI" value={draft.name} onChange={e => set({ name: e.target.value })} />
          </Label>
          <Label text="Kind">
            <select id="endpoint-kind" className={field} value={draft.kind} onChange={e => set({ kind: e.target.value as Draft['kind'] })}>
              <option value="openai">OpenAI-compatible</option>
              <option value="fake">Fake (offline)</option>
            </select>
          </Label>
        </div>

        {draft.kind === 'openai' ? (
          <div className="grid gap-4 sm:grid-cols-2">
            <Label text="Base URL" hint="Blank for OpenAI itself; otherwise any OpenAI-compatible server (Azure AI Foundry, Ollama, LM Studio…).">
              <input id="endpoint-base-url" className={field} placeholder="https://api.openai.com/v1" value={draft.baseUrl} onChange={e => set({ baseUrl: e.target.value })} />
            </Label>
            {!draft.shared && (
              <Label text="Key" hint={keys.length === 0 ? 'Add an API key above first, or leave blank for a server that needs none.' : 'One of your API keys.'}>
                <select id="endpoint-key" className={field} value={draft.credentialId} onChange={e => set({ credentialId: e.target.value })}>
                  <option value="">No key</option>
                  {keys.map(k => (
                    <option key={k.id} value={k.id}>
                      {k.label} ({k.hint})
                    </option>
                  ))}
                </select>
              </Label>
            )}
          </div>
        ) : (
          <p className="text-xs text-slate-500">
            The fake endpoint answers from canned heuristics with no network. Models: <code>fake</code>, <code>fake-flaky</code> (fails now and
            then, to see retries) and <code>fake-slow</code> (to watch the stepper).
          </p>
        )}

        <div className="grid gap-4 sm:grid-cols-4">
          <Label text="Default model">
            <input id="endpoint-default-model" className={field} list="fake-models" value={draft.defaultModel} onChange={e => set({ defaultModel: e.target.value })} />
          </Label>
          {tiers.map(t => (
            <Label key={t} text={`${t[0].toUpperCase()}${t.slice(1)} tier`} hint="Optional">
              <input
                id={`endpoint-tier-${t}`}
                className={field}
                value={draft.tierModels[t]}
                onChange={e => set({ tierModels: { ...draft.tierModels, [t]: e.target.value } })}
              />
            </Label>
          ))}
          {draft.kind === 'fake' && (
            <datalist id="fake-models">
              <option value="fake" />
              <option value="fake-flaky" />
              <option value="fake-slow" />
            </datalist>
          )}
        </div>

        <div className="grid gap-4 sm:grid-cols-[1fr_8rem]">
          <fieldset className="space-y-1 text-sm">
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={draft.nativeStructuredOutput} onChange={e => set({ nativeStructuredOutput: e.target.checked })} />
              Native structured output <span className="text-xs text-slate-500">(off: JSON is asked for in the prompt, with one repair)</span>
            </label>
            {admin && (
              <label className="flex items-center gap-2">
                <input id="endpoint-shared" type="checkbox" checked={draft.shared} onChange={e => set({ shared: e.target.checked })} />
                Shared with everyone <span className="text-xs text-slate-500">(admins only; cannot use your own key)</span>
              </label>
            )}
          </fieldset>
          <Label text="At once" hint="Max parallel calls">
            <input className={field} type="number" min={1} max={64} value={draft.maxConcurrency} onChange={e => set({ maxConcurrency: e.target.value })} />
          </Label>
        </div>

        <ErrorNote error={add.error} />
        <div className="flex justify-end">
          <Button type="submit" disabled={add.isPending || !draft.name.trim() || !draft.defaultModel.trim()}>
            {add.isPending ? 'Saving…' : 'Add endpoint'}
          </Button>
        </div>
      </form>
    </details>
  );
}
