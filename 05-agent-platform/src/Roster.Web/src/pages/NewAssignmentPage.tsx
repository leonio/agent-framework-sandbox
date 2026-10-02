import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../api/client';
import { useAgents, useEndpoints } from '../api/queries';
import type { AssignmentSummary } from '../api/types';
import { EndpointSelect, StructuredOutputNote } from '../components/EndpointSelect';
import { Button, Card, ErrorNote, field, Label, Spinner } from '../components/ui';

type Choice = { endpointId: string; model: string };

/**
 * Starting an assignment: what to review, and which model endpoint does the work.
 *
 * The endpoint choice is the picker from the design doc: one endpoint (and optionally a model) for the whole
 * assignment, and an "advanced" panel to give the review phase, or a single reviewer, a different one. Blank means
 * "inherit": a reviewer falls back to the phase, the phase to the assignment, the assignment to your default endpoint
 * and then the shared one.
 */
export function NewAssignmentPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const endpoints = useEndpoints();
  const agents = useAgents();

  const [sourceKind, setSourceKind] = useState<'fixture' | 'github'>('fixture');
  const [url, setUrl] = useState('');
  const [title, setTitle] = useState('');
  const [assignment, setAssignment] = useState<Choice>({ endpointId: '', model: '' });
  const [phase, setPhase] = useState<Choice>({ endpointId: '', model: '' });
  const [steps, setSteps] = useState<Record<string, Choice>>({});

  const reviewers = agents.data?.filter(a => a.input === 'ChangeReviewInput') ?? [];
  const chosen = endpoints.data?.find(e => e.id === assignment.endpointId);

  const create = useMutation({
    mutationFn: () => {
      const pick = (c: Choice) => ({ endpointId: c.endpointId || null, model: c.model.trim() || null });
      const stepOverrides = Object.fromEntries(
        Object.entries(steps)
          .filter(([, c]) => c.endpointId || c.model.trim())
          .map(([agent, c]) => [`review/${agent}`, pick(c)]),
      );
      return api<AssignmentSummary>('/api/assignments', {
        method: 'POST',
        body: {
          source: sourceKind === 'fixture' ? 'fixture:sample-pr' : url.trim(),
          title: title.trim() || null,
          endpointId: assignment.endpointId || null,
          model: assignment.model.trim() || null,
          overrides: {
            phases: phase.endpointId || phase.model.trim() ? { review: pick(phase) } : null,
            steps: Object.keys(stepOverrides).length > 0 ? stepOverrides : null,
          },
        },
      });
    },
    onSuccess: created => {
      queryClient.invalidateQueries({ queryKey: ['assignments'] });
      navigate(`/assignments/${created.id}`);
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    create.mutate();
  };

  if (endpoints.isPending || agents.isPending) return <Spinner />;

  return (
    <form onSubmit={submit} className="mx-auto max-w-3xl space-y-6">
      <h1 className="text-2xl font-semibold tracking-tight">New assignment</h1>

      <Card title="What to review">
        <div className="space-y-4">
          <fieldset className="space-y-2">
            <label className="flex items-start gap-2 text-sm">
              <input type="radio" name="source" checked={sourceKind === 'fixture'} onChange={() => setSourceKind('fixture')} className="mt-1" />
              <span>
                <span className="font-medium">The sample pull request</span>
                <span className="block text-slate-500">A payments endpoint with deliberate problems, bundled with Roster. Works offline.</span>
              </span>
            </label>
            <label className="flex items-start gap-2 text-sm">
              <input type="radio" name="source" checked={sourceKind === 'github'} onChange={() => setSourceKind('github')} className="mt-1" />
              <span className="flex-1">
                <span className="font-medium">A GitHub pull request</span>
                <input
                  className={`${field} mt-1`}
                  placeholder="https://github.com/owner/repo/pull/123"
                  value={url}
                  onChange={e => setUrl(e.target.value)}
                  onFocus={() => setSourceKind('github')}
                />
              </span>
            </label>
          </fieldset>
          <Label text="Title" hint="Optional. Defaults to the pull request.">
            <input className={field} value={title} onChange={e => setTitle(e.target.value)} />
          </Label>
        </div>
      </Card>

      <Card title="Who does the work">
        <div className="grid gap-4 sm:grid-cols-2">
          <Label text="Model endpoint">
            <EndpointSelect
              id="endpoint"
              endpoints={endpoints.data ?? []}
              value={assignment.endpointId}
              onChange={endpointId => setAssignment({ ...assignment, endpointId })}
              inheritLabel="Your default (or the shared one)"
            />
          </Label>
          <Label text="Model" hint={chosen ? `Blank: each agent's tier on ${chosen.name}, else ${chosen.defaultModel}.` : 'Blank: each agent\'s tier on the endpoint.'}>
            <input className={field} value={assignment.model} onChange={e => setAssignment({ ...assignment, model: e.target.value })} />
          </Label>
        </div>
        <div className="mt-2">
          <StructuredOutputNote endpoint={chosen} />
        </div>

        <details className="mt-5 rounded-lg border border-slate-200 p-4 dark:border-slate-800">
          <summary className="cursor-pointer text-sm font-medium">Advanced: a different endpoint for the review phase or one reviewer</summary>
          <div className="mt-4 space-y-4">
            <OverrideRow label="Review phase" endpoints={endpoints.data ?? []} value={phase} onChange={setPhase} inheritLabel="Inherit from the assignment" />
            {reviewers.map(r => (
              <OverrideRow
                key={r.name}
                label={r.name}
                endpoints={endpoints.data ?? []}
                value={steps[r.name] ?? { endpointId: '', model: '' }}
                onChange={c => setSteps({ ...steps, [r.name]: c })}
                inheritLabel="Inherit from the phase"
              />
            ))}
          </div>
        </details>
      </Card>

      <ErrorNote error={create.error} />
      <div className="flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={() => navigate('/')}>
          Cancel
        </Button>
        <Button type="submit" disabled={create.isPending || (sourceKind === 'github' && !url.trim())}>
          {create.isPending ? 'Starting…' : 'Start the review'}
        </Button>
      </div>
    </form>
  );
}

function OverrideRow({ label, endpoints, value, onChange, inheritLabel }: {
  label: string;
  endpoints: Parameters<typeof EndpointSelect>[0]['endpoints'];
  value: Choice;
  onChange: (c: Choice) => void;
  inheritLabel: string;
}) {
  return (
    <div className="grid items-center gap-2 sm:grid-cols-[12rem_1fr_10rem]">
      <span className="text-sm font-medium">{label}</span>
      <EndpointSelect endpoints={endpoints} value={value.endpointId} onChange={endpointId => onChange({ ...value, endpointId })} inheritLabel={inheritLabel} />
      <input className={field} placeholder="model (optional)" value={value.model} onChange={e => onChange({ ...value, model: e.target.value })} />
    </div>
  );
}
