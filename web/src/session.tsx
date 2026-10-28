import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { api, getToken, setToken, setUnauthorizedHandler, NetworkError } from './api'
import type { Bootstrap, Me } from './types'
import { setCurrency } from './format'

interface Session {
  me: Me | null
  boot: Bootstrap | null
  serverUp: boolean
  can: (perm: string) => boolean
  login: (u: string, p: string) => Promise<void>
  logout: () => void
  refreshBoot: () => Promise<void>
  registerId: string | null
  setRegisterId: (id: string) => void
  myShift: () => Bootstrap['openShifts'][number] | undefined
  ready: boolean
}
const Ctx = createContext<Session>(null as unknown as Session)
export const useSession = () => useContext(Ctx)

const REG_KEY = 'pos.register'
const lsGet = (k: string) => { try { return localStorage.getItem(k) } catch { return null } }
const lsSet = (k: string, v: string) => { try { localStorage.setItem(k, v) } catch { /* noop */ } }

export function SessionProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null)
  const [boot, setBoot] = useState<Bootstrap | null>(null)
  const [serverUp, setServerUp] = useState(true)
  const [ready, setReady] = useState(false)
  const [registerId, setReg] = useState<string | null>(lsGet(REG_KEY))

  const logout = useCallback(() => { setToken(null); setMe(null); setBoot(null) }, [])
  useEffect(() => { setUnauthorizedHandler(logout) }, [logout])

  const refreshBoot = useCallback(async () => {
    const b = await api.get<Bootstrap>('/api/bootstrap')
    setCurrency(b.company.currencySymbol); setBoot(b)
  }, [])

  useEffect(() => {
    (async () => {
      if (getToken()) {
        try { setMe(await api.get<Me>('/api/auth/me')); await refreshBoot() } catch (e) { if (!(e instanceof NetworkError)) setToken(null) }
      }
      setReady(true)
    })()
  }, [refreshBoot])

  // Vigilancia del servidor: si no responde, se bloquean los cobros y se muestra el aviso.
  useEffect(() => {
    let alive = true
    const ping = async () => {
      try { const r = await fetch('/api/health', { cache: 'no-store' }); if (alive) setServerUp(r.ok) } catch { if (alive) setServerUp(false) }
    }
    ping(); const t = setInterval(ping, 4000)
    return () => { alive = false; clearInterval(t) }
  }, [])

  const login = useCallback(async (username: string, password: string) => {
    const r = await api.post<{ token: string; user: Me }>('/api/auth/login', { username, password })
    setToken(r.token); setMe(r.user); await refreshBoot()
  }, [refreshBoot])

  const value = useMemo<Session>(() => ({
    me, boot, serverUp, ready, login, logout, refreshBoot, registerId,
    can: p => !!me?.permissions.includes(p),
    setRegisterId: id => { lsSet(REG_KEY, id); setReg(id) },
    myShift: () => boot?.openShifts.find(s => s.registerId === registerId),
  }), [me, boot, serverUp, ready, login, logout, refreshBoot, registerId])
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}
