# Hyperliquid Testnet 交易配置

本文适用于 Hyperliquid 官方 Testnet。Mainnet 使用独立配置，见 [MAINNET_TRADING.md](MAINNET_TRADING.md)。API Wallet（Agent Wallet）负责对交易动作签名；余额、仓位、挂单和成交查询始终使用主账户公开地址。API Wallet 没有提现权限，但仍应按敏感密钥管理。

## 1. 准备账户

1. 在 Hyperliquid Testnet 创建或导入主账户并领取测试资金。
2. 在 Testnet 的 API Wallet 页面创建并授权 Agent Wallet。
3. 保存主账户公开地址和 Agent Wallet 私钥。仅在本机门户的账户表单中输入 API Wallet 私钥；不要写入代码或聊天记录。

## 2. 启动后端
后端只需配置固定的 `GRID_TRADING_CREDENTIAL_KEY`（base64 编码的 32 字节密钥）。新安装可用 `openssl rand -base64 32` 生成；已有安装必须保留原值。

可在 `.env` 中配置加密密钥，也可从进程环境传入，然后运行 `./run-testnet.sh`，打开 <http://localhost:5050>。地址和 API Wallet 私钥无需写入环境文件。

在 **设置 → 交易所账户 → 添加账户** 选择 Testnet，填写名称、主账户公开地址和已授权的 API Wallet 私钥。保存后账户默认禁用；点击“测试连接”检查授权和余额，再点击“测试并启用”。可重复添加多个账户，并同时运行不同账户的策略。

密钥只在保存时发送至本机后端，以 AES-256-GCM 加密存入 SQLite；读取账户不会返回私钥或密文。旧环境变量只用于一次性导入缺失账户，不再覆盖网页配置。操作和迁移详情见 [多账户配置](ACCOUNTS.md)。此版本不增加 Vault / 子账户的网页配置入口，已有历史配置保留。

## 3. 用户操作

1. 打开“设置 → 交易所账户”，确认 Testnet 显示“可交易”。
2. 新建策略时选择 `Hyperliquid Testnet` 账户，点击“获取建议”读取真实 Testnet 盘口。
   Dashboard 会通过官方 `allMids` WebSocket 实时更新当前选中 Symbol 的价格；切换 Symbol 时会自动更换 SignalR 订阅。
3. 保存策略，在控制台点击“确认预览并开启”。后端会再次检查 Agent 授权、测试资金、实际仓位为零和无残留挂单，然后才提交 Entry。
4. Cycle 运行后，后端通过官方 Testnet WebSocket `userFills` 订阅实时接收成交；Entry 成交后立即按实际成交价和数量提交一张反方向普通 TP Limit。Entry 与 TP 都是非 reduce-only 普通 Limit。
5. 系统按当前中间价在冻结 Grid 中选择最近的下方 BUY 与上方 SELL，各维持一张未成交 Entry；已有未关闭 Lot 的层不会重复挂 Entry，越过最外层后停止该方向补单。TP 完成后释放原层，并在处理成交后按当前价格重新选层。
6. “暂停 Entry”只撤 Entry，保留已有 TP；“继续”按当前价格恢复工作 Entry；“关闭 Cycle”先撤全部策略单，再用非 reduce-only IOC Limit 尝试清零实际仓位。若仍有残余仓位，Cycle 会保持非终态并报错，绝不会假装关闭成功。
7. 当前代码会在对账时检查 Basket TP/SL，并在触发阈值时自动关闭 Cycle。此功能需要后端及交易所连接持续可用；不是交易所托管止损。

## Nonce 与安全边界

- nonce 由后端按 API Wallet 签名地址串行生成并先持久化，值为 `max(当前毫秒时间, 上次 nonce + 1)`；用户无需在界面填写。
- 每个请求使用稳定 CLOID，成交按交易所 execution identity 去重。
- WebSocket 每 30 秒发送官方应用层 ping，每个账户独立断线重连；重连 snapshot 与 REST Sync 都通过相同 execution identity 去重。
- `allMids` 行情在后端按 Dashboard 当前选中的 Symbol 转发；标题价格与当前 K 线实时更新，REST book/candle snapshot 仍作为首屏和断线兜底。
- REST Sync（默认 10 秒）继续校验 fills、Open Orders 和实际持仓，用于启动/断线恢复及漏消息兜底，不是实时成交的主路径。
- 签名域、Info、Exchange URL 都固定为官方 Testnet；配置成其他主机或 Mainnet 会被拒绝。
- 本机账户写入接口要求 loopback 来源、可信 Host / Origin 和 JSON 请求；Mainnet 仍由用户点击 Start 启动。
- V1 没有远程用户登录体系，因此所有 HTTP 写操作只接受 loopback 来源；远程部署前必须另行设计认证与授权。
