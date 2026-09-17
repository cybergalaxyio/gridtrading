import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
const source = readFileSync(new URL('../src/lib/chartIndicators.ts', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } })
const module = { exports: {} }
vm.runInNewContext(compiled.outputText, { module, exports: module.exports })
const { chartIndicators, displayedCandles } = module.exports
const fixture = JSON.parse(readFileSync(new URL('../../tests/fixtures/technical-indicators.json', import.meta.url)))

test('ATR and Bollinger Bands match the independent reference used by the backend', () => {
  const values = chartIndicators(fixture.candles)
  values.forEach((point, i) => {
    for (const key of ['atr', 'middle', 'upper', 'lower']) {
      const expected = fixture.expected[i][key]
      if (expected === null) assert.equal(point[key], null)
      else assert.ok(Math.abs(point[key] - expected) < 1e-9, `${key} at ${i}`)
    }
  })
})
test('live prices update only the current interval, preserve history, and leave input unchanged', () => {
  const bar = { time: 1800000000, open: '100', high: '101', low: '99', close: '100', volume: '10' }
  const live = displayedCandles([bar], 102, 900, (bar.time + 60) * 1000)
  assert.equal(live[0].high, '102'); assert.equal(live[0].close, '102')
  assert.equal(bar.close, '100')
  assert.equal(displayedCandles([bar], 102, 900, (bar.time + 900) * 1000)[0].close, '100')
  assert.equal(displayedCandles([bar], NaN, 900, (bar.time + 60) * 1000)[0].close, '100')
})
test('duplicate and invalid candles do not corrupt plotted indicators', () => {
  const bars = fixture.candles.slice(0, 20)
  const display = displayedCandles([...bars.reverse(), bars[2], { ...bars[1], close: 'NaN' }], undefined, 900)
  assert.equal(display.length, 20)
  assert.ok(display.every((x, i) => i === 0 || x.time > display[i-1].time))
  assert.equal(chartIndicators(display)[18].middle, null)
  assert.ok(chartIndicators(display)[19].middle > 0)
})
