import { useState, type FormEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { useCredentials } from '../../api/queries';
import type { Credential } from '../../api/types';
import { Badge, Button, Card, ConfirmButton, Empty, ErrorNote, field, Label, Spinner, timeAgo } from '../ui';

type Kind = Credential['kind'];

const kindLabels: Record<Kind, string> = { 'api-key': 'API key', 'github-token': 'GitHub token' };

/**
 * Your stored keys (design doc section 10).
 *
 * Write-only: a secret goes in once, the API seals it, and from then on you only ever see its label and a masked
 * hint. There is no "show" and no edit; to replace a key, add the new one and delete the old. Nobody else can list
 * yours, admins included.
 *
 * API keys are what your own endpoints call a model with (bring your own key). GitHub tokens are for slice 2, when
 * agents can run on your own Copilot seat; the API already refuses the classic `ghp_` kind that Copilot rejects.
 */
export function CredentialsCard() {
  const queryClient = useQueryClient();
  const credentials = useCredentials();

  const [kind, setKind] = useState<Kind>('api-key');
  const [label, setLabel] = useState('');
  const [secret, setSecret] = useState('');

  // Endpoints show the hint of the key they use, so both lists change when a key comes or goes.
  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['credentials'] });
    queryClient.invalidateQueries({ queryKey: ['endpoints'] });
  };

  const add = useMutation({
    mutationFn: () => api<Credential>('/api/credentials', { method: 'POST', body: { kind, label: label.trim(), secret: secret.trim() } }),
    onSuccess: () => {
      // Drop the secret from the page as soon as the server has it.
      setLabel('');
      setSecret('');
      refresh();
    },
  });

  const remove = useMutation({
    mutationFn: (id: string) => api(`/api/credentials/${id}`, { method: 'DELETE' }),
    onSuccess: refresh,
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    add.mutate();
  };

  return (
    <Card title="Keys">
      <div className="space-y-5">
        {credentials.isPending ? (
          <Spinner />
        ) : credentials.error ? (
          <ErrorNote error={credentials.error} />
        ) : credentials.data.length === 0 ? (
          <Empty>No keys yet. Add one to use your own model endpoint.</Empty>
        ) : (
          <ul className="divide-y divide-slate-100 dark:divide-slate-800" data-testid="credentials">
            {credentials.data.map(c => (
              <li key={c.id} className="flex items-center gap-3 py-2 text-sm">
                <Badge tone={c.kind === 'api-key' ? 'indigo' : 'slate'}>{kindLabels[c.kind]}</Badge>
                <span className="font-medium">{c.label}</span>
                <code className="text-xs text-slate-500">{c.hint}</code>
                <span className="ml-auto text-xs text-slate-500">
                  added {timeAgo(c.createdAt)}
                  {c.lastUsedAt ? `, last used ${timeAgo(c.lastUsedAt)}` : ', not used yet'}
                </span>
                <ConfirmButton label="Delete" confirmLabel="Really delete?" disabled={remove.isPending} onConfirm={() => remove.mutate(c.id)} />
              </li>
            ))}
          </ul>
        )}
        <ErrorNote error={remove.error} />

        <form onSubmit={submit} className="space-y-4 rounded-lg border border-slate-200 p-4 dark:border-slate-800">
          <h3 className="text-sm font-semibold">Add a key</h3>
          <div className="grid gap-4 sm:grid-cols-[10rem_1fr]">
            <Label text="Kind">
              <select id="credential-kind" className={field} value={kind} onChange={e => setKind(e.target.value as Kind)}>
                <option value="api-key">API key</option>
                <option value="github-token">GitHub token</option>
              </select>
            </Label>
            <Label text="Label" hint="So you can tell your keys apart.">
              <input id="credential-label" className={field} maxLength={80} placeholder="e.g. my OpenAI key" value={label} onChange={e => setLabel(e.target.value)} />
            </Label>
          </div>
          <Label
            text={kind === 'api-key' ? 'Key' : 'Token'}
            hint={
              kind === 'api-key'
                ? 'Stored encrypted. You will not be able to see it again.'
                : 'A fine-grained token (github_pat_) or an OAuth or GitHub App user token (gho_, ghu_). Classic ghp_ tokens do not work with Copilot.'
            }
          >
            {/* A password field, so the key is not shown on screen or offered back by autocomplete. */}
            <input id="credential-secret" type="password" autoComplete="off" className={field} value={secret} onChange={e => setSecret(e.target.value)} />
          </Label>
          <ErrorNote error={add.error} />
          <div className="flex justify-end">
            <Button type="submit" disabled={add.isPending || !label.trim() || !secret.trim()}>
              {add.isPending ? 'Saving…' : 'Add key'}
            </Button>
          </div>
        </form>
      </div>
    </Card>
  );
}
