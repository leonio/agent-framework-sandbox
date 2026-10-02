import { Link } from 'react-router';
import { useAssignments } from '../api/queries';
import { useMe } from '../auth';
import { AssignmentStateBadge } from '../components/status';
import { Card, Empty, ErrorNote, Spinner, timeAgo } from '../components/ui';

/** Your assignments, newest first, with how many findings still wait for a decision. */
export function AssignmentsPage() {
  const assignments = useAssignments();
  const me = useMe();

  return (
    <div className="space-y-6">
      <div className="flex items-end justify-between">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Assignments</h1>
          <p className="text-sm text-slate-500">Work you gave to the agents. Open one to triage its findings or hold its retro.</p>
        </div>
        <Link to="/assignments/new" className="rounded-lg bg-indigo-600 px-3 py-2 text-sm font-medium text-white hover:bg-indigo-500">
          New assignment
        </Link>
      </div>

      {!me.title && (
        <p className="rounded-lg bg-indigo-50 px-4 py-2 text-sm text-indigo-800 dark:bg-indigo-950 dark:text-indigo-200">
          Add your title in <Link to="/settings" className="font-medium underline">settings</Link>: it shows next to your name
          on every decision and card, so feedback can be weighed by who gave it.
        </p>
      )}

      <Card>
        {assignments.isPending && <Spinner />}
        <ErrorNote error={assignments.error} />
        {assignments.data?.length === 0 && <Empty>No assignments yet. Start one to see the reviewers at work.</Empty>}
        {assignments.data && assignments.data.length > 0 && (
          <ul className="-my-2 divide-y divide-slate-100 dark:divide-slate-800">
            {assignments.data.map(a => (
              <li key={a.id}>
                <Link to={`/assignments/${a.id}`} className="flex items-center gap-4 rounded-lg px-2 py-3 hover:bg-slate-50 dark:hover:bg-slate-800/50">
                  <div className="min-w-0 flex-1">
                    <p className="truncate font-medium">{a.title}</p>
                    <p className="truncate text-xs text-slate-500">
                      {a.scenario} · {a.source} · {timeAgo(a.createdAt)}
                    </p>
                  </div>
                  <span className="text-xs text-slate-500">
                    {a.findings} finding{a.findings === 1 ? '' : 's'}
                    {a.undecided > 0 && <span className="text-amber-600">, {a.undecided} to decide</span>}
                  </span>
                  <AssignmentStateBadge state={a.state} />
                </Link>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}
