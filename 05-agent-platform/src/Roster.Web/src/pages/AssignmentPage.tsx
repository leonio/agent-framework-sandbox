import { useState } from 'react';
import { useParams } from 'react-router';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../api/client';
import { useAssignment, useLiveEvents } from '../api/queries';
import { useMe } from '../auth';
import { FindingsTriage } from '../components/FindingsTriage';
import { LedgerView } from '../components/LedgerView';
import { RetroPanel } from '../components/RetroPanel';
import { PhaseStepper } from '../components/PhaseStepper';
import { AssignmentStateBadge, OutcomeBadge } from '../components/status';
import { Button, Card, ErrorNote, Spinner, timeAgo } from '../components/ui';

type Tab = 'findings' | 'retro' | 'ledger';

/**
 * One assignment, live: its phases, what each reviewer is doing, the findings to triage, and the ledger of every
 * agent call. Server-sent events keep it current without reloading.
 */
export function AssignmentPage() {
  const { id = '' } = useParams();
  const me = useMe();
  const queryClient = useQueryClient();
  const detail = useAssignment(id);
  const live = useLiveEvents(id);
  const [tab, setTab] = useState<Tab>('findings');

  const cancel = useMutation({
    mutationFn: () => api(`/api/assignments/${id}/cancel`, { method: 'POST' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['assignment', id] }),
  });

  if (detail.isPending) return <Spinner />;
  if (detail.error) return <ErrorNote error={detail.error} />;

  const d = detail.data;
  const a = d.assignment;
  const owner = a.ownerId === me.id;
  const active = a.state === 'queued' || a.state === 'running' || a.state === 'awaitingTriage';
  const undecided = d.findings.filter(f => f.decision === 'pending').length;

  // What each reviewer is doing right now: its latest ledger row in the review phase.
  const latestReview = new Map(d.steps.filter(s => s.phase === 'review').map(s => [s.agent, s]));

  // The agents a retro card can be about: the ones that worked on this assignment (not the facilitator itself).
  const agentsInvolved = [...new Set(d.steps.filter(s => s.phase !== 'retro').map(s => s.agent))];

  const tabs: [Tab, string][] = [
    ['findings', `Findings${undecided > 0 ? ` (${undecided} to decide)` : ''}`],
    ['retro', 'Retro'],
    ['ledger', `Ledger (${d.steps.length})`],
  ];

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start gap-4">
        <div className="min-w-0 flex-1">
          <h1 className="text-2xl font-semibold tracking-tight">{a.title}</h1>
          <p className="text-sm text-slate-500">
            {a.scenario} · {a.source} · started {timeAgo(a.createdAt)}
          </p>
        </div>
        <span className={`flex items-center gap-1.5 text-xs ${live ? 'text-emerald-600' : 'text-slate-400'}`} title="Updates arrive as they happen">
          <span className={`h-2 w-2 rounded-full ${live ? 'bg-emerald-500' : 'bg-slate-300'}`} />
          {live ? 'Live' : 'Reconnecting'}
        </span>
        <AssignmentStateBadge state={a.state} />
        {owner && active && !d.cancelRequested && (
          <Button variant="secondary" onClick={() => cancel.mutate()} disabled={cancel.isPending}>
            Cancel
          </Button>
        )}
      </div>
      <ErrorNote error={cancel.error} />
      {d.error && <ErrorNote error={d.error} />}

      <Card>
        <PhaseStepper phases={d.phases} />
        {latestReview.size > 0 && (
          <div className="mt-6 flex flex-wrap gap-3 border-t border-slate-100 pt-4 dark:border-slate-800">
            {[...latestReview.values()].map(s => (
              <span key={s.agent} className="flex items-center gap-2 rounded-lg bg-slate-50 px-3 py-1.5 text-sm dark:bg-slate-800/60" data-testid="reviewer">
                {s.agent} <OutcomeBadge outcome={s.outcome} />
                {s.durationMs !== null && <span className="text-xs text-slate-500">{(s.durationMs / 1000).toFixed(1)} s</span>}
              </span>
            ))}
          </div>
        )}
      </Card>

      <div className="border-b border-slate-200 dark:border-slate-800">
        <nav className="-mb-px flex gap-6">
          {tabs.map(([key, label]) => (
            <button
              key={key}
              onClick={() => setTab(key)}
              className={`border-b-2 pb-2 text-sm font-medium ${tab === key ? 'border-indigo-600 text-indigo-600' : 'border-transparent text-slate-500 hover:text-slate-700'}`}
            >
              {label}
            </button>
          ))}
        </nav>
      </div>

      {tab === 'findings' && <FindingsTriage detail={d} canDecide={owner} />}
      {tab === 'retro' && <RetroPanel assignmentId={id} isOwner={owner} agents={agentsInvolved} />}
      {tab === 'ledger' && <LedgerView assignmentId={id} steps={d.steps} />}
    </div>
  );
}
