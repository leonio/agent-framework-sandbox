import type { PhaseStep } from "../lib/api";

const dot: Record<PhaseStep["state"], string> = {
  done: "bg-emerald-500 text-white",
  active: "bg-sky-500 text-white animate-pulse",
  waiting: "bg-amber-400 text-white",
  failed: "bg-rose-500 text-white",
  upcoming: "bg-slate-200 text-slate-500",
};

/** The workflow screen's guide: where the run is, what is done, and where a human is needed. */
export function Stepper({ steps }: { steps: PhaseStep[] }) {
  return (
    <ol className="flex flex-wrap items-center gap-y-2">
      {steps.map((s, i) => (
        <li key={s.phase} className="flex items-center">
          <div className="flex items-center gap-2">
            <span className={`flex h-7 w-7 items-center justify-center rounded-full text-xs font-semibold ${dot[s.state]}`}>
              {s.state === "done" ? "✓" : i + 1}
            </span>
            <span className={`text-sm ${s.state === "upcoming" ? "text-slate-400" : "font-medium text-slate-800"}`}>
              {s.name}
              {s.state === "waiting" && <span className="ml-1 text-xs text-amber-600">(needs you)</span>}
            </span>
          </div>
          {i < steps.length - 1 && <span className="mx-3 h-px w-6 bg-slate-300" />}
        </li>
      ))}
    </ol>
  );
}
