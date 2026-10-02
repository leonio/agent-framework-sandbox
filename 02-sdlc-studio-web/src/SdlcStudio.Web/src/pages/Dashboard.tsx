import { Link } from "react-router";
import { api } from "../lib/api";
import { useLiveData } from "../lib/hooks";
import { Button, Card, ErrorText, Pill, StatusBadge, timeAgo } from "../components/ui";

/** Every workflow run. Several can run at once; the background worker processes them in parallel. */
export function Dashboard() {
  const { data: runs, error } = useLiveData(api.runs, "/api/events");

  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">Workflows</h1>
        <div className="flex gap-2">
          <Link to="/import"><Button variant="secondary">Import existing app</Button></Link>
          <Link to="/new"><Button>New app idea</Button></Link>
        </div>
      </div>
      <ErrorText>{error}</ErrorText>
      <Card>
        {!runs ? (
          <p className="text-sm text-slate-500">Loading...</p>
        ) : runs.length === 0 ? (
          <p className="text-sm text-slate-500">
            No workflows yet. Start with a <Link className="text-indigo-600 underline" to="/new">new app idea</Link> or{" "}
            <Link className="text-indigo-600 underline" to="/import">import an existing app</Link>.
          </p>
        ) : (
          <table className="w-full text-sm">
            <thead className="text-left text-xs uppercase text-slate-500">
              <tr><th className="pb-2">Name</th><th>Source</th><th>Phase</th><th>Status</th><th>Updated</th></tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {runs.map((r) => (
                <tr key={r.id} className="hover:bg-slate-50">
                  <td className="py-2.5">
                    <Link to={`/runs/${r.id}`} className="font-medium text-indigo-700 hover:underline">{r.name}</Link>
                    <div className="max-w-md truncate text-xs text-slate-500">{r.statusMessage}</div>
                  </td>
                  <td><Pill tone={r.source === "ExistingApp" ? "amber" : "indigo"}>{r.source === "ExistingApp" ? "Existing app" : "New idea"}</Pill></td>
                  <td>{r.phaseName}</td>
                  <td><StatusBadge status={r.status} /></td>
                  <td className="text-slate-500">{timeAgo(r.updatedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </div>
  );
}
