import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'

// Exercise the actual component's exported planner without a browser or server.
const source = readFileSync(new URL('../src/components/GridPreview.tsx', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.CommonJS, jsx: ts.JsxEmit.ReactJSX },
})
const module = { exports: {} }
vm.runInNewContext(compiled.outputText, {
  module, exports: module.exports, require: createRequire(import.meta.url),
})
const { buildGridPreview } = module.exports
const config = {
  maxLevelsPerSide: 3, initialGapPoints: '20', gridSpacingPoints: '20', gridSpacingStepPoints: '0',
  baseLotSize: '1', lotSizeIncreasePercent: '0', maxTradeLot: '0',
}

for (const [gridMode, side, prices] of [
  ['BUY_ONLY', 'BUY', [99.9, 99.7, 99.5]],
  ['SELL_ONLY', 'SELL', [100.1, 100.3, 100.5]],
]) {
  test(`${gridMode} previews only its selected side without accumulating extra ticks`, () => {
    const levels = buildGridPreview({ ...config, gridMode }, '100', '0.01', '0.1')
    assert.equal(levels.length, 3)
    for (const level of levels) {
      assert.equal(level.side, side)
      assert.ok(Math.abs(level.price - prices[level.level]) < 1e-9)
      assert.equal(level.quantity, 1)
    }
  })
}

test('changing modes rebuilds the preview and Two-Way retains both sides', () => {
  const selected = { ...config, gridMode: 'BUY_ONLY' }
  assert.equal(buildGridPreview(selected, '100', '0.01', '0.1').length, 3)
  selected.gridMode = 'SELL_ONLY'
  assert.ok(buildGridPreview(selected, '100', '0.01', '0.1').every(x => x.side === 'SELL'))
  selected.gridMode = 'TWO_WAY'
  const levels = buildGridPreview(selected, '100', '0.01', '0.1')
  assert.equal(levels.length, 6)
  assert.equal(levels.filter(x => x.side === 'BUY').length, 3)
  assert.equal(levels.filter(x => x.side === 'SELL').length, 3)
})

test('off-tick centers still round buys down and sells up', () => {
  const levels = buildGridPreview({ ...config, maxLevelsPerSide: 1, gridMode: 'TWO_WAY' }, '100.005', '0.01', '0.1')
  assert.equal(levels.find(x => x.side === 'BUY').price.toFixed(2), '99.90')
  assert.equal(levels.find(x => x.side === 'SELL').price.toFixed(2), '100.11')
})

test('quantity normalization keeps exact steps and rounds fractional quantities down', () => {
  const levels = buildGridPreview({ ...config, gridMode: 'BUY_ONLY', baseLotSize: '0.3', lotSizeIncreasePercent: '50' }, '100', '0.01', '0.1')
  assert.deepEqual(Array.from(levels, x => x.quantity.toFixed(1)), ['0.3', '0.4', '0.6'])
})


test('Initial Gap is the full gap, with half applied to each side', () => {
  const levels = buildGridPreview({ ...config, initialGapPoints: '10', maxLevelsPerSide: 1, gridMode: 'TWO_WAY' }, '100', '0.01', '0.1')
  assert.equal(levels.find(x => x.side === 'BUY').price.toFixed(2), '99.95')
  assert.equal(levels.find(x => x.side === 'SELL').price.toFixed(2), '100.05')
})
