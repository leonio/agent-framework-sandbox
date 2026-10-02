import type { AssignmentDetail, PhaseState } from '../api/types';

/**
 * The scenario's phases as a stepper: fetch → review → triage. Each step shows its state, its attempt if it had to be
 * retried, and its error if it failed. It updates live with the assignment's events.
 */

const labels: Record<string, string> = { fetch: 'Fetch the pull request', review: 'Review', triage: 'Your triage' };

const dot: Record<PhaseState, string> = {
  pending: 'border-slate-300 bg-white text-slate-400 dark:border-slate-700 dark:bg-slate-900',
  queued: 'border-slate-400 bg-white text-slate-500 dark:bg-slate-900',
  running: 'border-indigo-500 bg-indigo-50 text-indigo-600 animate-pulse dark:bg-indigo-950',
  succeeded: 'border-emerald-500 bg-emerald-500 text-white',
  failed: 'border-rose-500 bg-rose-500 text-white',
  cancelled: 'border-slate-400 bg-slate-200 text-slate-500 dark:bg-slate-800',
};

const symbol: Record<PhaseState, string> = { pending: '', queued: '…', running: '●', succeeded: '✓', failed: '!', cancelled: '–' };

export function PhaseStepper({ phases }: { phases: AssignmentDetail['phases'] }) {
  return (
    <ol className="flex flex-col gap-4 sm:flex-row sm:items-start sm:gap-0">
      {phases.map((phase, i) => (
        <li key={phase.key} className="flex flex-1 items-start gap-3 sm:flex-col sm:items-center sm:text-center">
          <div className="flex w-full items-center sm:justify-center">
            <span className={`hidden h-0.5 flex-1 sm:block ${i === 0 ? 'invisible' : 'bg-slate-200 dark:bg-slate-800'}`} />
            <span className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-full border-2 text-sm font-bold ${dot[phase.state]}`} data-testid={`phase-${phase.key}`} data-state={phase.state}>
              {symbol[phase.state]}
            </span>
            <span className={`hidden h-0.5 flex-1 sm:block ${i === phases.length - 1 ? 'invisible' : 'bg-slate-200 dark:bg-slate-800'}`} />
          </div>
          <div className="sm:mt-2">
            <p className="text-sm font-medium">{labels[phase.key] ?? phase.key}</p>
            <p className="text-xs text-slate-500">
              {phase.state}
              {phase.attempt > 1 && ` · attempt ${phase.attempt}`}
            </p>
            {phase.error && phase.state === 'failed' && <p className="mt-1 max-w-xs text-xs text-rose-600">{phase.error}</p>}
          </div>
        </li>
      ))}
    </ol>
  );
}
