import { createContext, useContext } from 'react';
import type { ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { atlasApi } from './api';
import type { Session } from './api';
import { ErrorState, LoadingState } from './states';

const SessionContext = createContext<Session | null>(null);
export const useSession = () => {
  const session = useContext(SessionContext);
  if (!session) throw new Error('Session is not available');
  return session;
};

export function SessionProvider({ children }: { children: ReactNode }) {
  const session = useQuery({ queryKey: ['session'], queryFn: atlasApi.session, retry: false, staleTime: 60_000 });
  if (session.isPending) return <main className="page-state"><LoadingState label="正在确认身份与权限" /></main>;
  if (session.isError || !session.data) return <main className="page-state"><ErrorState error={session.error} onRetry={() => session.refetch()} /></main>;
  return <SessionContext.Provider value={session.data}>{children}</SessionContext.Provider>;
}
