# v1.0.9 验证范围与限制

本次新增可选代理恢复诊断日志、设置页“日志开关”及用户主动执行的只读采集脚本。2026-10-13 用户要求推送发布。恢复判断、代理备份和自启动行为不改变；未执行否决的独立监护方案。

- 完整应用回归：588 项通过，包含日志内容、四字段差异、轮转、日志不可写、设置保存失败与即时启停。
- 标准 x64 Release：构建成功，零警告零错误；发布包另由 Release 工作流清洁构建、校验并执行隔离更新交接验证。
- Coverlet：日志输出器 30/32 分支，93.75%；代理租约 21/22，95.45%。LoggingSettings 在测量的 Release IL 中无插桩分支，行/方法覆盖 100%，不把 0/0 称为 100% 分支覆盖。
- Stryker：日志输出器 35/42，83.33%（35 杀死、6 存活、1 未覆盖、0 超时）；4 编译错误及4工具过滤不计分。LoggingSettings 1/1，100%，有效变异体只有一个，证据规模有限。
- 采集脚本：3 项脱敏和 12 项模拟配置检查通过，未测 PowerShell 覆盖率/变异得分。

新增及相关代码完成 24 项异味审查；日志上下文参数、阶段字符串和方法规模仍有维护性倾向，没有据此执行额外重构。编译器及默认分析器无警告。日志缺失、写入 API 成功及配置指纹不能单独证明真实用户的网络行为；真实关闭/关机、浏览器内存配置与跨进程写日志竞争未完整验证。日志仍可能在突然终止或文件系统故障时丢失部分事件。

本次指标不替代 MC/DC 或真实系统验收，不延伸 v1.0.8 的质量例外为任何新增控制逻辑的自动豁免。发布用于获取故障证据，不声称代理残留已修复。

复现命令：`dotnet run --project Tests/UpdateTests.csproj -c Release`、`powershell.exe -NoProfile -ExecutionPolicy Bypass -File Tests/ProxyDiagnosticsTests.ps1`、`dotnet build singC.csproj -c Release -p:Platform=x64 -r win-x64`。完整本地日志与测量原始证据保存在忽略目录 `artifacts/validation/proxy-diagnostics-20261013/`；用户说明为 [代理恢复日志](PROXY-RECOVERY-LOG.md) 与 [现场采集](PROXY-DIAGNOSTICS.md)。
