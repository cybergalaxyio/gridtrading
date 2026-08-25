# Grid Trading V1

依据 `v1/` 规格实现的半自动网格交易终端。当前版本支持确定性 Replay、Paper 与 Hyperliquid Testnet API Wallet 交易；**不会连接 Mainnet，也不具备提现能力或 Mainnet 签名路径**。

## 已实现

- React + TypeScript + Vite 深色交易终端，覆盖控制台、策略、创建与预览、订单/成交、告警、设置和紧急停止界面。
- Lightweight Charts K 线、成交量、固定中心和网格价格线。
- ASP.NET Core Web API、SignalR 增量事件和后台 Paper 撮合服务。
- Hyperliquid Testnet API Wallet：官方 EIP-712 签名、持久化单调 nonce、稳定 CLOID、真实挂撤单、成交对账、reduce-only TP 与清仓。
- API Wallet 私钥仅从后端环境变量注入，并以 AES-256-GCM 加密保存；浏览器和账户 API 只看到公开地址。
- SQLite 持久化，启用 WAL、Foreign Keys 和 Busy Timeout；金额和数量使用 `decimal` 并无损保存为文本。
- Strategy / Frozen Cycle 分离、异步命令、幂等键、`If-Match` 状态版本和审计记录。
- 固定中心、递增间距、几何层级仓位、保守 Tick/Quantity 取整、滚动双向 Entry、独立 TP、同层重入。
- 单向净持仓账本、Execution ID 去重、部分成交 Lot 语义和 `MaxNetLot` 最坏情形活动订单容量预留。
- Basket 清算 PnL、Paper 自动 Basket TP/SL、有序关闭、紧急撤单与清零、人工对账；Testnet 自动阈值清仓需另行明确授权。
- 保守 OHLC 回放：同一 Bar 同时触及 Entry 与 TP 时，不假设有利成交顺序。
- 服务端硬性拒绝名称包含 `live` 或 `mainnet` 的账户；Hyperliquid 客户端只接受官方 Testnet Info URL。

## Hyperliquid Testnet

完整配置和用户操作见 [docs/TESTNET_TRADING.md](docs/TESTNET_TRADING.md)。启动 Testnet cycle 前，后端会强制验证 API Wallet 授权、账户已有测试资金、实际仓位为零以及没有残留挂单。nonce 由后端按签名地址管理，用户无需输入。

## 本地运行

要求 .NET 10 SDK 与 Node.js 24+。

```bash
cd web
npm install
npm run build
cd ..
dotnet run --project src/GridTrading.Api/GridTrading.Api.csproj --urls http://localhost:5050
```

打开 <http://localhost:5050>。前端生产构建会输出到 `src/GridTrading.Api/wwwroot`，由 API 同源托管。

前后端分离开发：

```bash
dotnet run --project src/GridTrading.Api/GridTrading.Api.csproj --urls http://localhost:5050
cd web && npm run dev
```

Vite 开发服务器为 <http://localhost:5173>，并代理 `/api` 与 `/hubs`。

## 验证

```bash
dotnet restore GridTrading.slnx
dotnet build GridTrading.slnx --no-restore
dotnet test GridTrading.slnx --no-build
cd web && npm run build
```

## 目录

- `src/GridTrading.Domain`：纯算法、状态机、风险容量、账本和保守回放策略。
- `src/GridTrading.Api`：REST、SignalR、SQLite、Paper 撮合、回放及 Hyperliquid Testnet 交易适配器。
- `tests/GridTrading.Domain.Tests`：算法和安全规则测试。
- `web`：React 控制台。
- `v1`：原始产品、算法、API 与设计规格。

## 安全边界

Paper 撮合仅用于开发验证。Hyperliquid 交易适配器固定使用官方 Testnet HTTPS 端点，并强制拒绝 Mainnet / Live 账户。API Wallet 私钥不会通过 HTTP 接收；V1 的 HTTP 写操作还会拒绝非 loopback 来源。部署时应保护后端环境变量与 `GRID_TRADING_CREDENTIAL_KEY`。Testnet 仍可能产生不可逆的测试资金损失，启动和关闭前请核对实际账户仓位与挂单。
