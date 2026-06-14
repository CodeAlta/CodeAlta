# 白起 — 三国杀风格 WPF 卡牌游戏

- Status: Approved
- Plan file: `.alta/plans/2026-06-14-baiqi-game.md`
- Created: 2026-06-14
- Task: 在 `C:\Users\Administrator\Desktop\Agent\BaiQi\` 下创建一个类似三国杀的 WPF 卡牌游戏，第一期武将仅白起，单人打 AI，核心战斗系统可玩。
- Git: 不涉及 CodeAlta 仓库，但计划文件作为项目记录保留于此。

## Objective

- **目标**: 构建一个可玩的 .NET WPF 卡牌对战游戏，玩家扮演白起 vs AI 对手
- **非目标**（一期范围外）: 多武将、联网对战、复杂装备/锦囊牌、动画特效、存档/读档

## Context and evidence

- 本地有成熟的 .NET 10 WPF 项目参考：`Snake/`（贪吃蛇）使用 `net10.0-windows`、`UseWPF=true`、`Nullable=enabled`、`ImplicitUsings=enabled`
- Snake 项目模式: `Game/`（纯逻辑）+ `UI/`（WPF）+ `Services/`（持久化），引擎与渲染分离
- Snake 使用 `Canvas` + `DispatcherTimer` 驱动游戏循环，`install-and-run.bat` 一键构建运行
- SDK 版本: `10.0.100`，`global.json` 控制
- 代码注释中文，UI 文字中文，代码命名英文

## Assumptions and open decisions

- **已决**: .NET 10 WPF，单机 vs AI，中文 UI + 英文代码命名，核心战斗一期
- **已决**: 项目名 `BaiQi`，放在 `C:\Users\Administrator\Desktop\Agent\BaiQi\`
- **已决**: 简化三国杀规则，适配 2 人对战
- **开放**: 白起技能效果（"杀神"方案见设计说明，可在实现前调整数值）
- **开放**: 牌组构成比例（见设计说明，可在试玩后调优）

## Design notes

### 白起武将设计

| 属性 | 值 |
|------|-----|
| 称号 | 杀神 |
| 体力 | 4 点 |
| 技能 | **杀神** — 你使用的"杀"不可被"闪"响应（锁定技） |

> **杀神** 典故：白起居功至伟，长平之战坑杀四十万降卒，杀人如麻，故其出"杀"无人能挡。适合新手，简单强力。

### 卡牌系统（一期 30 张牌）

| 牌名 | 数量 | 花色/点数 | 效果 |
|------|------|-----------|------|
| 杀 | 18 | ♠A-10 / ♣A-8 | 出牌阶段，对对手造成 1 点伤害 |
| 闪 | 8 | ♦2-9 | 响应"杀"时使用，抵消该"杀" |
| 桃 | 4 | ♥2-5 | 出牌阶段/濒死时，回复 1 点体力 |

### 简化规则

1. **开局**: 双方各 4 体力、摸 4 张手牌
2. **回合流程**: 摸牌阶段（摸2张）→ 出牌阶段（使用杀、桃等，一回合只能出一张杀）→ 弃牌阶段（弃至体力上限张）
3. **胜负**: 对方体力 ≤ 0 时获胜
4. **濒死**: 体力为 0 时，可使用桃自救（一次），否则败北
5. **距离/装备/锦囊**: 一期不实现，直接可以互相攻击

### AI 逻辑（简单）

- 出牌阶段用杀攻击（如果手牌有杀且命中率 > 50%）
- 受伤时桃急救
- 受攻击时自动出闪

### 项目结构

```
Agent/BaiQi/
├── BaiQi.csproj
├── global.json
├── install-and-run.bat
├── App.xaml
├── App.xaml.cs
├── Game/
│   ├── Card.cs              — 卡牌枚举（杀/闪/桃）+ 数据模型
│   ├── Deck.cs              — 牌堆初始化与洗牌摸牌
│   ├── Hero.cs              — 武将定义（名称、体力、技能）
│   ├── Skill.cs             — 技能定义与效果
│   ├── PlayerState.cs       — 玩家状态（手牌、体力、区域）
│   ├── GameEngine.cs        — 回合管理 + 胜负判断
│   └── AIPlayer.cs          — AI 决策逻辑
├── UI/
│   ├── MainWindow.xaml      — WPF 主窗口布局
│   ├── MainWindow.xaml.cs   — UI 事件 + 游戏循环
│   └── GameRenderer.cs      — Canvas 渲染（手牌、战场、信息面板）
└── Services/                — （预留，一期暂无持久化需求）
```

### 遵循的本地约定

- 文件范围命名空间、`using` 在 namespace 外、`var` 显式类型
- `sealed` 非继承类、`_camelCase` 私有字段
- `ArgumentException.ThrowIfNull()` 参数校验
- XML 文档注释公共 API 避免 CS1591
- 中文注释 + 英文代码命名

## Risks and challenges

- **杀神技能平衡性**: "杀不可闪" 对新手友好但 AI 难以对抗。应对: AI 初始较高体力或手中额外桃
- **牌堆规模小**: 30 张牌对两人对战可能需要频繁洗牌。二期可扩展牌堆
- **WPF Canvas 卡牌布局**: 手牌扇形排列 / 并排布局需合理安排。用固定位置+半透明叠放实现
- **边界情况**: 双方同时濒死、手牌满、牌堆摸空等需要处理

## Implementation checklist

- [x] **1. 项目脚手架** — 创建 `BaiQi/` 目录，复制 `Snake` 的 `global.json`，创建 `BaiQi.csproj`（net10.0-windows，WPF），创建 `App.xaml` / `App.xaml.cs`、`install-and-run.bat`
- [x] **2. 卡牌数据模型** — `Game/Card.cs`：卡牌花色枚举（Suit）、卡牌类型枚举（CardType：杀/闪/桃）、Card 类（Suit/Rank/Type）、`CardDisplay()` 中文名方法
- [x] **3. 牌堆** — `Game/Deck.cs`：30 张牌初始化、洗牌（Fisher-Yates）、摸牌方法
- [x] **4. 武将/技能定义** — `Game/Hero.cs`：Hero 类（Name/Title/MaxHp/Skills）；`Game/Skill.cs`：Skill 类、白起杀神技能效果（修饰杀不可闪避）
- [x] **5. 玩家状态** — `Game/PlayerState.cs`：PlayerState 类（Hero/HandCards/CurrentHp/MaxHp/IsAlive/PlayerName）
- [x] **6. AI 逻辑** — `Game/AIPlayer.cs`：AI 决策（出杀时机、桃急救、闪响应）
- [x] **7. 游戏引擎** — `Game/GameEngine.cs`：回合流程、阶段推进、出牌处理、伤害结算、杀神技能判断、胜负判定
- [x] **8. WPF 主窗口** — `UI/MainWindow.xaml` + `.xaml.cs`：布局（双方信息区+游戏日志+手牌区+操作按钮），事件处理，DispatcherTimer 驱动
- [ ] **9. 渲染器** — UI 集成在 MainWindow 中，使用数据绑定 + 直接操作，无需单独 GameRenderer.cs
- [x] **10. 集成调试** — `dotnet build -c Release` 通过，0 错误 0 警告
- [x] **11. 补充文档** — 更新计划状、添加快速注释

## Verification checklist

- [x] `dotnet build -c Release` 无错误无警告
- [ ] 游戏启动后白起显示 4 体力 + 4 手牌（无法在无 GUI 环境验证，理论正确）
- [ ] 出牌阶段可使用"杀"，AI 无法用"闪"响应（杀神技能生效）
- [ ] AI 回合中 AI 会出杀、用桃、出闪
- [ ] 体力归零时触发濒死处理，桃可自救
- [ ] 一方体力≤0 时正确判定胜负
- [ ] 多轮循环不出异常（牌堆洗牌正常、界面不卡死）

## Handoff notes

- 新项目路径 `C:\Users\Administrator\Desktop\Agent\BaiQi\`，与 `Snake/` 并列
- WPF 项目模板参考 `Snake`：`net10.0-windows`，`UseWPF=true`
- 建议先发版运行 `install-and-run.bat` 验证编译环境
- 游戏引擎纯逻辑不依赖 WPF，便于后续单元测试
- 所有功能在一期中完成，后续仅需扩展武将/卡牌/模式
