# Yumeno-Kanae

World Dai Star（ユメステ）私有服务器实现，C# / .NET 10。

用于协议研究与客户端运行，不含任何游戏资源。

## 文档

- [项目进度](docs/PROGRESS.md)：各模块已修复的问题与待办
- [逆向记录](docs/REVERSE.md)：公式推导、字段顺序、主数据语义、错误码对照
- [更新记录](CHANGELOG.md)：修复与变更

## 快速开始

```powershell
$env:REPLAY_MODE='0'
$env:WDS_SKIP_MASTER_OVERLAY='1'
$env:WDS_EXTRA_UNION_KEYS='1'
.\SiriusLocalServer.exe
```

依赖 PostgreSQL 16（库名 `local_game`）、主数据文件、客户端资源。

`WDS_SKIP_MASTER_OVERLAY=1` 为必需项，否则客户端在初始化阶段拒绝主数据补丁。

仓库不包含主数据。`SiriusLocalServer.csproj` 中有 61 个 `<EmbeddedResource>` 引用 `masterdata-export/tables/*.json`，缺少时编译失败。需自行从客户端导出并转换为 JSON 表。

## 技术栈

C# / .NET 10、ASP.NET Core、gRPC、PostgreSQL、MessagePack、MasterMemory。

端口：8099 API、443 Http1AndHttp2、8788 gRPC。

## 来源与许可

基于 yumesute-server-main 二次开发。原项目与本项目均采用 PolyForm Noncommercial License 1.0.0：允许个人使用、修改、非商业分发；禁止商业用途；分发时需保留 LICENSE 与版权声明，并说明修改内容。

早期独立实现的单文件版本已停止维护，见 [Yumeno-Kanae-Prototype](https://github.com/Shintsuri/Yumeno-Kanae-Prototype)。

##免责声明

仅用于学习与协议研究。不包含任何游戏资源。不得用于商业用途。游戏及相关素材版权归 Project WDS 及KMS.inc所有。

详见 [LICENSE](LICENSE)。
