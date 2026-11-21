import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';

const root = document.getElementById('root');
if (!root) throw new Error('Missing application root');

createRoot(root).render(
  <StrictMode>
    <main>
      <h1>Business Atlas</h1>
      <p>PREPARATION build. Product workspaces are not implemented in T02.</p>
    </main>
  </StrictMode>,
);
