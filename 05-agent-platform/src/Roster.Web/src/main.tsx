import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createBrowserRouter } from 'react-router';
import { RouterProvider } from 'react-router/dom';
import { AuthGate } from './auth';
import { Layout } from './components/Layout';
import { Welcome } from './pages/Welcome';
import './index.css';

// Server state lives in TanStack Query. Live updates come from the server-sent events stream, which invalidates
// queries, so there is no need to refetch on window focus.
const queryClient = new QueryClient({
  defaultOptions: { queries: { refetchOnWindowFocus: false, staleTime: 5_000 } },
});

const router = createBrowserRouter([
  {
    element: <Layout />,
    children: [{ index: true, element: <Welcome /> }],
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
