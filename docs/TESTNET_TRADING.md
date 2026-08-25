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
3. 保存策略，在控制台点击“确认预览并开启”。后端会再次检查 Agent 授权、测试资金、实际仓位为零和无残留挂单，然后才提交 Entry。
4. Cycle 运行后，成交由后台按策略的对账间隔（默认 10 秒）同步；Entry 成交会按实际成交价和数量创建 reduce-only TP，TP 完成后再回补对应层。
5. “暂停 Entry”只撤 Entry，保留已有 TP；“继续”恢复缺失层；“关闭 Cycle”先撤全部策略单，再用 reduce-only IOC 清零实际仓位。若仍有残余仓位，Cycle 会保持非终态并报错，绝不会假装关闭成功。
6. Basket TP/SL 阈值目前在 Testnet 只用于监控，达到阈值不会自动发出真实清仓指令；请使用“关闭 Cycle”人工确认。若要启用自动阈值清仓，需要另行明确授权。

## Nonce 与安全边界

- nonce 由后端按 API Wallet 签名地址串行生成并先持久化，值为 `max(当前毫秒时间, 上次 nonce + 1)`；用户无需在界面填写。
- 每个请求使用稳定 CLOID，成交按交易所 execution identity 去重。
- 签名域、Info、Exchange URL 都固定为官方 Testnet；配置成其他主机或 Mainnet 会被拒绝。
- 项目不会接收“通过浏览器提交 API 私钥”的请求，也没有 Mainnet 下单路径。
- V1 没有远程用户登录体系，因此所有 HTTP 写操作只接受 loopback 来源；远程部署前必须另行设计认证与授权。
