import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { SessionProvider } from './shared/session';
import ProjectsPage from './routes/ProjectsPage';
import ProjectShell from './features/project/ProjectShell';
import OverviewPage from './features/project/OverviewPage';
import CatalogPage from './features/project/CatalogPage';
import BusinessPage from './features/project/BusinessPage';
import KnowledgePage from './features/project/KnowledgePage';
import ImportWorkspace from './features/project/ImportWorkspace';
import ReviewWorkspace from './features/review/ReviewWorkspace';
import TaskWorkspace from './features/task/TaskWorkspace';
import DeliveryWorkspace from './features/delivery/DeliveryWorkspace';
import SelfProjectPage from './features/public/SelfProjectPage';
import './styles.css';
import './styles-workspace.css';

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 30_000, refetchOnWindowFocus: false }, mutations: { retry: false } } });

createRoot(document.getElementById('root')!).render(<StrictMode><QueryClientProvider client={queryClient}><BrowserRouter><SessionProvider><Routes>
  <Route path="/" element={<Navigate to="/public/self" replace />} />
  <Route path="/public/self" element={<SelfProjectPage />} />
  <Route path="/projects" element={<ProjectsPage />} />
  <Route path="/p/:projectId" element={<ProjectShell />}>
    <Route index element={<Navigate to="overview" replace />} />
    <Route path="overview" element={<OverviewPage />} />
    <Route path="catalog" element={<CatalogPage />} />
    <Route path="business" element={<BusinessPage />} />
    <Route path="knowledge/:unitId" element={<KnowledgePage />} />
    <Route path="review" element={<ReviewWorkspace />} />
    <Route path="review/:changeSetId" element={<ReviewWorkspace />} />
    <Route path="tasks" element={<TaskWorkspace />} />
    <Route path="tasks/:taskId" element={<TaskWorkspace />} />
    <Route path="delivery" element={<DeliveryWorkspace />} />
    <Route path="delivery/:taskId" element={<DeliveryWorkspace />} />
    <Route path="imports" element={<ImportWorkspace />} />
  </Route>
  <Route path="*" element={<Navigate to="/projects" replace />} />
</Routes></SessionProvider></BrowserRouter></QueryClientProvider></StrictMode>);
