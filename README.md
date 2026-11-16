# Gekidan-Denki-Server

非官方的《ワールドダイスター 夢のステラリウム》（ユメステ）本地服务端，使用 C#、ASP.NET Core 与 PostgreSQL 实现客户端所需的 API、实时通信与玩家数据持久化。

> [!WARNING]
> - 本项目仅面向学习、协议研究和个人测试，不隶属于游戏开发商或运营方，也不提供官方线上服务。
> - 项目仍在开发中，已有接口不代表完整游戏功能；部分排行榜与接口仍为占位数据。
> - 项目大量使用 AI 辅助开发，可能遇到各种奇怪的问题，欢迎提 issue 或直接修。

## 目录

- [快速开始](#快速开始)
- [功能状态](#功能状态)
- [已知问题与计划](#已知问题与计划)
- [技术栈与目录结构](#技术栈与目录结构)
- [开发者参考](#开发者参考)
- [相关项目](#相关项目)
- [许可与声明](#许可与声明)
- [鸣谢](#鸣谢)

## 快速开始

### 1. 运行要求

- .NET 10 SDK
- PostgreSQL 16（库名默认 `local_game`）
- 与所用客户端版本匹配、且你有权使用的 MasterData 与本地资源
- 可访问本服务端的测试客户端或模拟器

### 2. 准备数据

- 将客户端 `mastermemory.db` 解包，把导出的 JSON 表放到服务端根目录的 `masterdata-export/tables/`。
- [`SiriusLocalServer.csproj`](SiriusLocalServer.csproj) 通过 `<EmbeddedResource>` 嵌入其中约 60 张表，缺少时**无法编译**；仅克隆源码不保证能直接构建。
- 准备游戏资源目录（Resources 与 Addressables），路径在下一步的 `.env` 中配置。

### 3. 配置

```powershell
Copy-Item .env.example .env
```

按注释填写 `.env`，至少需要：

| 变量 | 说明 |
| --- | --- |
| `DATABASE_URL` | PostgreSQL 连接串 |
| `MASTER_DATA_FILE` | `mastermemory.db` 路径 |
| `RESOURCES_ROOT` / `ADDRESSABLES_ROOT` | 客户端资源目录 |
| `APPLICATION_VERSION` / `ASSET_VERSION` | 必须与客户端、主数据配套 |
| `WDS_SKIP_MASTER_OVERLAY` | **必须为 `1`**，否则客户端初始化阶段拒绝主数据补丁 |

### 4. 构建与运行

```powershell
dotnet build -c Release
cd bin\Release\net10.0
$env:REPLAY_MODE='0'
$env:WDS_SKIP_MASTER_OVERLAY='1'
$env:WDS_EXTRA_UNION_KEYS='1'
.\SiriusLocalServer.exe
```

启动后访问 `http://127.0.0.1:8099/health`，返回 200 即正常。

### 5. 连接客户端

客户端通过 HTTPS 443 与服务端通信，使用自签名证书（`server.pfx`）。测试客户端需信任该证书，或使用已允许自签名的测试包；首次连接失败时优先检查证书与 `PUBLIC_BASE_URL`。

## 功能状态

账号侧已经打通登录、注册、引继与账号恢复，玩家数据、主页、教程和个人资料都有持久化。

打歌与稽古可以完整结算：分数校验、评级、掉落、经验、星等级经验、每日次数与体力的消耗和恢复都已接上，卡结算的问题也已修复。

养成方面，角色等级、才能開花、覚醒、星等级、技能等级与卡牌升级都可以正常进行，相关主数据与消耗都按官方表处理；剧团侧已接入剧团等级、剧团加成与剧团任务进度的读取与结算。

活动侧实现了官方 PT 公式、累计 PT 奖励（达到档位立即发放）以及结算期结束时的排名奖励；星等级奖励、任务、通行证、登录奖励同样可以正常领取。

抽卡支持普通池与活动池的单抽、十连、保底与抽卡历史记录；兑换侧覆盖交换所、乐谱兑换与限购校验，角色碎片兑换等少数接口仍待补。

拍照与摄影相册支持拍照生成、相册编成、升级、出售、能力更换与观影记录。

演劇リーグ与三連劇的参加奖励和排名结算已经可用，但客户端排行榜的内容仍是固定数据；好友与社团功能大部分可用，少数边角操作还是占位实现。

实时通信（gRPC）可以正常连接，多人演出与好友邀请的基础流程已接通。

## 已知问题与计划

- 演劇リーグ / 三連劇的客户端排行榜仍是固定数据，奖励是真的、榜单不是；
- 部分接口（角色碎片兑换、卡池重抽等）客户端尚未发出请求，暂无真实报文，暂时保留空实现；
- 主数据与游戏素材不在仓库中，需自行导出；
- `WDS_SKIP_MASTER_OVERLAY=1` 为硬性要求，付费礼包类依赖 overlay 的功能不可用；
- 服务端只在效果确定的情况下复算分数，遇到动态效果会保留客户端数值并做上界校验。

## 技术栈与目录结构

- C# / .NET 10、ASP.NET Core、gRPC、PostgreSQL、MessagePack、MasterMemory
- 端口：`8099` HTTP API、`443` HTTP/1.1 + HTTP/2、`8788` gRPC 实时

```
Endpoints/     HTTP 路由与协议封装
Services/      账号、玩家数据、社团、实时通信
Game/          规则与结算（打歌、稽古、活动、奖励）
Storage/       数据库结构与迁移
Protocol/      MessagePack、LZ4、实时协议
Assets/        主数据读取与资源解析
docs/          进度、逆向记录
```

## 开发者参考

- [项目进度](docs/PROGRESS.md)：各模块已修复的问题与待办
- [逆向记录](docs/REVERSE.md)：公式推导、字段顺序、主数据语义、错误码对照
- [更新记录](CHANGELOG.md)：修复与变更

## 相关项目

- [yumesute-server](https://github.com/Sakurashiyo/yumesute-server)：本项目的上游参考实现
- [yumesute-preservation-server](https://github.com/Alehero/yumesute-preservation-server)：基于 server-of-dreams 的 Python 实现
- [yumesute-calc](https://github.com/esterTion/yumesute-calc)：演技力 / 打歌分数计算器

## 许可与声明

基于 [yumesute-server](https://github.com/Sakurashiyo/yumesute-server) 二次开发，原项目与本项目均采用 **PolyForm Noncommercial License 1.0.0**：

- 允许个人使用、修改与非商业分发；
- 禁止任何商业用途；
- 分发时需保留 `LICENSE` 与版权声明，并说明修改内容。

本项目不包含任何游戏素材与主数据；游戏及相关素材版权归 Project WDS 与 KMS.inc 所有。详见 [LICENSE](LICENSE) 与 [NOTICE](NOTICE.md)。

## 鸣谢

- [yumesute-server](https://github.com/Sakurashiyo/yumesute-server) 的作者与贡献者
- [yumesute-calc](https://github.com/esterTion/yumesute-calc) 提供的数值参考
- 以及所有提交 issue、帮忙测试的朋友
