import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'

function loadApi() {
  const requests = []
  const module = { exports: {} }
  const source = readFileSync(new URL('../src/api.ts', import.meta.url), 'utf8')
  vm.runInNewContext(ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS },
  }).outputText, {
    module, exports: module.exports,
    fetch: async (url, init = {}) => {
      requests.push({ url, method: init.method ?? 'GET', body: init.body ? JSON.parse(init.body) : null })
      return init.method === 'DELETE'
        ? { ok: true, status: 204, json: async () => ({}) }
        : { ok: true, status: 200, json: async () => ({ configured: true, tokenStored: true, chatId: '-100123' }) }
    },
  })
  return { ...module.exports, requests }
}

test('Telegram settings client uses the local settings and command endpoints', async () => {
  const { api, requests } = loadApi()

  await api.telegramSettings()
  await api.saveTelegramSettings({ botToken: 'secret', chatId: '-100123' })
  await api.testAndEnableTelegram()
  await api.disableTelegram()
  await api.removeTelegram()

  assert.deepEqual(requests.map(x => [x.method, x.url]), [
    ['GET', '/api/v1/notification-settings/telegram'],
    ['PUT', '/api/v1/notification-settings/telegram'],
    ['POST', '/api/v1/notification-settings/telegram/test-and-enable'],
    ['POST', '/api/v1/notification-settings/telegram/disable'],
    ['DELETE', '/api/v1/notification-settings/telegram'],
  ])
  assert.deepEqual(requests[1].body, { botToken: 'secret', chatId: '-100123' })
})

test('Settings UI masks the bot token and exposes explicit activation controls', () => {
  const source = readFileSync(new URL('../src/pages/TestnetSettingsPage.tsx', import.meta.url), 'utf8')

  assert.match(source, /type="password"/)
  assert.match(source, /已安全保存；留空则保持不变/)
  assert.match(source, /测试并启用/)
  assert.match(source, /禁用通知/)
  assert.match(source, /清除配置/)
  assert.doesNotMatch(source, /setBotToken\(value\./)
})
