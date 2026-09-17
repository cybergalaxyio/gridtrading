import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'

const module = { exports: {} }
const source = readFileSync(new URL('../src/components/EntryHolds.tsx', import.meta.url), 'utf8')
vm.runInNewContext(ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
}).outputText, { module, exports: module.exports, require: createRequire(import.meta.url) })
const { EntryHolds } = module.exports
const now = Date.parse('2026-09-17T00:00:00Z')
const hold = { side: 'SELL', reason: 'FILL_LIMIT', resumeAt: '2026-09-17T00:42:01Z', filledCount: 3, fillLimit: 3 }
const render = (holds, time = now) => renderToStaticMarkup(createElement(EntryHolds, {
  holds, now: time, formatDate: () => '2026-09-17 08:42:01',
}))

test('active sell fill restriction shows its resume datetime', () => {
  const html = render([hold])
  assert.match(html, /HOLD · SELL/)
  assert.doesNotMatch(html, /Entry fill limit|midpoint|no scheduled time/)
  assert.match(html, /2026-09-17 08:42:01/)
})

test('other reasons from older backends and buy restrictions stay hidden', () => {
  for (const reason of ['PRICE_OUTSIDE_GRID', 'LEVELS_OCCUPIED', 'HISTORY_NOT_READY', 'MARKET_DATA_UNAVAILABLE', 'OPERATOR_PAUSED', 'RISK_PAUSED']) {
    assert.equal(render([{ ...hold, reason }]), '')
  }
  assert.equal(render([{ ...hold, side: 'BUY' }]), '')
})

test('indicator disappears exactly at expiry without waiting for another snapshot', () => {
  assert.equal(render([hold], Date.parse(hold.resumeAt)), '')
  assert.equal(render([hold], Date.parse(hold.resumeAt) + 1000), '')
  assert.equal(render([{ ...hold, resumeAt: null }]), '')
  assert.equal(render([]), '')
})
