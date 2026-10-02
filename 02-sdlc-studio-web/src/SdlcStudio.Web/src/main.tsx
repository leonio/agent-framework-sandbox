import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createBrowserRouter, RouterProvider } from "react-router";
import "./index.css";
import { Layout } from "./components/Layout";
import { Dashboard } from "./pages/Dashboard";
import { NewRun } from "./pages/NewRun";
import { ImportRun } from "./pages/ImportRun";
import { RunPage } from "./pages/RunPage";
import { Admin } from "./pages/Admin";

const router = createBrowserRouter([
  {
    element: <Layout />,
    children: [
      { path: "/", element: <Dashboard /> },
      { path: "/new", element: <NewRun /> },
      { path: "/import", element: <ImportRun /> },
      { path: "/runs/:id", element: <RunPage /> },
      { path: "/admin", element: <Admin /> },
    ],
  },
]);

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <RouterProvider router={router} />
  </StrictMode>,
);
