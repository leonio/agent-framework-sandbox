import { useState, type FormEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../../api/client';
import { useEndpoints } from '../../api/queries';
import { nameAndTitle, useMe } from '../../auth';
import { EndpointSelect } from '../EndpointSelect';
import { Button, Card, ErrorNote, field, Label } from '../ui';

/**
 * Your profile: the free-text title shown next to your name (on triage decisions, retro cards, and in the scorecards'
 * breakdown by title), and the endpoint your assignments use when you do not pick one.
 *
 * Your name and email come from Keycloak and are changed there; Roster copies them on each sign-in.
 */
export function ProfileCard() {
  const me = useMe();
  const queryClient = useQueryClient();
  const endpoints = useEndpoints();

  // The form starts from the profile as it is; the page only renders once you are signed in, so `me` is there.
  const [title, setTitle] = useState(me.title ?? '');
  const [defaultEndpointId, setDefaultEndpointId] = useState(me.defaultEndpointId ?? '');

  const save = useMutation({
    mutationFn: () =>
      api('/api/me/profile', {
        method: 'PUT',
        body: { title: title.trim() || null, defaultEndpointId: defaultEndpointId || null },
      }),
    // The top bar and the pickers read `me`, so refetch it rather than patching it by hand.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['me'] }),
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  const unchanged = title.trim() === (me.title ?? '') && defaultEndpointId === (me.defaultEndpointId ?? '');

  return (
    <Card title="Profile">
      <form onSubmit={submit} className="space-y-4">
        <p className="text-sm text-slate-600 dark:text-slate-400">
          Signed in as <span className="font-medium text-slate-900 dark:text-white">{me.name}</span>
          {me.email && <> ({me.email})</>}. Your name and email come from your Keycloak account.
        </p>
        <div className="grid gap-4 sm:grid-cols-2">
          <Label text="Title" hint={`How you appear: "${nameAndTitle(me.name, title.trim() || null)}".`}>
            <input id="title" className={field} maxLength={80} placeholder="e.g. Security Engineer" value={title} onChange={e => setTitle(e.target.value)} />
          </Label>
          <Label text="Default endpoint" hint="Used by your assignments when you do not pick one.">
            <EndpointSelect
              id="default-endpoint"
              endpoints={endpoints.data ?? []}
              value={defaultEndpointId}
              onChange={setDefaultEndpointId}
              inheritLabel="None: the shared default"
            />
          </Label>
        </div>
        <ErrorNote error={save.error} />
        <div className="flex items-center justify-end gap-3">
          {save.isSuccess && unchanged && <span className="text-sm text-emerald-700 dark:text-emerald-300">Saved.</span>}
          <Button type="submit" disabled={save.isPending || unchanged}>
            {save.isPending ? 'Saving…' : 'Save profile'}
          </Button>
        </div>
      </form>
    </Card>
  );
}
