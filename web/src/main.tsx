import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  createRootRoute,
  createRoute,
  createRouter,
  RouterProvider,
} from '@tanstack/react-router'

import './index.css'
import { Layout } from './components/Layout'
import { Dashboard } from './pages/Dashboard'
import { Queue } from './pages/Queue'
import { Review } from './pages/Review'
import { Calendar } from './pages/Calendar'
import { Clips } from './pages/Clips'
import { Settings } from './pages/Settings'

const rootRoute = createRootRoute({ component: Layout })

const routes = [
  createRoute({ getParentRoute: () => rootRoute, path: '/', component: Dashboard }),
  createRoute({ getParentRoute: () => rootRoute, path: '/queue', component: Queue }),
  createRoute({ getParentRoute: () => rootRoute, path: '/review', component: Review }),
  createRoute({ getParentRoute: () => rootRoute, path: '/calendar', component: Calendar }),
  createRoute({ getParentRoute: () => rootRoute, path: '/clips', component: Clips }),
  createRoute({ getParentRoute: () => rootRoute, path: '/settings', component: Settings }),
]

const router = createRouter({ routeTree: rootRoute.addChildren(routes) })

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // The pipeline is long running; stale reads are cheap and refetch is frequent.
      staleTime: 1500,
      retry: 1,
      refetchOnWindowFocus: true,
    },
  },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
)
