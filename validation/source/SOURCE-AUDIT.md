# 2026-10-21 FIPS / Monica 最新源码审计

本审计读取 `D:\Documents\Code\FIPS\FIPS2022` 和 `D:\Documents\Code\FIPS\MoLibrary` 当前工作树。FIPS HEAD 为 `f64cfc98a7c88272475ecd969bc84f0158a59cfb`，Monica HEAD 为 `4eb5e715f58823815363c930de5f2de4a30ec8f5`。读取时两个源码工作树均干净。逐文件 SHA256 与仓库状态由 [Collect-SourceAudit.ps1](Collect-SourceAudit.ps1) 收集到 [source-audit.json](source-audit.json)。

本页是源码发现与验证范围说明，运行结论以本轮真实测试结果为准。旧 `Latest2026` 目录的 9 月结果不能直接证明当前 10 月保存管线兼容。旧报告出现 `OurDbContext`、旧 `CompleteAsync` 和提交前事件缓冲器；当前代码已经采用 `FipsDbContext`、`RepositoryDbContext`、`UnitOfWorkManager.RunAsync` 和事务性 Outbox。

更新：下表记载的 BulkSaveChanges 外部事务 tracker 问题已经在 `8b48303` 修复；直接 COPY 的生命周期守卫、复杂 Owned 输出、复合键及 null 回填也已修复并实测。具体通过项及仍存在的 FIPS 自有失败见 [本轮最终报告](../REPORT-20261021.md)。本页其余内容作为修复前源码审计与验证清单保留。

## 采用的本地技能

- FIPS `skills/fips-application-spec/SKILL.md` 及 `references/profile.md`、`references/persistence.md`：核对业务仓储窄接口、技术批量边界、历史库与分片事务所有权。
- Monica `skills/monica-application/SKILL.md`：核对现有应用职责与契约。
- Monica `skills/monica-infra-persistence/SKILL.md` 及 repositories / transactions 引用：核对真实保存、事务、失败与上下文生命周期。
- Monica `skills/monica-unit-testing/SKILL.md`：优先真实 public surface 和最小可证明测试边界；本任务要求真实 localhost GaussDB，因此使用独立验证库满足用户指定的数据库测试。

本轮未修改 FIPS / Monica 应用架构或其技能。测试适配不能自行把直接 Bulk 的行为升级为普通仓储语义。

## 影响集成判断的当前契约

| 当前事实 | 源码位置 | 集成含义 |
| --- | --- | --- |
| FIPS 仍引用官方 `EFCore.BulkExtensions 10.0.0` | `src/Shared/Platform.Infrastructure/Platform.Infrastructure.csproj` | 独立验证通过不代表 FIPS 已切换到 fork 包。实际替换时需处理同名 Core 程序集与包依赖，确保只加载一个一致版本。 |
| `FipsDbContext<T>` 直接继承当前 `RepositoryDbContext<T>` | `src/Shared/Platform.BuildingBlocks/Persistence/FipsDbContext.cs` | 最新保存策略必须作为正常保存的回归对照，旧模拟 SavingChanges 钩子不足以代表当前版本。 |
| 审计、软删除改写、版本与 ConcurrencyStamp 更新在固定保存管线内执行 | `Monica.Repository/Persistence/Services/PersistencePolicies.cs` | 原始 Bulk 绕过 `SaveChanges` 时不能声称自动保持这些策略。BulkDelete 默认是物理删除。 |
| FIPS 同步字段由 `IPersistencePolicy` 扩展执行 | `src/Shared/Platform.BuildingBlocks/DataSync/FipsPersistencePolicy.cs` | 原始 Bulk 成功不代表 `IDataSyncPropertySetter` 已执行。必须保留普通写的同步行为并明确技术批量责任。 |
| SaveCore 将业务保存、实体事件快照和 Outbox 写入同一事务，外部传输另行执行 | `Monica.Repository/Persistence/Services/RepositoryDbContext.cs` | 不能用旧版“提交前事件 publisher 抛错”替代当前 Outbox 原子性与快照验证。 |
| UoW 成功或失败之后变为 terminal，失败后上下文也拒绝继续写；多上下文要求显式参与者与相同连接实例 | `Monica.Repository/UnitOfWork/Services/UnitOfWorkManager.cs` | 应验证提交后、回滚后、rollback-only、非参与上下文和失败后复用均拒绝产生新写入。 |
| `RepositoryOperationInterceptor` 守护 EF Reader / NonQuery / Scalar 操作 | `Monica.Repository/Persistence/Services/Support/RepositoryOperationInterceptor.cs` | GaussDB 二进制 COPY 直接连接路径需单独验证，避免绕过此生命周期守护。 |
| FIPS 分片物理上下文具有 `TransactionOwner`，创建器绑定共享连接与事务 | `FipsDbContext.cs`、`DbContextShardingTableExtensions.cs` | 逻辑 shell Bulk 不会自动等价于按实体分片路由。物理上下文分组必须验证同库多月提交、失败回滚与正确表名。 |
| `BulkSaveChanges` 在外部事务存在时不会自动 AcceptAllChanges | `EFCore.BulkExtensions.Core/DbContextBulkTransactionSaveChanges.cs` | 在 UoW 最终普通 SaveChanges 前检查状态，防止再次提交同一批 Added / Modified / Deleted；不能无条件接受包含未写入实体的整个 tracker。 |

## 当前真实模型应覆盖的形状

| 实体或能力 | 当前源形状 | 必需的可观察断言 |
| --- | --- | --- |
| `Flight` 完整图 | 客户端 long `FPID`；同表 Owned `Plan`、`AircraftInfo`、`RouteInfo`；`DepInfo` / `ArrInfo` 共享主外键；`RteInfo.FPID` 一对一；`FlightLabels` 复合键 | 增、读、改、删后以新上下文逐字段核对；核对 Owned 字段、可空子实体、复合键和 FK；删除后全部目标子表计数为零。 |
| `FlightPlan` / `AircraftInfo` / `RouteInfo` | `char` 与 `varchar`、枚举字符串 converter、`timestamp`、`interval`、可空属性、显式列名 | COPY 列顺序、converter 往返、char 去尾空格、null 与非 null 多批次切换、仅 Owned 属性的选择性更新。 |
| `PreFlightPlan` 与 Flight 分片 | 按时间路由、动态物理模型、逻辑上下文协调保存 | 混月行进入正确物理表；无后缀逻辑表无意外数据；同库所有物理写都在同一拥有者事务中。 |
| `Airway` / `AirwaySegmentPoint` / `AirwayPoint` | 多表关系、共享航点、自然键与引用限制 | 父子键传播、同一航点引用保留、Restrict 错误不导致部分提交、复合自然键 upsert 不污染其他范围。 |
| `InMessage.Detail`、`AlarmDetail` | Owned 实体、不同根实体拥有不同值对象 | 含 Owned 的 output / BulkRead / IncludeGraph 与普通 EF 查询一致；真实身份回写不再只对标量实体验证。 |
| `User.UserInfo`、`FileTemplate.Content`、消息 `PFPIDs` | JSON / 集合 converter、深层对象与集合 | 直接 Bulk 整列转换与普通跟踪深层修改均可读回，不能只验证 JSON 字符串非空。 |
| MilitaryFlight encryption | `UseEncryption` 按属性类型选择 string / byte[] converter，已有 converter 保留 | Bulk 写入物理存储不含明文；普通 EF 可解密；BulkRead 值与输入一致；加密与已有 converter 的优先规则保留。 |
| MessageExchange 四类 `Revision` | `MessageOutboundBatch`、`MessageInboundReceipt`、`MessageInboundContent`、`MessageCircuitState` 均显式 `IsConcurrencyToken()` | 两个独立写者旧快照，普通 EF 拒绝第二次写；原始 Bulk 的并发行为需明确验证，不能将时间戳选项误当任意并发令牌支持。 |
| 普通 Monica / FIPS 仓储 | soft-delete query filter、审计、实体版本、stamp、同步属性、Outbox、同操作重复 flush | 安装 fork 后普通保存仍保留全部原有策略，新增 / 修改 / 软删除事件快照与业务行原子提交，失败无残留。 |

`FlightLabel` 当前存在两个 `IEntityTypeConfiguration`，分别声明了相同键集合的不同顺序；真实模型最终键顺序由配置应用顺序决定。测试应读取运行时 EF metadata，不能凭其中一个配置类硬编码生成 SQL 的列顺序。

## 结论的边界

安装 Bulk provider 包、保留普通保存路径与按约定显式调用技术批量，可以分别验证。全仓所有业务功能、消息传输、部署拓扑、独立历史数据库、跨数据源一致性、长期稳定性和完整 FIPS 宿主的启动不由几组实体 CRUD 或吞吐测试证明。报告必须区分已执行测试、源码发现、明确拒绝的调用与尚未执行范围。

性能测试应记录真实行数、字段宽度、层级数、FK 索引、事务、同步或异步、身份回写及硬件环境；执行后以独立查询核对精确行数和关键字段。单机一次计时不作为完整 FIPS 吞吐或稳定性保证。
