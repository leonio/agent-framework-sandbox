import { useEffect, useState } from "react";
import { NavLink, Outlet } from "react-router";
import { api, type Meta } from "../lib/api";

const links = [
  { to: "/", label: "Workflows", end: true },
  { to: "/new", label: "New app idea" },
  { to: "/import", label: "Import existing app" },
  { to: "/admin", label: "Admin: skills & prompts" },
];

export function Layout() {
  const [meta, setMeta] = useState<Meta>();
  useEffect(() => void api.meta().then(setMeta).catch(() => undefined), []);

  return (
    <div className="flex min-h-screen">
      <aside className="w-60 shrink-0 border-r border-slate-200 bg-white">
        <div className="px-5 py-4">
          <div className="text-lg font-bold text-indigo-700">SDLC Studio</div>
          <div className="text-xs text-slate-500">Microsoft Agent Framework sample</div>
        </div>
        <nav className="flex flex-col gap-0.5 px-3">
          {links.map((l) => (
            <NavLink
              key={l.to}
              to={l.to}
              end={l.end}
              className={({ isActive }) =>
                `rounded-md px-3 py-2 text-sm ${isActive ? "bg-indigo-50 font-medium text-indigo-700" : "text-slate-600 hover:bg-slate-50"}`
              }
            >
              {l.label}
            </NavLink>
          ))}
        </nav>
        {meta && (
          <div className="mx-3 mt-6 rounded-md bg-slate-50 p-3 text-xs text-slate-600">
            <div>
              Model: <b>{meta.llmProvider}</b> ({meta.model})
            </div>
            <div>Integrations: {meta.integrationsMode}</div>
            <div>Database: {meta.database}</div>
          </div>
        )}
      </aside>
      <main className="min-w-0 flex-1 p-6">
        <Outlet />
      </main>
    </div>
  );
}
