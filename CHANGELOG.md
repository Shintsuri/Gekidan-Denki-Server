# Changelog

本文件记录服务端的修复与变更。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

## 版本约定

本项目未按语义化版本管理，阶段性记录如下。

## 未发布

### 修复

- 每日免费礼包恢复为每天 1 次，修正数据库中已被写成 9999 的 `purchase_limit`
- 社团体力领取改为连续时间累计，不再先把小时数取整
- 补上 `Accessories/SetAccessoryAutoSell` 路由，原本返回 404 导致 `HE2-005-0`

### 变更

- 活动 PT 编成加成改为按剧团、角色、属性三类判定，双人卡第二人单独计算
- 角色 `StarRank` 与 `TotalStarPoint` 与客户端 `[Key(n)]` 对齐

## 活动点数

原实现：

```csharp
eventGain = score / 100;
```

与官方相差约 70 倍。改为官方公式后，用实测样本校准：

```
10,623,783 分 / 体力 34 / 编成 +175% / 10 炊き
计算 22,819.6，实际 22,819
```

## 排行榜

修复项：

- `CharacterBase` 按客户端 8 字段下发，不再按数据库 10 列
- `userProfiles` 补足 13 字段
- 阶梯排行档位改用 `rankTo`
- 0 分不进入排名
- 好友排行包含自己与好友
- 剧团排行补足 13 字段，称号使用可空值

## 角色任务

修复项：

- `BulkReceiveAllMission` 的 `ToDictionary` 重复键异常
- 领取时的字段索引与 `CharacterBase` 对齐
- `KeyMissionLevel` 使用 `level` 列存储

## 稽古

修复项：

- 无 Sense 角色（千寿いろは、静香）不再生成技能
- 稽古结算的 `result[23]` 填入奖励，恢复报酬动画

## 照片

修复项：

- 拍照写入 `user_photo_records`
- `Photo` 字段映射，等级与稀有度存入 `payload`
- 照片升级按 `PhotoLevelUpItemGroupMaster` 扣素材

## 好友

修复项：

- `FriendResult` 的称号位置错误导致档案打不开
- 字段顺序按客户端 `[Key(n)]` 重新排列

## 社团

修复项：

- 剧场体力领取的取整问题
- 首次加入社团时的领取逻辑

## 商店

修复项：

- 交换所限购检查与递增价格
- 海报兑换写入 `user_posters`
- 每日免费礼包限次
