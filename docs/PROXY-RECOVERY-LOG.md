# 代理恢复诊断日志

日志位置：`%LOCALAPPDATA%\singC\proxy-diagnostics.jsonl`。在资源管理器地址栏输入 `%LOCALAPPDATA%\singC` 即可找到。超过约 1 MiB 时保留一份 `proxy-diagnostics.jsonl.previous`，旧备份替换；单条记录可能使文件略超上限。两份文件均可交给开发者分析。

在设置页打开“日志开关”后，启动、停止、切换和正常关闭时记录诊断。默认关闭，用户选择保存到本地设置，切换立即生效；关闭后保留已有日志，已开始的单次写入可能完成。该开关供未来其他诊断日志共用，不影响代理恢复备份，也不控制内核向页面提供的运行日志或用户主动执行的外部采集脚本。进程被强制终止后不能继续写日志，事件缺失不能单独证明关闭方式。不注册自启动。

当前系统代理恢复策略：停止、切换或退出时强制写回本次启用前的完整设置，期间其他软件修改的代理、绕过列表和 PAC 会被覆盖；恢复未完成时下次启动的补恢复也采用同一策略。原设置已启用代理时仍恢复为启用，不等同于强制关代理。TUN-only 租约保持原匹配恢复逻辑。写回失败保留记录；写回成功返回后清空记录，尚未增加写后核验。

每行一个 JSON 对象，包含 UTC 时间、进程 ID、会话编号 `Session` 和阶段 `Stage`。`Original` 是启动前设置，`Applied` 是 singC 写入的设置，`Current` 是恢复判断时读到的设置。启用标志 `Flags` 保存数值；地址、绕过列表、PAC（自动代理脚本）只保存长度、是否为空和 SHA-256 指纹，用于比较差异，不输出原文或凭据。错误保存类型、HResult 及适用的 Windows 错误码，不输出异常消息。

| 阶段 | 含义 |
|---|---|
| `app.close-requested` | 已进入正常窗口关闭流程 |
| `core.stop-requested` | 开始停止内核 |
| `core.stop-failed-before-proxy-restore` | 内核停止失败，未继续到该次代理恢复 |
| `core.unexpected-exit` | 观察到内核意外结束，随后尝试恢复 |
| `restore.compare` | 当前设置非空时，`ChangedFields` 列明与 Applied 的差异；读取失败时 Current 为空 |
| `restore.forced-original` | 系统代理租约无条件恢复原快照，差异不再阻止写回 |
| `restore.read-current-failed` | 当前设置读取失败，系统代理恢复仍继续；后续 compare 的 Current 可能为空 |
| `restore.write-original` / `restore.write-succeeded` | 开始写回原设置 / 写入 API 返回成功 |
| `restore.skipped-settings-changed` | TUN-only 租约因设置不匹配跳过恢复；旧版本系统代理租约也可能出现此事件 |
| `restore.journal-cleared` | 已清空恢复记录；系统代理在写回成功后执行，TUN-only 沿用原行为 |
| `restore.failed` / `recover.failed` | 恢复发生异常，查看错误类型和代码 |
| `restore.empty-journal` / `recover.no-journal` | 记录为空 / 文件不存在 |
| `recover.active-lease` | 文件被其他实例持有，未抢占恢复 |
| `apply.journal-saved` / `apply.completed` | 备份已保存 / 代理设置调用成功 |
| `apply.write-failed` / `apply.rollback-failed` | 写入失败 / 原设置回滚也失败 |
| `app.close-cleanup-completed` / `app.close-failed` | 退出清理完成 / 退出流程失败 |

`restore.completed` 仅表示恢复方法没有抛异常，**不表示一定写回设置**；需要结合 `write-succeeded`、`skipped-settings-changed` 或空记录阶段。写入 API 成功也不证明其他程序随后没有再次更改设置，建议配合 [现场快照工具](PROXY-DIAGNOSTICS.md)。

分析建议：先按进程与会话排列事件，再看有没有进入关闭、是否到达恢复比较、差异是哪几个字段、是否执行写回或跳过、恢复记录什么时候清空。缺少日志还可能来自磁盘权限/空间或日志轮转；日志失败不会阻止正常代理恢复。
