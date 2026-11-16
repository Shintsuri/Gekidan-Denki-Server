# 逆向记录

开发过程中从客户端封包、主数据表推断出的规则。每条附推断依据，便于复核。

---

## 1. 活动点数（イベントP）

### 结论

```
PT = 100 × (体力 / 20) × スコア係数 × (1 + 编成加成) × ブースト倍率

スコア係数
  0 ~ 2,999,999          : 1 + score / 6,000,000
  3,000,000 ~ 14,999,999 : 1.5 + (score - 3,000,000) / 60,000,000
  15,000,000 以上        : 1.75 + (score - 15,000,000) / 100,000,000

ブースト倍率（体力消耗 n 炊き）
  1 → 5    2 → 10   3 → 14   4 → 18   5 → 21
  6 → 24   7 → 26   8 → 28   9 → 29  10 → 30
```

`基本報酬量 = 楽曲のスタミナ消費量 / 20`。

### 依据

- 公式结构来自日服 wiki（gamerch、gamelifenote 的实测验证）
- 系数分母（600 万 / 6000 万 / 1 亿）与分段点由实测样本反推
- ブースト倍率表来自 wikiwiki 的整理

### 校验

实测样本：10,623,783 分、体力 34、编成 +175%、10 炊き

```
基本報酬量 = 34 / 20 = 1.70
スコア係数 = 1.5 + (10,623,783 - 3,000,000) / 60,000,000 = 1.627
编成加成   = 1 + 1.75 = 2.75
ブースト   = 30

100 × 1.70 × 1.627 × 2.75 × 30 = 22,819.6
```

游戏内实际值：**22,819**。误差 0.6，为取整导致。

### 注意

- 分数不随体力倍率变化，倍率只通过 `ブースト` 表体现
- 体力不足被降倍时使用 `EffectiveMagnification`，避免用少量体力刷高倍率

---

## 2. 编成加成（編成ボーナス）

### 结论

`StoryEventBonusCharacterBaseMaster` 每行格式：

```
[Id, EventMasterId, Type, Value]
```

| Type | 含义 | 匹配方式 |
|---|---|---|
| 1 | 剧团加成 | `CharacterBaseMaster[row][9]` == Value |
| 2 | 角色加成 | 角色 base id == Value |

命中一次 +25%。同一角色剧团与角色都命中则为 +50%。5 人上限 +250%。

### 依据

对比 12 个活动的加成行：

```
event 20001: type 2, value 201/202/204/205   ← 多个，值为角色 base id
event 20002: type 1, value 4                 ← 单个，值为剧团 id
event 20005: type 1, value 4
event 20006: type 2, value 203/204/101/102/103
event 20007: type 2, value 301/201/401
```

`type 2` 的 value 全部落在角色 base id 区间；`type 1` 的 value 只有 1~4，对应 `CompanyMaster` 的剧团数量（不含联动剧团 900）。

### 剧团 id

`CompanyMaster`：

```
1   シリウス
2   Eden
3   銀河座
4   劇団電姫
900 ラブライブサンシャイン
```

`CharacterBaseMaster[row][9]` 即角色所属剧团 id。

### 属性加成

`StoryEventMaster[row][2]` 是活动指定属性，角色的属性编码在 `CharacterMaster[row][15]`。

`CharacterMaster[15]` 是三位数，中间位为属性 id：

```
131 → 属性 3
121 → 属性 2
141 → 属性 4
111 → 属性 1
411 → 属性 1
431 → 属性 3
```

属性 id 对应关系（1~4）：憐 / 凛 / 彩 / 陽。

命中 +25%/人。

### 双人卡

`CharacterMaster[row][25]` 是第二人的 base id。

双人卡的第二人需要**单独判定**一次剧团、角色、属性。例如「姉妹は手を取り合って」的主角色是千寿暦（剧团 3），第二人是千寿いろは（剧团 4），两者的加成不能合并计算。

### 依据

`CharacterBaseMaster` 的字段：

```
[402, "白丸美兎", 说明, "東上野高等学校", 2, 5, 5, 156, "コスプレ", 4, "Shiromaru Mito", ...]
                                                              ↑
                                                          row[9] 剧团
```

同剧团的角色 `row[9]` 一致：402/403/404/405 都是 4，301 是 3。这与 `CompanyMaster` 的剧团划分一致。

---

## 3. 客户端字段顺序

### CharacterBase

客户端只有 **8 个字段**，不是数据库的列顺序：

```
[Key(0)] Id
[Key(1)] CharacterBaseMasterId
[Key(2)] StarRank              ← 星级，不是等级
[Key(3)] TotalStarPoint        ← 星点
[Key(4)] CostumeMasterId
[Key(5)] KeyMissionLevel
[Key(6)] PortalCharacterId
[Key(7)] PortalDisplayAwakeningStatus
```

**客户端没有「角色等级」字段。** 游戏内显示的等级是卡牌等级（`user_character_cards.level`），角色本身的成长表现为 `StarRank` 与 `TotalStarPoint`。

### 依据

`dump.cs` 中 `CharacterBase` 的 `[Key(n)]` 定义。按数据库列顺序下发会导致 `userId` 被当成 `CharacterBaseMasterId`、`character_base_master_id` 被当成 `StarRank`，表现为角色界面数值全乱。

### FriendResult

```
[Key(0)]  UserId
[Key(1)]  （废弃占位）
[Key(2)]  PlayerRank
[Key(3)]  TrophyMasterId1      ← 称号在这里
[Key(4)]  TrophyMasterId2
[Key(5)]  TrophyMasterId3
[Key(6)]  Introduction
[Key(7)]  LastLoggedInAt
[Key(8)]  Name
[Key(9)]  PlayerRate           ← double?
[Key(10)] IsPublicPlayerRate
[Key(11)] LeagueClass
[Key(12)] CharacterRanks
[Key(13)] IsPublicAlbumMainPage
[Key(14)] （废弃占位）
[Key(15)] MainCharacterMasterId
[Key(16)] DisplayAwakeningStatus
[Key(17)] IconFrameMasterId
[Key(18)] IsFavorite
```

称号必须放在 3/4/5，放到 9/10/11 会挤掉 `PlayerRate`、`IsPublicPlayerRate`、`LeagueClass`，后续字段整体错位，表现为点击玩家档案打不开。

### Photo

```
[Key(0)]  Id
[Key(1)]  FileName
[Key(2)]  SasToken
[Key(3)]  PhotoEffectMasterId
[Key(4)]  Lock
[Key(5)]  UseAlbumPage
[Key(6)]  Level
[Key(7)]  Rarity
[Key(8)]  SignMasterId
[Key(9)]  GeneratedAt
[Key(10)] ThumbnailSasToken
[Key(11)] AppearedCharacterBaseMasterIds
[Key(12)] TaggedCharacterBaseMasterIds
[Key(13)] UseDecoPage
```

### 依据

均在 `dump.cs` 中按 `[Key(n)]` 确认。MessagePack 按索引序列化，顺序错一位就整体错位。

---

## 4. 稽古的 Sense

千寿いろは与静香在设定上没有 Sense。`CharacterMaster[row][11]` 为 0 表示该角色无 Sense。

稽古编成时不能无条件为每个槽位生成 Sense 与触发时轴，否则会释放其他角色的技能。

### 依据

`CharacterMissionRules.HasSense()` 读取的就是这一列，角色任务中「无 Sense 角色不参与某些任务」的逻辑也基于它。

---

## 5. 错误码对照

| 错误码 | 场景 | 原因 |
|---|---|---|
| `HE05-004-0` | 角色任务批量领取 | `ToDictionary` 遇到重复键抛异常 |
| `HE2-005-0` | 饰品自动售卖 | 路由缺失，返回 404 |
| `HE20-008-0` | 稽古切换角色/曲目 | 旧会话未覆盖，抛 `LESSON_SESSION_CONFLICT` |
| `HE29-003-0` | 兑换乐谱 | `UnlockCondition != 14` 限制过严 |
| `HE29-004-0` | 交换所兑换 | 商店状态不同步 / 海报限购 |

---

## 6. 主数据 overlay

启用 `WDS_SKIP_MASTER_OVERLAY=0` 时，客户端会在初始化阶段拒绝服务端下发的主数据补丁，表现为「初始化失败」。

因此运行时必须设置：

```
WDS_SKIP_MASTER_OVERLAY=1
```

### 依据

多次尝试（包括只发 category 的补丁）均在同一阶段失败。关闭 overlay 后客户端正常进入。

---

## 7. 待确认

以下结论来自推断，尚未用实测数据验证：

- `StoryEventMaster[row][1]` 的语义（值为 1/3/4，与剧团数量部分重合，但与角色剧团字段不匹配）
- 编成加成的额外 +3.5%（客户端显示 +253.5%，公式只能算出 +250%）
- 高分段（1500 万以上）系数未经实测校验
- `CharacterMaster[16]`（`412` / `432`）的用途
