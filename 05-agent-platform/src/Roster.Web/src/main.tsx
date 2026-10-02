import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createBrowserRouter } from 'react-router';
import { RouterProvider } from 'react-router/dom';
import { ApiError } from './api/client';
import { AuthGate } from './auth';
import { Layout } from './components/Layout';
import { AssignmentsPage } from './pages/AssignmentsPage';
import { NewAssignmentPage } from './pages/NewAssignmentPage';
import { AssignmentPage } from './pages/AssignmentPage';
import { AgentsPage } from './pages/AgentsPage';
import { AgentPage } from './pages/AgentPage';
import { SettingsPage } from './pages/SettingsPage';
import './index.css';

// Server state lives in TanStack Query. Live updates come from the server-sent events stream, which invalidates
// queries, so there is no need to refetch on window focus.
//
// Retries are for failures that may pass (the network, a 5xx while a service restarts). A 4xx is the API's answer,
// "not yours" or "no such thing", and asking again would only keep a spinner up for seconds before saying so.
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      staleTime: 5_000,
      retry: (failures, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && failures < 3,
    },
  },
});

const router = createBrowserRouter([
  {
    element: <Layout />,
    children: [
      { index: true, element: <AssignmentsPage /> },
      { path: 'assignments/new', element: <NewAssignmentPage /> },
      { path: 'assignments/:id', element: <AssignmentPage /> },
      { path: 'agents', element: <AgentsPage /> },
      { path: 'agents/:name', element: <AgentPage /> },
      { path: 'settings', element: <SettingsPage /> },
    ],
  },
]);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <AuthGate>
        <RouterProvider router={router} />
      </AuthGate>
    </QueryClientProvider>
  </StrictMode>,
);
