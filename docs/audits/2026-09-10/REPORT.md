# 本轮 Order History 策略执行审计

结论：**核心网格下单与止盈行为基本符合预期，但整体运行不能验收通过。TP 数量账本失真导致一次可复算的 FAULT 误触发，cycle 利润与手续费汇总也有差异。**

本次是只读审计。没有改动运行代码、服务状态、数据库或交易所订单。离线复核共 17 项：12 项通过、5 项失败；这些是同一轮历史的检查项，不代表策略测试覆盖率或盈利能力。

## 范围与预期

- Cycle：`cycle_9030f8e838b8417587f662ed9d926cb4`，Hyperliquid Testnet / SOL。
- 启动：2026-08-30 08:49:45 UTC。SQLite 一致性快照：2026-09-10 09:04:41 UTC；交易所快照约 09:05:17 UTC。
- 本地：85 张逻辑订单、82 笔 executions、31 个 virtual lots、251 条 funding。
- 官方 Testnet `/info`：`historicalOrders`、`userFillsByTime`、`openOrders`、`clearinghouseState`、`userFunding`。按稳定 CLOID 关联，85 张本地订单全部找到，获得本轮 196 条订单历史事件和 82 笔成交。改单产生多个 OID，不把它们当成重复开仓。
- 预期以本轮 **FrozenConfiguration / FrozenPlan** 及当前 V1 规则为准；不使用根目录较早的“周末自适应网格研究建议”作为已上线功能要求。

| 参数 | 本轮冻结值 |
|---|---:|
| 中心 | 104.41 |
| 每侧层数 / 活动 Entry 上限 | 14 / 1 |
| Tick / Quantity step | 0.01 / 0.01 |
| 首层距中心 | 0.25 |
| 网格间隔 | 50 points = 0.50 |
| TP 距离 | 200 points = 2.00 |
| 第 i 层数量，i 从 0 开始 | floor(0.2 × 1.162^i / 0.01) × 0.01 |
| 最大净仓位 | 10 SOL |
| 部分成交撤余单时间 | 10 分钟 |
| FAULT 未保护敞口阈值 | 10 USD |
| Basket TP / SL | 50 / 0，SL 关闭 |

买网格 `104.16 − 0.50×i`，卖网格 `104.66 + 0.50×i`。TP 基于实际成交价加减 2.00；原层 lot 完整止盈后，该层重新具备 Entry 资格，实际工作层还取决于市价。

## 已验证正常的行为

| 检查 | 结果 |
|---|---|
| 冻结网格公式 | 28 层价格与数量全部正确 |
| Entry 价格 / 数量 | 55 张全部符合冻结计划，未发现重复交易后加大同层仓量 |
| 交易所与本地 executions | 82/82 的 ID、方向、价格、数量、手续费逐笔一致，无缺失或重复 |
| TP 方向 / 价格 | 30 张 TP 方向正确，价格符合实际 entry 价 ±2.00；成交价均满足限价 |
| lot 数量守恒 | 31/31 正确；30 个已关闭，余 1 个 0.03 SOL |
| 每侧活动 Entry | 重放交易所 open / terminal 事件，买侧峰值 1，卖侧峰值 1 |
| 占层与重入 | 未发现原 lot 尚未退出就重新创建同侧同层 Entry；B13 多次重复交易均保持计划数量 1.40 |
| 仓位及活动订单预留 | 实际净仓峰值 6.17 SOL；含 Entry 和 TP 的最坏买侧场景 6.17、卖侧场景绝对值 1.01，均低于 10 |
| 部分成交超时 | B13 / OID `59126979354` 成交 0.65/1.40，首笔成交后 601.759 秒撤余单，符合 10 分钟规则与同步延迟 |
| FAULT 后停止 Entry | 最后一次创建 Entry 为 09-04 15:55:26；FAULT 后没有新增 Entry |
| 当前净仓位 | 逐笔重建、本地两列仓位、交易所均为 +0.03 SOL；交易所活动订单 0 |

81 笔为 maker、1 笔为 taker。该 taker 是买入 TP（OID `59119207923`），成交价 100.38 优于其限价 103.16；符合 TP post-only 被拒后使用普通限价重试的现有实现。未发现 Entry 使用 taker 成交。

## 发现 1：TP 数量失真，导致 FAULT 误触发（高优先级）

8 张 TP 的本地 `Quantity` / `FilledQuantity` 小于实际 executions 合计。交易所确实完成了扩量改单，且最终全部成交；问题发生在本地订单账本。

| 最终 TP OID | 层 | 本地 Quantity / FilledQuantity | 交易所最终 origSz / executions 合计 |
|---|---|---:|---:|
| 59119485095 | B8 | 0.39 | 0.66 |
| 59125950087 | B10 | 0.15 | 0.89 |
| 59200928612 | B6 | 0.17 | 0.49 |
| 59226002871 | B3 | 0.15 | 0.31 |
| 59301476024 | B4 | 0.11 | 0.36 |
| 59301543794 | B5 | 0.10 | 0.42 |
| 59302302582 | B6 | 0.40 | 0.49 |
| 59314509821 | B5 | 0.11 | 0.42 |

**09-04 17:52:10.224 UTC 的 FAULT 可以精确复算：**

| 当时尚未退出的 lot | 剩余数量 | 本地 TP 保护量 | 交易所实际 TP 保护量 | 本地多计未保护金额 |
|---|---:|---:|---:|---:|
| B4，TP 104.16 | 0.36 | 0.11 | 0.36 | (0.36−0.11)×104.16 = 26.0400 |
| B5，TP 103.66 | 0.42 | 0.11 | 0.42 | (0.42−0.11)×103.66 = 32.1346 |
| B6 新部分成交，目标 TP 103.16 | 0.03 | 0 | 0 | 实际未保护 3.0948 |

其余 B0–B3 未退出 lot 当时有完整 TP。交易所 B4 / B5 改单历史分别证明已扩至 0.36 / 0.42，且直到次日才成交。因此实际未保护敞口只有 **3.0948 USD**，低于阈值 10；本应警告并维持 RUNNING。本地却算出：

`26.0400 + 32.1346 + 3.0948 = 61.2694 USD`，恰好对应告警中的 **61.27 USD**，继而进入 FAULT，撤销两侧活动 Entry。

B6 当时名义价值小于最低下单额，无法独立建 TP 本身符合既定小额容忍规则；错误在于把另两张已经受到保护的 lot 算成了未保护。

代码关联：

- `src/GridTrading.Api/Exchanges/Hyperliquid/HyperliquidExecutionAdapter.cs:128`：改单成功后应保存新的 Quantity；历史表明这一结果没有可靠地体现在持久账本中。
- `src/GridTrading.Api/Strategies/Grid/GridOrderLifecycle.Core.cs:253`：未保护金额依赖本地 `Quantity − FilledQuantity`，失真会直接进入风控决策。
- `src/GridTrading.Api/Strategies/Grid/GridOrderLifecycle.cs:175`：成交累计被 `Math.Min(order.Quantity, ...)` 截断，使错误 Quantity 进一步污染 FilledQuantity。

**后续根因定位：单笔 modify 的成功响应被错误地当成 order 响应解析。**

`HyperliquidTradingClient.ModifyLimitAsync`（约第 116 行）发送 `type=modify`，却复用 `SendOrderAction`（第 230 行）。后者固定读取：

```csharp
result.RootElement.GetProperty("response").GetProperty("data").GetProperty("statuses")
```

单笔 modify 的成功 ACK 可为 `{"status":"ok","response":{"type":"default"}}`，不带 `data.statuses`。对应协议类型可见 [nktkas SDK 的 modify 实现及响应定义](https://github.com/nktkas/hyperliquid/blob/main/src/api/exchange/_methods/modify.ts)；[官方 Python SDK](https://github.com/hyperliquid-dex/hyperliquid-python-sdk/blob/master/hyperliquid/exchange.py) 的 `modify_order` 则走 `batchModify`，不能将单笔 modify 与批量改单的响应格式混用。

因果链：

1. 第一段成交 0.11，创建 0.11 TP。
2. 后续 Entry 分段成交入库，lot 增至 0.36 / 0.42；`AddFillToTakeProfitAsync` 在请求改单前已调用 `SaveChangesAsync`。
3. 交易所接受扩量改单，但返回 `type=default`；`GetProperty("data")` 抛 `KeyNotFoundException`。
4. 调用无法返回到 `AmendOrderAsync` 的 `order.Quantity = quantity` 和保存语句，本地 TP 仍是 0.11。异常也不是只捕获的 `PROTECTIVE_ORDER_REJECTED`，因此没有直接走保护失败告警分支。
5. 同一成交再由 REST / WebSocket 到达时，被已持久化的 Execution ID 去重过滤，不会重做保护改单后的保存。
6. 后续 order updates 能更新 OID / Status，但不更新 Quantity；REST 对账只提取活动单 CLOID/OID，也不修正真实数量。
7. 下一笔无法独立建立 TP 的小额成交触发保护检查，错误保护量被放大成 61.2694，导致 FAULT。

这也解释了 8 张异常 TP 为什么都停在**首次成功建 TP 时的数量**：B8 第一段 0.06 尚未建单，累计到 0.39 才首次建 TP，所以本地停在 0.39；其余对应首次建单量 0.15 / 0.17 / 0.11 / 0.10 / 0.40 等。

隔离复现使用当前源码、实际 `HyperliquidExecutionAdapter` / `HyperliquidTradingClient` / `GridOrderLifecycle`、内存 SQLite 和拦截所有 HTTP 的 handler，没有访问交易所或运行数据库：

- `0.11 → 0.36` + default ACK：稳定抛上述异常，重开 DbContext 仍读到 TP=0.11、lot=0.36；重复成交不会修复。
- `0.11 → 0.42` + default ACK：相同结果。
- 对照组 `0.11 → 0.36` + order/statuses ACK：Quantity 正常保存为 0.36。

**3/3 复现断言通过**，它们断言当前缺陷及对照行为，不代表缺陷已经修复。保存了 `ModifyAcknowledgementReproTests.cs` 和 `root-cause-test-output.txt`。没有拿到当时的原始 HTTP 响应日志；历史归因来自协议定义、全部 8 张 TP 的一致模式和确定性调用链复现，而非声称读取到了当时的响应包。此前把数量问题列为“可能并发覆盖”只是假设，本次已定位到更直接、可复现的协议解析错误；利润/手续费累计差异仍独立调查。

## 发现 2：cycle 汇总利润和手续费不能由逐笔记录复算（高优先级）

| 指标 | 逐笔 / lot 复算 | Cycle 保存值 | 少记 |
|---|---:|---:|---:|
| 已实现网格归因毛利 | 33.319400 | 32.919400 | 0.400000 |
| 成交手续费 | 0.502115 | 0.495912 | 0.006203 |
| 本地 funding cost 合计 | 0.233637 | 0.233637 | 0 |

利润使用每个 lot 的实际 Entry 均价与关联 TP 成交复算；手续费同时与交易所 82 笔成交、各 lot 的 EntryFee + ExitFee 一致。所以不是漏拉成交，而是 cycle 累计值与明细脱节。

`GridOrderLifecycle.cs:178` 与 `GridOrderLifecycle.Core.cs:281` 采用内存读改写累加。`GridReconciliationService.cs:30` 在账户锁之前读取跟踪的 Cycle，随后将该实例交给 Reconcile；这是需要确定性并发测试检查的旧实体覆盖风险。它是候选原因，不是本次已经复现的结论。

另须区分“lot 归因毛利”和“单向净持仓的账户已实现利润”。交易所 `closedPnl` 合计 33.339635 与 lot 毛利口径不同，不把这 0.020235 差异判成交易错误。V1 明确要求 Basket 使用成交现金流加实际净仓估值：本轮未扣费现金流为 30.284600，另有 +0.03 SOL；不可只把 lot 毛利与交易所净仓 UPNL 混合，或把页面累计值当成已经验证的清算收益。本次未验证历史 Basket 阈值是否曾被触及。

Funding 补充：官方 REST 中 08-31 至 09-02 返回按日汇总，本地保留小时明细；按日合计后，08-31 至 09-10 均一致。08-30 在当前 REST 返回中缺失，因此当日 -0.007084 只验证了本地汇总守恒，没有宣称完整外部复核通过。

## 发现 3：FAULT 后停止周期性 REST 对账（中优先级）

- 最后 `LastReconciledAt` 为 **2026-09-04 17:52:02 UTC**，早于误触发。
- 至审计时仍为 FAULT，约 5 天 15 小时没有更新该时间。
- 已有 TP 继续在交易所执行，最后成交为 **09-06 03:37:25.550 UTC**；WebSocket 仍接收到了成交与后续 funding。
- `GridReconciliationService.cs:30` 只选择 RUNNING / PAUSED / CLOSING，排除了非终态 FAULT。

所以“服务进程还在”和“策略持续运行”是两回事。当前 0.03 SOL 恰好能对齐，并不能说明后台对账仍健康；FAULT 期间若 WebSocket 漏消息，就缺少正常 REST 恢复路径。建议 FAULT 继续只读仓位、订单、成交和费用对账，同时保持禁止新增 Entry。

## 验证范围与修复顺序

1. 首先分离单笔 modify 与新下单响应解析，正确处理 default ACK，并通过 CLOID 查询确认新订单的 OID、价格和数量后持久化；或者按官方 SDK 使用 batchModify 并明确验证其响应。补上“成交已入库但改单后处理失败”的可恢复状态，以及真实响应格式测试。对账必须核验价格、数量、剩余量，不只是订单存在性。
2. 使 Cycle 金额能从去重后的明细确定性重建，验证锁内加载与事务边界；Basket 按现金流和实际净仓估值核对。
3. 给非终态 FAULT 保留只读对账和陈旧状态提示。
4. 修复后再评估本轮账本修复和恢复运行；本次审计没有执行恢复命令。

没有历史逐时盘口和完整状态转移日志，不能严格证明每次滚动选择的都是当时最近合法层，也不能评估整个期间的市场数据新鲜度、全部下单延迟、Basket 阈值、暂停/恢复或紧急退出。本轮只出现了部分成交超时、小额保护告警、误触发 FAULT；未触发的分支不计作验收通过。运行进程的可执行文件及 DLL 映射显示 `(deleted)`，无法仅凭当前工作区 HEAD 证明运行程序集完全相同，因此代码行号仅定位当前源码，历史结论以实际订单和持久数据为准。

## 离线复核

同目录交付：

- `evidence.json`：本轮本地一致性快照和过滤后的交易所只读返回；不含私钥、密钥或账户凭据。
- `orders.csv`：85 张订单逐行核对，8 张异常 TP 可直接筛选 `quantity_check=FAIL`。
- `verify.py`：仅 Python 标准库，Decimal 精确计算，不接触网络、服务或运行数据库。
- `results.json`：17 项检查的结果和异常明细。

```bash
python3 docs/audits/2026-09-10/verify.py
```

预期退出码 **1**，因为捕获的交易状态有 5 个失败检查；不是脚本执行失败。本次验证采用真实订单事件的离线重建，并追加了 3 个真实适配器、模拟网络响应的根因复现用例。没有用现有单元测试通过来替代运行历史验证。
