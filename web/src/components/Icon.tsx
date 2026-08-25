const paths: Record<string, string> = {
  dashboard: 'M3 3h7v7H3V3Zm11 0h7v7h-7V3ZM3 14h7v7H3v-7Zm11 0h7v7h-7v-7Z',
  strategy: 'M4 19h16M6 16l3-5 3 2 5-8 2 3M6 6h3v3H6V6Zm10 8h3v3h-3v-3Z',
  orders: 'M6 2h12v20H6V2Zm3 5h6M9 11h6M9 15h4',
  alert: 'M12 3 2 21h20L12 3Zm0 6v5m0 3v1',
  settings: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8Zm0-6 2 3 4-.5.5 4L22 11l-2 3 1 4-4 1.5L15 23l-3-2-3 2-2-3.5L3 18l1-4-2-3 3.5-2.5.5-4 4 .5 2-3Z',
  plus: 'M12 5v14M5 12h14',
  search: 'm20 20-4.5-4.5M10.5 18a7.5 7.5 0 1 1 0-15 7.5 7.5 0 0 1 0 15Z',
  close: 'm6 6 12 12M18 6 6 18',
  shield: 'M12 2 4 5v6c0 5 3.5 9 8 11 4.5-2 8-6 8-11V5l-8-3Zm-3 10 2 2 4-5',
  refresh: 'M20 7v5h-5M4 17v-5h5M6.1 8A7 7 0 0 1 18 7l2 5M4 12l2 5a7 7 0 0 0 11.9-1',
  check: 'm5 12 4 4L19 6',
  download: 'M12 3v12m-5-5 5 5 5-5M4 20h16',
  collapse: 'm15 18-6-6 6-6',
  expand: 'm9 18 6-6-6-6',
}

export function Icon({ name, size = 20 }: { name: string; size?: number }) {
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d={paths[name] ?? paths.dashboard} />
  </svg>
}
