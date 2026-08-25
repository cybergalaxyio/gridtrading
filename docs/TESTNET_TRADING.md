# Hyperliquid Testnet 交易配置

本项目只允许 Hyperliquid 官方 Testnet。API Wallet（Agent Wallet）负责对交易动作签名；余额、仓位、挂单和成交查询始终使用主账户公开地址。API Wallet 没有提现权限，但仍应按敏感密钥管理。

## 1. 准备账户

1. 在 Hyperliquid Testnet 创建或导入主账户并领取测试资金。
2. 在 Testnet 的 API Wallet 页面创建并授权 Agent Wallet。
3. 保存主账户公开地址和 Agent Wallet 私钥。不要把私钥写入项目文件、浏览器或聊天记录。

## 2. 启动后端
项目根目录已提供 git-ignored `.env` 和 `run-testnet.sh`。在本机编辑 `.env`，填写主账户公开地址和 API Wallet 私钥后运行：

```bash
./run-testnet.sh
```

脚本会把 `.env` 加载到后端进程，并在仍有占位值时拒绝启动。下面仍保留手动配置环境变量的方式。

生成用于本地静态加密的 32-byte master key：

```bash
openssl rand -base64 32
```

在启动后端的同一个终端设置环境变量：

```bash
export GRID_TRADING_CREDENTIAL_KEY='<上一步生成的 base64 值>'
export GRID_TRADING_HL_TESTNET_ACCOUNT_ADDRESS='0x主账户公开地址'
export GRID_TRADING_HL_TESTNET_AGENT_PRIVATE_KEY='0xAPI_WALLET_PRIVATE_KEY'

dotnet run --project src/GridTrading.Api/GridTrading.Api.csproj --urls http://localhost:5050
```

使用 Vault 时还可以设置：

```bash
export GRID_TRADING_HL_TESTNET_VAULT_ADDRESS='0xVault地址'
```

后端会派生 Agent 公共地址，将私钥用 AES-256-GCM 加密后保存到 SQLite。API 与前端只返回公开地址。更换 `GRID_TRADING_CREDENTIAL_KEY` 前，应先保留旧值或删除并重新建立本地 Testnet 数据库，否则旧密文无法解密。

## 3. 用户操作

1. 打开“设置 → 交易所账户”，确认 Testnet 显示“可交易”。
2. 新建策略时选择 `Hyperliquid Testnet` 账户，点击“获取建议”读取真实 Testnet 盘口。
   Dashboard 会通过官方 `allMids` WebSocket 实时更新当前选中 Symbol 的价格；切换 Symbol 时会自动更换 SignalR 订阅。
3. 保存策略，在控制台点击“确认预览并开启”。后端会再次检查 Agent 授权、测试资金、实际仓位为零和无残留挂单，然后才提交 Entry。
4. Cycle 运行后，后端通过官方 Testnet WebSocket `userFills` 订阅实时接收成交；Entry 成交后立即按实际成交价和数量提交一张反方向普通 TP Limit。Entry 与 TP 都是非 reduce-only 普通 Limit。
5. 系统按当前中间价在冻结 Grid 中选择最近的下方 BUY 与上方 SELL，各维持一张未成交 Entry；已有未关闭 Lot 的层不会重复挂 Entry，越过最外层后停止该方向补单。TP 完成后释放原层，并在处理成交后按当前价格重新选层。
6. “暂停 Entry”只撤 Entry，保留已有 TP；“继续”按当前价格恢复工作 Entry；“关闭 Cycle”先撤全部策略单，再用非 reduce-only IOC Limit 尝试清零实际仓位。若仍有残余仓位，Cycle 会保持非终态并报错，绝不会假装关闭成功。
7. Basket TP/SL 阈值目前在 Testnet 只用于监控，达到阈值不会自动发出真实清仓指令；请使用“关闭 Cycle”人工确认。若要启用自动阈值清仓，需要另行明确授权。

## Nonce 与安全边界

- nonce 由后端按 API Wallet 签名地址串行生成并先持久化，值为 `max(当前毫秒时间, 上次 nonce + 1)`；用户无需在界面填写。
- 每个请求使用稳定 CLOID，成交按交易所 execution identity 去重。
- WebSocket 每 30 秒发送官方应用层 ping，断线后按指数退避自动重连；重连 snapshot 与 REST Sync 都通过相同 execution identity 去重。
- `allMids` 行情在后端按 Dashboard 当前选中的 Symbol 转发；标题价格与当前 K 线实时更新，REST book/candle snapshot 仍作为首屏和断线兜底。
- REST Sync（默认 10 秒）继续校验 fills、Open Orders 和实际持仓，用于启动/断线恢复及漏消息兜底，不是实时成交的主路径。
- 签名域、Info、Exchange URL 都固定为官方 Testnet；配置成其他主机或 Mainnet 会被拒绝。
- 项目不会接收“通过浏览器提交 API 私钥”的请求，也没有 Mainnet 下单路径。
- V1 没有远程用户登录体系，因此所有 HTTP 写操作只接受 loopback 来源；远程部署前必须另行设计认证与授权。
