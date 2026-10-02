import { Link } from 'react-router';
import { nameAndTitle, useMe } from '../auth';
import { Card } from '../components/ui';

/** The landing page until the assignment pages exist: who you are, and where to set your title. */
export function Welcome() {
  const me = useMe();
  return (
    <Card title="Welcome">
      <p className="text-sm">
        Signed in as <strong>{nameAndTitle(me.name, me.title)}</strong>.
      </p>
      {!me.title && (
        <p className="mt-2 text-sm text-slate-600 dark:text-slate-400">
          Add your title in <Link to="/settings" className="text-indigo-600 hover:underline">settings</Link>: it shows next to
          your name on every decision and card.
        </p>
      )}
    </Card>
  );
}
