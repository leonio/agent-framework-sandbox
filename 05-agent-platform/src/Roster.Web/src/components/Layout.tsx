import { NavLink, Outlet } from 'react-router';
import { nameAndTitle, useMe } from '../auth';

/** The frame around every signed-in page: the top bar with navigation, who you are, and signing out. */
export function Layout() {
  const me = useMe();
  const link = ({ isActive }: { isActive: boolean }) =>
    `rounded-lg px-3 py-1.5 text-sm font-medium ${isActive ? 'bg-slate-100 text-slate-900 dark:bg-slate-800 dark:text-white' : 'text-slate-600 hover:text-slate-900 dark:text-slate-400 dark:hover:text-white'}`;

  return (
    <div className="min-h-screen">
      <header className="border-b border-slate-200 bg-white/80 backdrop-blur dark:border-slate-800 dark:bg-slate-900/80">
        <div className="mx-auto flex max-w-6xl items-center gap-6 px-4 py-3">
          <NavLink to="/" className="text-lg font-semibold tracking-tight">
            Roster
          </NavLink>
          <nav className="flex gap-1">
            <NavLink to="/" end className={link}>
              Assignments
            </NavLink>
            <NavLink to="/agents" className={link}>
              Agents
            </NavLink>
            <NavLink to="/settings" className={link}>
              Settings
            </NavLink>
          </nav>
          <div className="ml-auto flex items-center gap-3 text-sm">
            <span className="text-slate-600 dark:text-slate-300" data-testid="who">
              {nameAndTitle(me.name, me.title)}
            </span>
            {/* Signing out is a real form post: it ends with a redirect to Keycloak, which a fetch cannot follow. */}
            <form method="post" action="/api/auth/logout">
              <button className="rounded-lg px-2 py-1 text-slate-500 hover:bg-slate-100 hover:text-slate-900 dark:hover:bg-slate-800 dark:hover:text-white">
                Sign out
              </button>
            </form>
          </div>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-4 py-8">
        <Outlet />
      </main>
    </div>
  );
}
