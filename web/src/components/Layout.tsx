import { Link, Outlet, useRouterState } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { Toaster } from '@/components/ui/sonner'
import { api } from '../lib/api'
import { Badge, Separator, cn } from './ui'

const NAV = [
  { to: '/', label: 'Dashboard', icon: '🎬' },
  { to: '/queue', label: 'Queue', icon: '📋' },
  { to: '/review', label: 'Review', icon: '✅' },
  { to: '/calendar', label: 'Calendar', icon: '🗓️' },
  { to: '/clips', label: 'Clips', icon: '🐱' },
  { to: '/settings', label: 'Settings', icon: '⚙️' },
] as const

export function Layout() {
  const path = useRouterState({ select: (s) => s.location.pathname })

  const { data: stats } = useQuery({
    queryKey: ['stats'],
    queryFn: api.stats,
    refetchInterval: 4000,
  })

  return (
    <div className="flex min-h-screen">
      <aside className="flex w-60 shrink-0 flex-col border-r border-sidebar-border bg-sidebar">
        <div className="flex items-center gap-2.5 px-5 py-5">
          <span className="text-2xl">🐱</span>
          <div>
            <div className="font-heading text-sm font-bold tracking-tight">CatMeme Maker</div>
            <div className="text-[11px] text-muted-foreground">Reel pipeline</div>
          </div>
        </div>

        <nav className="flex flex-1 flex-col gap-1 px-3">
          {NAV.map((n) => {
            const active = n.to === '/' ? path === '/' : path.startsWith(n.to)
            const badge =
              n.to === '/review'
                ? stats?.awaitingApproval
                : n.to === '/queue'
                  ? stats?.rendering
                  : undefined

            return (
              <Link
                key={n.to}
                to={n.to}
                className={cn(
                  'flex items-center gap-3 rounded-lg px-3 py-2 text-sm transition-colors',
                  active
                    ? 'bg-sidebar-accent font-medium text-sidebar-accent-foreground'
                    : 'text-muted-foreground hover:bg-sidebar-accent/60 hover:text-foreground',
                )}
              >
                <span className="text-base">{n.icon}</span>
                <span className="flex-1">{n.label}</span>
                {!!badge && <Badge>{badge}</Badge>}
              </Link>
            )
          })}
        </nav>

        <div className="px-5 py-4">
          <Separator className="mb-3" />
          <div className="space-y-1 text-[11px] text-muted-foreground">
            <div className="flex justify-between">
              <span>Clips</span>
              <span className="text-foreground">{stats?.clips ?? '—'}</span>
            </div>
            <div className="flex justify-between">
              <span>Posted</span>
              <span className="text-foreground">{stats?.posted ?? '—'}</span>
            </div>
          </div>
          <a
            href="/hangfire"
            target="_blank"
            rel="noreferrer"
            className="mt-3 block text-[11px] text-muted-foreground underline-offset-2 hover:text-primary hover:underline"
          >
            Hangfire dashboard →
          </a>
        </div>
      </aside>

      <main className="flex-1 overflow-x-hidden">
        <div className="mx-auto max-w-6xl px-8 py-8">
          <Outlet />
        </div>
      </main>

      <Toaster position="bottom-right" richColors />
    </div>
  )
}
