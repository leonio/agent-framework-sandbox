import { CredentialsCard } from '../components/settings/CredentialsCard';
import { EndpointsCard } from '../components/settings/EndpointsCard';
import { ProfileCard } from '../components/settings/ProfileCard';

/** Settings: your profile, your stored keys, and the model endpoints you can use. */
export function SettingsPage() {
  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
      <ProfileCard />
      <CredentialsCard />
      <EndpointsCard />
    </div>
  );
}
