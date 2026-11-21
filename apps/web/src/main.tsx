import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';

const root = document.getElementById('root');
if (!root) throw new Error('Missing application root');

createRoot(root).render(
  <StrictMode>
    <main>
      <h1>Business Atlas</h1>
      <p>开发预览：API、后台进程和网页框架已部署。业务功能正在开发。</p>
    </main>
  </StrictMode>,
);
