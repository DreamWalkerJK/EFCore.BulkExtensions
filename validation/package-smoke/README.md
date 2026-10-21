# 本地 NuGet 独立消费验证

此项目只引用 NuGet 包，没有 Core/GaussDB 项目引用。先在仓库根目录执行：

```powershell
dotnet pack EFCore.BulkExtensions.Core/EFCore.BulkExtensions.Core.csproj -c Release -o validation/packages
dotnet pack EFCore.BulkExtensions.GaussDB/EFCore.BulkExtensions.GaussDB.csproj -c Release -o validation/packages
```

设置 `GAUSSDB_TEST_CONNECTION`，在本目录运行 `dotnet run -c Release`。当前本地版本为 `10.0.1-gaussdb.2`，未由本次任务发布到公共 NuGet。项目使用 validation/packages 作为附加包源，其余依赖从正常 NuGet 源获取。

`verified-results.json` 记录生成 ID、Owned 输出、char/date、更新/读取/删除、调用者事务回滚以及单一 Core 程序集加载检查通过。PG 模式测试库唯一命名并在 finally 删除。

`native-a-probe.json` 记录原生 GaussDB 驱动在 A 模式中读取 DateOnly 与二进制 date COPY 的失败，不调用任何 Bulk API。参数化 DateOnly 普通写入成功，但 SQL date 返回 timestamp，不能按 DateOnly 读取；原生 COPY date 触发 08P01。第二个探测库也在 finally 删除。

本地包 SHA256 见 `package-hashes.json`；包文件本身忽略 Git，保留在上级 packages 目录。

最后一次验证为新生成的包使用了独立 `NUGET_PACKAGES` 目录，避免同版本旧缓存影响，运行日志见 `../evidence/package-smoke-final.log`。若本地重打同一版本，复现时也应选择新的包缓存目录。
