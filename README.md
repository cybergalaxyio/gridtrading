# Grid Trading V1

依据 `v1/` 规格实现的半自动网格交易终端。当前版本支持确定性 Replay、Paper、Hyperliquid Testnet 与独立 Mainnet API Wallet 交易。Mainnet 按用户配置执行，点击 Start 启动实盘，无提现功能。配置步骤见 [Mainnet 操作指南](docs/MAINNET_TRADING.md)。

## 已实现

- React + TypeScript + Vite 深色交易终端，覆盖控制台、策略、创建与预览、订单/成交、告警、设置和紧急停止界面。
- Lightweight Charts K 线、成交量、固定中心和网格价格线。
- ASP.NET Core Web API、SignalR 增量事件和后台 Paper 撮合服务。
- Hyperliquid Testnet API Wallet：官方 EIP-712 签名、持久化单调 nonce、稳定 CLOID、真实挂撤单、成交对账、普通 Limit TP；Mainnet 使用 reduce-only 清仓。
- API Wallet 私钥仅从后端环境变量注入，并以 AES-256-GCM 加密保存；浏览器和账户 API 只看到公开地址。
- SQLite 持久化，启用 WAL、Foreign Keys 和 Busy Timeout；金额和数量使用 `decimal` 并无损保存为文本。
- Strategy / Frozen Cycle 分离、异步命令、幂等键、`If-Match` 状态版本和审计记录。
- 固定中心、递增间距、几何层级仓位、保守 Tick/Quantity 取整、滚动双向 Entry、独立 TP、同层重入。
- 单向净持仓账本、Execution ID 去重、部分成交 Lot 语义和 `MaxNetLot` 最坏情形活动订单容量预留。
- Basket 清算 PnL、Paper 自动 Basket TP/SL、有序关闭、紧急撤单与清零、人工对账；配置的 Basket TP/SL 阈值由后端对账触发自动关闭，依赖服务持续在线。
- 保守 OHLC 回放：同一 Bar 同时触及 Entry 与 TP 时，不假设有利成交顺序。
- Testnet/Mainnet 独立账户、签名、官方主机与行情订阅；Mainnet 使用用户填写的策略参数，无试运行预设或额外下单开关。
- 可选 Telegram 风险告警推送：Bot Token 加密保存，先测试后启用，后台投递不会阻塞交易。

## 中心模式与 Initial Gap

- **Current Mid**：无需输入中心价格。预览使用当时的 Bid / Ask；Start 在后端重新获取实时行情并生成实际 Grid。`Buy0 = Bid − Initial Gap × Tick Size ÷ 2`，`Sell0 = Ask + Initial Gap × Tick Size ÷ 2`。
- **Manual**：必须填写大于 0 的中心价格。价格随策略保存，预览和 Start 都使用该值；Buy0 / Sell0 分别位于手动中心下方 / 上方半个 Initial Gap。
- **Initial Gap** 单位为 pts，表示完整初始间距，每侧使用一半；`0` 自动使用 Grid Spacing 作为完整间距。Buy 向下、Sell 向上按 Tick Size 取整。Current Mid 的首层买卖价差还包含启动时的 Bid / Ask spread。
- Cycle 启动后冻结中心、Bid / Ask 基准和实际 Grid，不随市场或策略编辑移动。已有运行中 Cycle 保留原计划。此前未保存中心价格的 Manual 策略需要编辑并补填价格后才能启动。

## Entry Fill Limit

在“资金与风控”中启用 **Enable Entry Fill Limit**，配置 **Lookback Window (min)** 和 **Max Filled Entries per Side**。默认关闭，初始值为 60 分钟、3 张；买卖分别计数，共用参数。

下新 Entry 前只查询订单历史，以 `FilledAt` 统计窗口内完全成交的 Entry，每张订单计一次；部分成交、TP 和平仓不计数。历史限定为同一策略、环境、账户和市场，包含之前 Cycle。达到上限后停止该方向的新 Entry，并撤销该方向的 Entry 余单（包括部分成交余量），保留 TP；撤单失败会在 Sync 重试。数量降至上限以下后，正常对账恢复下单资格，仍受其他风控限制。

后端启动时自动添加完成时间字段并一次性回填旧订单；无法确认完成时间时，通过告警提示该方向历史未就绪并阻止新 Entry。重启不会清零计数。配置在 Cycle 启动时冻结，编辑策略只影响未来 Cycle；旧 Cycle 默认关闭。此规则按完全成交数量限制后续下单，不保证交易所在撤单确认前不会继续成交已有订单。

## 自动开始下一轮

勾选“止盈 / 止损关闭后自动开始下一轮”后，Basket TP / SL 成功关闭当前 Cycle 会保存一个自动重启请求，后台约 2 秒内尝试启动下一轮。初次启动仍需手动 Start。旧 Cycle 的实际仓位、成交账本及未决挂单必须清零，历史虚拟持仓数量必须与已成交的平仓记录核对一致，新一轮仍执行正常启动检查。

- Current Mid 每轮获取启动时的实时 Bid / Ask；Manual 使用策略保存的中心价格。
- 自动重启保持原来的账户和执行环境，采用最新保存的策略参数。变更 Symbol 后需手动启动。
- 手动关闭、紧急平仓、人工暂停期间触发的关闭、故障关闭不会自动重启。关闭自动重启选项或归档策略会取消尚未执行的请求。
- 自动重启失败时写入 `AUTO_RESTART_FAILED` 告警并停止重试，检查后可手动 Start。服务重启会继续处理尚未开始的请求；启动中断且结果不确定时停止自动操作，避免重复下单。
- 控制台自动刷新并跟随新 Cycle。开启开关后需启动新 Cycle 才生效；关闭开关可阻止待执行的重启。

## Telegram 风险告警通知

1. 在 Telegram 中通过 @BotFather 创建 Bot 并复制 Bot Token。私聊需要先向 Bot 发送一条消息；群组需要把 Bot 加入群组；频道需要授予 Bot 发消息权限。
2. 获取接收目标：私聊和群组使用数字 Chat ID（群组通常为负数，超级群组/频道通常以 -100 开头）；公开频道也可填写 @channel_username。可通过 Telegram Bot API 的 getUpdates 查看 Bot 收到的更新并找到 message.chat.id 或 channel_post.chat.id。
3. 后端必须设置稳定的 GRID_TRADING_CREDENTIAL_KEY（base64 编码的 32 字节密钥）并重启。该密钥用于 AES-256-GCM 加密 Bot Token；密钥变更后已保存的 Token 无法解密。
4. 打开 **Settings → 通知设置**，填写 Bot Token 和 Chat ID，点击“测试并启用”。只有测试消息成功后才会启用自动推送；读取设置时后端不会返回 Token 或密文。

启用后，系统会推送新产生的 INFO、WARNING 和 CRITICAL 风险告警，内容包含级别、代码、Cycle（如有）、UTC 时间与消息。启用前及禁用期间的历史告警不会补发。每条告警只尝试一次；Telegram 超时、限流或拒绝时会在设置页记录失败，但不会阻塞交易或自动重试。禁用操作无法撤回已在发送中的请求。

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
cd web && npm test && npm run build
```

## 目录

- `src/GridTrading.Domain`：纯算法、状态机、风险容量、账本和保守回放策略。
- `src/GridTrading.Api`：REST、SignalR、SQLite、Paper 撮合、回放及 Hyperliquid Testnet/Mainnet 交易适配器。
- `tests/GridTrading.Domain.Tests`：算法和安全规则测试。
- `web`：React 控制台。
- `v1`：原始产品、算法、API 与设计规格。

## 安全边界

Paper 撮合仅用于开发验证。Hyperliquid 交易适配器按账户网络锁定官方 HTTPS 端点。Mainnet 支持用户选择的永续合约市场，不设试运行金额、层数或杠杆限制。API Wallet 私钥不会通过 HTTP 接收；V1 的 HTTP 写操作还会拒绝非 loopback 来源。部署时应保护后端环境变量与 `GRID_TRADING_CREDENTIAL_KEY`。Testnet 仍可能产生不可逆的测试资金损失，启动和关闭前请核对实际账户仓位与挂单。
