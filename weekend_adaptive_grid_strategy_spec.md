# 周末自适应网格策略设计建议

**状态：** 研究与离线回测设计  
**目标市场：** BTCUSDT USDT 永续合约（交易所暂定 Bybit）  
**策略类型：** 周末限定、震荡过滤、逐格止盈、Basket 总止盈、库存感知的中性网格  

> 本文档用于研究、回测和后续 paper/testnet 实现，不授权连接真实资金账户或进行实盘交易。

## 1. 核心设计

策略采用“离散重置式自适应网格”：

```text
固定中轴启动一轮 Grid
→ 各网格逐格止盈并重复挂单
→ 整轮 Basket 净利润达到目标
→ 撤单并清空库存
→ 等待 cooldown 并重新检查市场状态
→ 根据最新中轴规划下一轮 Grid
```

本策略有三层退出机制：

1. 单个 lot 的逐格止盈：负责持续收割区间波动。
2. 整轮 Grid 的 Basket 总止盈：负责锁定整轮利润并安全更新中轴。
3. 突破、亏损、库存、断线和时间风控退出：负责控制尾部风险。

一轮 Grid 运行期间原则上不移动中轴。只有在整轮仓位和订单完全归零后，才根据最新市场数据规划下一轮。这可以避免带着旧库存追逐新中轴。

## 2. 策略状态机

```text
OFF
  ↓ 满足交易时段和震荡状态条件
ARMED
  ↓ 网格挂单完成并通过订单对账
RUNNING
  ├─ 市场状态温和恶化 → PAUSED
  ├─ Basket 总止盈      → CLOSING
  ├─ 突破或硬风控       → UNWIND
  └─ 周末交易时间结束   → UNWIND

CLOSING / UNWIND
  ↓ 撤单、处理迟到成交、清空实际仓位
COOLDOWN / LOCKOUT
  ↓ 冷却结束并重新通过状态过滤
ARMED 或 OFF
```

各状态的含义：

- `OFF`：不挂单、不持仓。
- `ARMED`：已完成网格规划，正在建立并核对订单。
- `RUNNING`：允许 entry、逐格 exit 和完成后重新挂回 entry。
- `PAUSED`：停止新增库存，保留合理的减仓/止盈订单。
- `CLOSING`：正常 Basket 止盈退出，禁止任何新 entry。
- `UNWIND`：风险退出，优先降低库存并尽快清仓。
- `COOLDOWN`：正常一轮结束后的短暂等待期。
- `LOCKOUT`：突破或严重异常后的强制禁止重启期。

## 3. 交易时段与 Grid 启动条件

候选交易时段采用 Asia/Singapore 时区：

- 周六约 05:00 开始。
- 周一 05:30 停止新 entry。
- 周一 06:30 前强制清空库存。
- 实际边界应在回测中处理美国夏令时并作为参数比较。

只有满足以下条件时才能启动新一轮 Grid：

1. 当前位于允许交易时段。
2. `ADX(14, 1h) < 20`。
3. 最近 6 小时实现波动率不超过周末滚动基准的 `1.2x`。
4. 最近 1 小时成交量低于滚动中位数的 `1.5x`。
5. 当前价格距离候选中轴不超过约 `1 ATR(1h)`。
6. 没有处于突破后的 lockout。
7. 行情数据、连接、账户和订单状态均正常。

新闻或重大事件过滤可以作为实盘外部开关，但第一版历史回测不应依赖难以复现的主观新闻标签。

启动 Grid 时不使用市价单建立底仓，只创建 post-only 限价 entry。

## 4. Grid 中轴

周末刚开始时，本周末 session VWAP 的样本不足，因此第一轮的启动中轴建议使用：

```text
C0 = 0.70 × VWAP_24h + 0.30 × EMA_24h
```

周末运行一段时间后，下一轮 Grid 可以使用：

```text
C_target = 0.70 × weekend_session_VWAP + 0.30 × EMA_24h
```

基准版本在一轮 Grid 内固定中轴，不进行连续 trailing 或 recenter。

如果后续研究半动态版本，只允许在净库存很轻时调整，并满足：

- 新旧中轴相差至少 `1.75` 个网格间距。
- 价格在候选新中轴附近稳定至少 2 小时。
- ADX 仍低于状态阈值。
- 净库存低于最大库存的 20%。
- 距离上次调整至少 4 小时。

已有 lot 保留原退出目标；不能因为中轴变化而把已有止盈移动得更远。

## 5. Grid 间距和价格

采用对数对称的几何网格：

```text
bid[i] = centre × exp(-i × d)
ask[i] = centre × exp(+i × d)
i = 1...N
```

候选间距：

```text
d = clamp(
    max(
        0.35 × ATR_1h / price,
        3.0 × round_trip_maker_cost
    ),
    0.15%,
    0.40%
)
```

初始研究参数：

- 每边 `N = 7` 层。
- 典型初始间距约 `0.25%`。
- 对应总半宽约 `1.75%`。
- bid 价格按交易所 tick 向下取整。
- ask 价格按交易所 tick 向上取整。
- 正常网格订单使用 post-only。

间距必须使用账户真实费率验证。手续费、资金费、漏单和逆向选择均需要在回测中计入。

## 6. Entry 规则

中轴下方创建 buy entry，中轴上方创建 sell entry：

```text
centre 以下：buy entry → 建立或增加多头库存
centre 以上：sell entry → 减少多头或建立空头库存
```

每条网格线同时最多存在一个有效 entry order。

Entry 成交后：

1. 按实际成交数量创建一个 fill lot。
2. 在相邻的内侧网格线上创建该 lot 的止盈 exit。
3. 该 entry 网格线在 exit 完成前不重复创建完整订单。
4. 部分成交按实际数量建立或累计 exit，不得假定整张订单已经成交。

例子：

```text
centre = 100,000
d ≈ 0.25%

99,501 buy entry → 99,750 sell exit
99,750 buy entry → 100,000 sell exit

100,250 sell entry → 100,000 buy exit
100,501 sell entry → 100,250 buy exit
```

## 7. 单个 lot 的逐格止盈

每个 lot 的生命周期：

```text
ENTRY_PENDING
    ↓ entry 部分或全部成交
OPEN_LOT
    ↓ 在相邻内侧网格线创建 TP
EXIT_PENDING
    ↓ TP 部分或全部成交
CLOSED
    ↓ cycle 仍允许交易
重新挂回原 entry
```

正常逐格止盈规则：

- Long lot 在上一条、更靠近中轴的网格线上卖出。
- Short lot 在下一条、更靠近中轴的网格线上买回。
- Exit 数量等于该 lot 尚未退出的实际数量。
- 正常 exit 优先使用 post-only。
- Exit 不得超过可退出库存，避免平仓单意外变成反向开仓。

逐格 exit 完成后，只有同时满足以下条件才重新创建原 entry：

1. cycle 状态仍为 `RUNNING`。
2. regime gate 未恶化。
3. 没有达到库存限制。
4. 没有进入停止新开仓时间。
5. 同一网格线没有其他有效 entry。

不建议为了库存调整直接把某个 lot 的 exit 数量放大到 125%。库存调整应通过削减新 entry、创建明确的 reduce-only rebalancing order，或风险退出完成。

## 8. Basket 总止盈

从一轮 Grid 启动开始持续计算保守的可清算净利润：

```text
CycleLiquidationPnL
    = 已实现网格利润
    + 当前库存按可成交价格计算的未实现利润
    - 已支付手续费
    - 已支付或应计资金费
    - 预计最终平仓手续费
    - 预计平仓滑点
```

也可以理解为：

```text
CycleLiquidationPnL
    = 假设现在立刻撤单并清仓后的预计账户权益
    - 本轮开始时的账户权益
```

估值要求：

- 平多使用当前 best bid 或更保守的可成交价格。
- 平空使用当前 best ask 或更保守的可成交价格。
- 最终清仓按 taker 费率计提。
- 加入与仓位和盘口深度相关的滑点缓冲。
- 不使用未扣成本的交易所界面 PnL 触发退出。

Basket 目标建议按分配资金比例定义，而不是固定 USDT 金额：

```text
basket_target = allocated_capital × target_return
```

第一轮回测范围：

- `0.05%`
- `0.10%`
- `0.15%`
- `0.20%`
- `0.30%`

建议默认值先使用 `0.15%`，再通过 walk-forward 和样本外数据决定。

### Basket 止盈执行顺序

当 `CycleLiquidationPnL >= basket_target`：

1. 原子性地将 cycle 从 `RUNNING` 切换为 `CLOSING`。
2. 禁止创建、补充或重新挂任何 entry。
3. 撤销所有未成交 entry 和普通逐格 exit。
4. 等待撤单确认并处理撤单期间发生的迟到成交。
5. 从交易所重新读取并对账实际仓位。
6. 使用 reduce-only 订单平掉最终净库存。
7. 必要时使用 taker 确保退出完成。
8. 确认实际仓位为零、活动订单为零。
9. 保存本轮最终净 PnL、费用、滑点、库存和成交记录。
10. 进入 15–30 分钟 cooldown。

Basket 止盈触发后，不能只取消 entry 而保留未知仓位，也不能在撤单尚未确认时立即按旧仓位数量发送平仓单。

## 9. 下一轮 Grid

正常 Basket 止盈并完全清仓后：

1. 等待至少一个完整的 15 分钟 bar，建议 cooldown 为 15–30 分钟。
2. 重新计算 session VWAP、EMA、ATR、spacing 和 centre。
3. 重新执行完整 regime gate。
4. 检查距离周一强制退出是否仍有足够运行时间。
5. 只有全部通过后，创建新的 `cycleId` 并启动下一轮。

每轮 `CycleLiquidationPnL` 可以归零，但以下全局风险指标不能归零：

- 本周末累计净 PnL。
- 本周末最大权益和当前 drawdown。
- 本周末的累计费用和资金费。
- 本周末触发的异常和停止次数。

这可以防止频繁重启 cycle 掩盖整个周末的连续亏损。

## 10. 每条网格线的 lot size

第一版采用固定 USDT 名义价值，而不是固定 BTC 数量，也不采用外层逐步加仓。

设：

```text
E     = 分配给策略的资金
Imax  = 最大单方向净敞口
N     = 每边网格层数
```

初始建议：

```text
Imax = 35% × E
N = 7
base_notional_per_level = Imax / N = 5% × E
```

例如分配资金为 10,000 USDT、BTC 为 100,000 USDT：

```text
最大单方向库存 = 3,500 USDT
每层基础名义价值 = 500 USDT
每层基础数量 = 500 / 100,000 = 0.005 BTC
```

最终数量：

```text
quantity = floor(
    base_notional × inventory_multiplier / order_price,
    exchange_quantity_step
)
```

订单创建前必须再次检查：

- 最小下单数量和最小名义价值。
- 成交后预计净库存不超过 `Imax`。
- 成交后预计毛敞口不超过策略上限。
- 可用保证金能够覆盖订单和风险退出。

不使用马丁格尔，不因为外层价格更远或前一笔亏损而增加基础 lot。

## 11. Inventory skew

定义归一化净库存：

```text
z = net_inventory_notional / Imax
z ∈ [-1, 1]
```

- `z > 0`：净多。
- `z < 0`：净空。

新 entry 的候选尺寸乘数：

```text
buy_multiplier  = clamp(1 - 0.75 × z, 0.50, 1.25)
sell_multiplier = clamp(1 + 0.75 × z, 0.50, 1.25)
```

例如净多已经达到库存上限的 50%，即 `z = 0.5`：

```text
新 buy size  = 62.5% × base size
新 sell size = 125% × base size
```

硬规则：

- `|z| >= 0.75`：禁止继续增加同方向库存。
- 任一订单成交后若预计超过 `Imax`，该订单不得创建。
- `|z| >= 1.0`：停止所有新 entry 并主动降低库存。

基准版本先使用 size skew，不在同一版本中同时加入动态中轴 skew，以便识别收益究竟来自哪项机制。

## 12. 持仓模式

第一版研究建议使用单向净持仓模型：

```text
net_inventory = total_buy_quantity - total_sell_quantity
```

软件内部仍保存每个 fill lot，用于逐格 exit、PnL 归因和审计。

选择单向净持仓的原因：

- 等量多空在经济上互相抵消，但会增加毛敞口和保证金占用。
- 同时持有多空会增加手续费、资金费和订单管理复杂度。
- 单向净库存更适合做统一的 inventory cap 和风险退出。

Hedge mode 可以作为后续独立实验，但不应在第一版同时实现两套持仓语义。

## 13. 市场状态恶化与风险退出

### 13.1 温和恶化

当 ADX、波动率或成交量开始放大，但尚未确认突破：

- 状态切换到 `PAUSED`。
- 撤销会增加同方向库存的新 entry。
- 保留合理的逐格止盈或减仓订单。
- 已完成一轮的 entry 暂不重新挂回。
- 状态持续恢复后才能重新进入 `RUNNING`。

### 13.2 突破退出

如果 1 小时收盘突破最外层网格，并且至少有一个确认条件：

- 成交量超过滚动中位数的 `1.5x`。
- ADX 高于约 25。
- 4 小时持仓量变化超过约 5%。
- 外部重大事件开关触发。

则：

1. 进入 `UNWIND`。
2. 撤销所有普通 Grid 订单。
3. 对账实际仓位。
4. 使用 reduce-only 快速减仓并平仓。
5. 至少 6 小时内禁止自动重启。

灾难价格止损候选值：

```text
outer_boundary ± 0.6 × ATR_1h
```

灾难止损、数据断线、订单对账失败和库存硬上限不需要等待 1 小时收盘确认。

### 13.3 Basket 止损

Basket 总止盈不能替代亏损控制。第一轮回测候选范围：

```text
basket profit target：+0.10% 至 +0.20% allocated capital
basket stop loss：     -0.40% 至 -0.80% allocated capital
```

确认突破时应在触及完整 basket stop 之前退出。Basket stop 是最后一道账户级保护，不是主要突破识别方法。

### 13.4 时间退出

建议分阶段退出：

- 周一 05:30：停止所有新 entry。
- 05:30–06:15：使用较积极的限价单降低库存。
- 06:15–06:30：退出价格逐步靠近盘口。
- 06:30：剩余库存使用 taker 强制清仓。

回测需要计入最终强制退出的手续费和滑点。

## 14. 初始 v1 参数建议

| 项目 | 初始值 |
|---|---:|
| Instrument | BTCUSDT USDT perpetual |
| Position mode | 单向净持仓 |
| Grid levels | 每边 7 层 |
| Spacing | 动态，典型约 0.25% |
| 单层基础名义价值 | 分配资金的 5% |
| 最大净库存 | 分配资金的 35% |
| 最大毛敞口 | 分配资金的 70% |
| Centre | 每轮启动时固定 |
| Normal lot exit | 相邻内侧网格 |
| Re-entry | lot exit 完成后挂回原 entry |
| Basket profit target | 分配资金的 0.15% |
| Basket stop loss | 研究范围 0.40%–0.80% |
| Normal cooldown | 15–30 分钟 |
| Breakout lockout | 至少 6 小时 |
| Effective leverage | 初始 1x |
| Martingale | 禁止 |

所有参数均为待回测假设，不是已验证的盈利参数。

## 15. 回测时必须比较的策略版本

为了判断每个机制是否真正增加收益，应至少比较：

1. 纯 Basket Grid：entry 每条线只成交一次，没有逐格 exit，Basket 达标后整体退出。
2. 逐格止盈 Grid：逐格 exit 并重新挂 entry，没有 Basket 总止盈。
3. 逐格止盈 + Basket 总止盈。
4. 第 3 项加周末时间过滤。
5. 第 4 项加 regime gate。
6. 第 5 项加 inventory size skew。
7. 完整版本加突破 kill switch 和强制时间退出。

回测必须防止：

- 使用未来 K 线信息决定当前订单。
- 假设价格触碰限价就必然成交。
- 同一 OHLC bar 内发生不可能的 entry/exit 顺序。
- 撤单与迟到成交造成仓位遗漏。
- 订单数量取整后突破库存上限。
- Basket PnL 重复计算 realized 和 unrealized PnL。
- cycle 重置掩盖整个周末的累计亏损。

## 16. 尚需通过回测确定的参数

- Basket target 最佳范围及是否需要随波动率变化。
- Basket stop 和突破退出的相对优先级。
- 相邻一格止盈是否优于两格止盈或分批止盈。
- cooldown 使用固定时间还是等待完整状态恢复。
- 每层固定名义价值与距离递减 sizing 的差异。
- Inventory skew 的系数和触发阈值。
- 一轮 Grid 的最长允许运行时间。
- 在盘口和逐笔数据条件下的保守 maker fill 模型。
- 单向净持仓与 hedge mode 的实际收益和成本差异。

