# 逐项运行证据

`test-outcomes.json` 保存 GaussDB 相关选择性回归、SQLite 回归、FIPS 共享套件原始依赖和 fork 依赖两轮的 TRX 计数器、逐项名称/状态及错误摘要。

`monica-test-outcomes.json` 保存最新 Monica 原有 Repository 118 项测试的计数器及逐项结果。三个 build/pack 日志记录最终 GaussDB 项目构建和本地打包；Monica 原始测试输出见 `monica-repository.log`。`package-smoke-final.log` 记录新包在独立 NuGet 缓存下的消费验证。

原始 TRX 为生成文件、忽略 Git，保留在本地本目录。可复现命令、版本和已知失败解释见 [验证报告](../REPORT-20261021.md)。本目录结果不是对所有 FIPS 微服务端到端业务功能的完整证明。
