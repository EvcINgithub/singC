# v1.0.8 质量审核记录

2026-10-09，维护者明确指示：“接受质量例外，推送重构代码并进行发布”。依据 TestRule.md 第 9.2、10.5 节，本次验收结论为**有条件通过**，允许发布 v1.0.8。接受例外不改变测得的指标，也不代表未执行的检查已经通过。

## 已执行验证

- 完整隔离回归 554 项通过；标准 x64 Release 重建 0 警告、0 错误。
- 原设置/配置/统计格式及更新包格式保持兼容；测试使用临时目录、模拟代理后端、假内核进程、本地回环。
- Coverlet 6.0.4 分支测量、Stryker.NET 4.16.0 变异测量；保留未覆盖和存活变异，不调整分母来提高得分。
- 全部 24 项代码异味复查、资源/并发/安全审查；系统架构维护于 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 已接受的例外及风险

| 模块 | 分支覆盖 | 变异得分 | 剩余风险/后续处理 |
| --- | --- | --- | --- |
| ConfigFileStore | 100% | 79.31% | 5 个命名格式等价候选仍保留在分母；写完至替换前的取消窗口需确定性文件接缝 |
| SingBoxService | 61.79% | 45.08% | 补可控进程终止、事件与时间接缝，验证完整失败/退出交错矩阵 |
| ClashWebSocketService | 86.96% | 50% | 补最大重试次数及平台连接异常边界 |
| ConfigCheckRunner | 87.50% | 47.62% | 补平台进程启动与输出/取消边界 |
| ProxyModeConfig | 90.28% | 76.02% | 按剩余变异补模式构造断言 |
| TrafficStatistics | 98.33% | 71.96% | 继续补总量统计剩余行为断言 |
| TrafficData | 98.44% | 58.77% | 继续补呈现字段与通知断言 |
| SystemProxyLease | 95.45% | 68% | 原生失败与回滚语义需更多可控故障验证 |
| InstallTransaction | 100% | 75% | 继续补事务失败断言 |
| ReleaseInfo | 95.83% | 78.57% | 继续补版本/资源识别边界 |
| UpdatePackage | 90.38% | 59.57% | 补清单、路径与归档剩余变异 |
| 历史 RouteConfigDocument / RouteModel | 82.89% / 82.03% | 61.04% / 33.46% | 保留兼容回归，后续补规则模型测试 |
| WinUI / COM / 原生系统代理 / 更新协调 | 未完整测量 | 未完整测量 | 当前不代表真实 UI、WinINet 写入或完整安装交接已通过；另行制定隔离系统级验证方案 |

SettingsStore（100%/85.37%）、StatisticsFileStore（100%/85.11%）、ConfigLoadCoordinator（100%/80%）、ClashApiEndpoint（97.50%/100%）、ClashConnectionSnapshot（90.32%/89.47%）、ConnectionPresentation（100%/92.31%）、OutboundTrafficStatistics（96.05%/81.37%）、BoundedResponse（100%/83.33%）及 UpdateHandoff（100%/88.89%）达到两个数值门槛。无分支的 LogAnalysis、ShutdownSequence 变异为 100%，分支记为不适用。

AppSettings 的 100% 分支仅来自 URL 判定，不代表真实静态路径读写完整验证。WindowsSystemProxy 只进行了原生只读快照检查（66.67% 分支），没有执行真实写入。

完整本地证据位于 `artifacts/validation/batch-improvements-20261009/`，该目录按仓库规则不提交构建产物。上述风险和后续计划继续保留；崩溃后更新事务恢复协议未纳入本次实现。
