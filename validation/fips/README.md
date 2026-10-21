# 最新 FIPS 源码和真实模型集成验证

使用原路径中实际 `FlightService.Domain`、`UserService.Domain`、`FileService.Domain`、`MilitaryFlightService.Domain`、`DataExchangeService.Domain` 和 `Monica.Testing` 的项目引用。测试启动完整 Monica TestApplication 宿主，使用真实 FipsDbContext、RepositoryDbContext、工作单元及 FipsPersistencePolicy。没有复制实体定义，也没有编辑 FIPS/MoLibrary 的生产源码。

设置 `GAUSSDB_TEST_CONNECTION` 后运行 `./Run-Validation.ps1`。可通过 `-FipsRoot`、`-MonicaRoot` 指定源码根目录。所有参数均在 restore 和 build 阶段传入；不能在换依赖以后用旧 assets 的 `--no-restore` 替代首次 restore。`Integration.targets` 保留两个仓库的原始 targets，并将官方聚合 Bulk 包替换成当前 GaussDB 源码引用，排除同名 Core 版本冲突。

每轮创建唯一 `bulk_current_fips_<GUID>`，兼容模式为 PG，结束后删除。`GenerateCreateScript` 在新库中创建真实模型表；使用这条路径是因为当前驱动的 EnsureCreated 表探测会看见 openGauss 系统表而跳过业务表创建。测试加密使用零值测试 key/IV，通过生产 UseEncryption/AesProvider；外部身份和同步节点以确定性 TEST seam 提供。

当前实测 **9/9 PASS**，见 `verified-results.json`。覆盖完整 Flight 普通保存和子实体查询、三种同表 Owned、五类实体集 Bulk CRUD、char/varchar 枚举转换、interval、共享主键、复合标签键、选择性 Owned 更新、普通和批量共事务回滚、后续正常跟踪保存；此外覆盖保留生产保存管线、成功/失败工作单元结束后的同步和异步 COPY 拒绝、User.UserInfo 深层 JSON/null、FileTemplate JSON 集合、MAPlan 物理密文与 EF 解密和参数转换、MessageCircuitState 自定义 Revision 并发。

`model-evidence.json` 记录真实 Flight 的运行时 metadata 及加载程序集。模型有 20 个根标量、三个 Owned 共 37 个 metadata 属性（包含所有权键）。`schema.sql` 是实际执行的全模型 DDL。

默认 A 模式运行结果另保存在 `default-a-results.json`：7 PASS、2 FAIL。DateOnly 的军航 COPY 返回 08P01，消息日期读取返回 InvalidCastException。`../package-smoke/native-a-probe.json` 使用原生驱动、不调用 Bulk，重现同样错误，证明需要匹配数据库模式或应用层日期转换；安装包不会修改已有数据库模式。

`-SharedSuite` 额外运行 FIPS 原有 Test.Platform.Shared 全套，并要求 `FIPS_EFCORE_TEST_GAUSS` 指向独立的可丢弃数据库。原始和替换依赖两次均为 **310 PASS / 3 FAIL / 3 SKIP**，三个失败名称完全一致：两个 GaussDB 分片审计 UTC/timestamp 写入错误，一个对象映射 profile 重复。PostgreSQL 未配置造成三个 skip。脚本保留非零退出码；不会把这些失败变成通过。

原始共享测试也运行了 SQLite 分片、分片迁移 SQL、实际 GaussDB 迁移生成，以及工厂/持久化/同步/映射等回归。这一结果支持已覆盖范围没有新增失败，不能证明全 FIPS 的所有微服务、部署、外部消息交互和业务流程都完成端到端验证。完整结论见 [最终报告](../REPORT-20261021.md)。
