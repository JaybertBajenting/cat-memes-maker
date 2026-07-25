/**
 * App-level component layer.
 *
 * Primitives come from shadcn (src/components/ui/*). This module re-exports the ones the
 * pages use and adds the few app-specific pieces shadcn does not ship: a labelled form
 * field, a pipeline status chip, an empty state, and a spinner.
 */
import type * as React from 'react'
import type { ReactNode } from 'react'
import { Loader2Icon } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader as SCardHeader, CardTitle, CardDescription, CardAction } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Separator } from '@/components/ui/separator'
import { Switch } from '@/components/ui/switch'
import { Textarea } from '@/components/ui/textarea'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { cn } from '@/lib/utils'

/** shadcn's Button has no loading state; the pipeline pages need one everywhere. */
export function LoadingButton({
  loading,
  children,
  ...rest
}: React.ComponentProps<typeof Button> & { loading?: boolean }) {
  return (
    <Button {...rest} disabled={rest.disabled || loading}>
      {loading && <Loader2Icon className="size-4 animate-spin" aria-hidden />}
      {children}
    </Button>
  )
}

export {
  Badge,
  Button,
  Card,
  CardContent,
  CardTitle,
  CardDescription,
  CardAction,
  Input,
  Label,
  Separator,
  Switch,
  Textarea,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  cn,
}

export function Spinner({ className }: { className?: string }) {
  return <Loader2Icon className={cn('size-4 animate-spin', className)} aria-hidden />
}

/**
 * Pager for the server-paged lists. Renders nothing when everything already fits on
 * one page, so short queues stay uncluttered.
 */
export function Pager({
  page,
  pageSize,
  total,
  onPage,
}: {
  page: number
  pageSize: number
  total: number
  onPage: (p: number) => void
}) {
  const last = Math.max(1, Math.ceil(total / pageSize))
  if (last <= 1) return null

  const first = (page - 1) * pageSize + 1
  const shown = Math.min(page * pageSize, total)

  return (
    <div className="flex items-center justify-between gap-4 pt-2">
      <p className="text-xs text-muted-foreground">
        {first}–{shown} of {total}
      </p>
      <div className="flex items-center gap-2">
        <Button size="sm" variant="ghost" onClick={() => onPage(page - 1)} disabled={page <= 1}>
          ← Prev
        </Button>
        <span className="text-xs text-muted-foreground tabular-nums">
          {page} / {last}
        </span>
        <Button size="sm" variant="ghost" onClick={() => onPage(page + 1)} disabled={page >= last}>
          Next →
        </Button>
      </div>
    </div>
  )
}

/** Card header with the title/subtitle/action shape the pipeline pages use. */
export function CardHeader({
  title,
  subtitle,
  action,
}: {
  title: string
  subtitle?: string
  action?: ReactNode
}) {
  return (
    <>
      <SCardHeader>
        <CardTitle className="text-sm font-semibold tracking-wide uppercase">{title}</CardTitle>
        {subtitle && <CardDescription className="text-xs">{subtitle}</CardDescription>}
        {action && <CardAction>{action}</CardAction>}
      </SCardHeader>
      <Separator />
    </>
  )
}

export function Field({
  label,
  hint,
  error,
  htmlFor,
  children,
}: {
  label: string
  hint?: string
  error?: string
  htmlFor?: string
  children: ReactNode
}) {
  return (
    <div className="space-y-1.5">
      <Label htmlFor={htmlFor} className="text-xs">
        {label}
      </Label>
      {children}
      {error ? (
        <p className="text-xs text-destructive">{error}</p>
      ) : (
        hint && <p className="text-xs text-muted-foreground">{hint}</p>
      )}
    </div>
  )
}

const STATUS_STYLES: Record<string, string> = {
  rendering: 'bg-sky-500/15 text-sky-300 ring-1 ring-sky-500/30',
  awaiting_approval: 'bg-amber-500/15 text-amber-300 ring-1 ring-amber-500/30',
  ready: 'bg-emerald-500/15 text-emerald-300 ring-1 ring-emerald-500/30',
  posted: 'bg-violet-500/15 text-violet-300 ring-1 ring-violet-500/30',
  failed: 'bg-rose-500/15 text-rose-300 ring-1 ring-rose-500/30',
  cancelled: 'bg-muted text-muted-foreground ring-1 ring-border',
  rejected: 'bg-muted text-muted-foreground ring-1 ring-border',
  scheduled: 'bg-sky-500/15 text-sky-300 ring-1 ring-sky-500/30',
}

export function StatusChip({ status }: { status: string }) {
  return (
    <Badge
      variant="secondary"
      className={cn('capitalize', STATUS_STYLES[status] ?? 'bg-muted text-muted-foreground')}
    >
      {status.replace(/_/g, ' ')}
    </Badge>
  )
}

export function EmptyState({
  icon,
  title,
  body,
  action,
}: {
  icon: string
  title: string
  body: string
  action?: ReactNode
}) {
  return (
    <div className="flex flex-col items-center justify-center gap-3 px-6 py-16 text-center">
      <div className="text-4xl">{icon}</div>
      <h3 className="text-base font-semibold text-foreground">{title}</h3>
      <p className="max-w-md text-sm text-muted-foreground">{body}</p>
      {action}
    </div>
  )
}
