import { create } from 'zustand'
import { persist } from 'zustand/middleware'
import { toast as sonner } from 'sonner'

interface GenerateState {
  /** Persisted so the count survives navigation, per the spec. */
  count: number
  setCount: (n: number) => void
}

export const useGenerateStore = create<GenerateState>()(
  persist(
    (set) => ({
      count: 5,
      setCount: (n) => set({ count: n }),
    }),
    { name: 'catmeme-generate' },
  ),
)

/** Thin alias over sonner so pages keep a single import for notifications. */
export const toast = {
  success: (m: string) => sonner.success(m),
  error: (m: string) => sonner.error(m),
  info: (m: string) => sonner(m),
}
