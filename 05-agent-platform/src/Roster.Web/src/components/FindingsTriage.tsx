import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '../api/client';
import type { AssignmentDetail, Finding } from '../api/types';
import { DecisionBadge, SeverityBadge, shortHash } from './status';
import { Button, Empty, ErrorNote, field } from './ui';

/**
 * Triage: the findings, grouped by the reviewer that raised them, each with Accept and Reject.
 *
 * Rejecting asks why. That reason is the most useful thing the person gives the system: the retro asks about it and
 * the scorecards learn from it, so the button stays disabled until there is one. Decisions can be changed, and each
 * shows who made it as "Name · Title".
 */
export function FindingsTriage({ detail, canDecide }: { detail: AssignmentDetail; canDecide: boolean }) {
  const reviewing = detail.phases.some(p => p.key === 'review' && (p.state === 'running' || p.state === 'queued' || p.state === 'pending'));
  if (detail.findings.length === 0) {
    return <Empty>{reviewing ? 'The reviewers are working. Findings appear here as each one finishes.' : 'No findings: the reviewers found nothing material.'}</Empty>;
  }

  const byAgent = Map.groupBy(detail.findings, f => f.agent);
  const open = detail.assignment.state === 'awaitingTriage' || detail.assignment.state === 'completed';

  return (
    <div className="space-y-8">
      {[...byAgent.entries()].map(([agent, findings]) => (
        <section key={agent}>
          <h3 className="mb-3 flex items-center gap-2 text-sm font-semibold">
            {agent}
            <span className="font-mono text-xs font-normal text-slate-400">{shortHash(findings[0].agentHash)}</span>
            <span className="text-xs font-normal text-slate-500">· {findings.length} finding{findings.length === 1 ? '' : 's'}</span>
          </h3>
          <div className="space-y-3">
            {findings.map(f => (
              <FindingCard key={f.id} finding={f} assignmentId={detail.assignment.id} canDecide={canDecide && open} />
            ))}
          </div>
        </section>
      ))}
    </div>
  );
}

function FindingCard({ finding, assignmentId, canDecide }: { finding: Finding; assignmentId: string; canDecide: boolean }) {
  const queryClient = useQueryClient();
  const [mode, setMode] = useState<'idle' | 'rejecting' | 'accepting'>('idle');
  const [reason, setReason] = useState('');

  const decide = useMutation({
    mutationFn: (decision: 'accepted' | 'rejected') =>
      api<Finding>(`/api/findings/${finding.id}/decision`, { method: 'POST', body: { decision, reason: reason.trim() || null } }),
    onSuccess: () => {
      setMode('idle');
      setReason('');
      queryClient.invalidateQueries({ queryKey: ['assignment', assignmentId] });
    },
  });

  const decided = finding.decision !== 'pending';

  return (
    <article className="rounded-lg border border-slate-200 p-4 dark:border-slate-800" data-testid="finding" data-decision={finding.decision}>
      <header className="flex flex-wrap items-center gap-2">
        <SeverityBadge severity={finding.severity} />
        <h4 className="font-medium">{finding.title}</h4>
        <span className="ml-auto flex items-center gap-2 text-xs text-slate-500">
          {Math.round(finding.confidence * 100)}% sure
          <DecisionBadge decision={finding.decision} />
        </span>
      </header>
      {finding.filePath && <p className="mt-1 font-mono text-xs text-slate-500">{finding.filePath}</p>}
      <p className="mt-2 text-sm text-slate-700 dark:text-slate-300">{finding.detail}</p>
      <p className="mt-2 text-sm">
        <span className="font-medium">Suggestion: </span>
        {finding.recommendation}
      </p>

      {decided && mode === 'idle' && (
        <p className="mt-3 text-sm text-slate-600 dark:text-slate-400">
          {finding.decision === 'rejected' ? 'Rejected' : 'Accepted'} by {finding.decidedBy}
          {finding.reason && <>: “{finding.reason}”</>}
          {canDecide && (
            <button className="ml-2 text-indigo-600 hover:underline" onClick={() => setMode(finding.decision === 'rejected' ? 'accepting' : 'rejecting')}>
              change
            </button>
          )}
        </p>
      )}

      {canDecide && !decided && mode === 'idle' && (
        <div className="mt-3 flex gap-2">
          <Button onClick={() => decide.mutate('accepted')} disabled={decide.isPending}>
            Accept
          </Button>
          <Button variant="secondary" onClick={() => setMode('rejecting')}>
            Reject…
          </Button>
        </div>
      )}

      {mode !== 'idle' && (
        <div className="mt-3 space-y-2">
          <textarea
            className={field}
            rows={2}
            autoFocus
            placeholder={mode === 'rejecting' ? 'Why is it wrong, or not worth doing? (required)' : 'Anything worth saying about it? (optional)'}
            value={reason}
            onChange={e => setReason(e.target.value)}
          />
          <div className="flex gap-2">
            {mode === 'rejecting' ? (
              <Button variant="danger" disabled={!reason.trim() || decide.isPending} onClick={() => decide.mutate('rejected')}>
                Reject with this reason
              </Button>
            ) : (
              <Button disabled={decide.isPending} onClick={() => decide.mutate('accepted')}>
                Accept
              </Button>
            )}
            <Button variant="ghost" onClick={() => setMode('idle')}>
              Never mind
            </Button>
          </div>
        </div>
      )}
      <div className="mt-2">
        <ErrorNote error={decide.error} />
      </div>
    </article>
  );
}
