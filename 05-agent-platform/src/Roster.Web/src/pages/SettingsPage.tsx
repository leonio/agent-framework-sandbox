import { CredentialsCard } from '../components/settings/CredentialsCard';
import { ProfileCard } from '../components/settings/ProfileCard';

/** Settings: your profile and your stored keys. */
export function SettingsPage() {
  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
      <ProfileCard />
      <CredentialsCard />
    </div>
  );
}
