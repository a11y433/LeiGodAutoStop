# 雷神自动暂停时长

首次使用时点“一键检查并开始”。程序会核对安装包、雷神账号环境，并设置雷神打开时自动启动、完全退出时关闭监控。需要权限时 Windows 会询问；选择“是”后继续。雷神缩到托盘时监控继续运行。

当前发布目标为 Windows x64 self-contained EXE；32 位 Windows 不支持。不同 Windows x64 电脑都必须使用目标电脑自己的雷神安装、登录账号和游戏 EXE 路径。开发电脑的打包成功不等于所有雷神客户端日志/API组合已获现场验证，目标电脑仍须按跨电脑验收脚本完成实机确认。

一个适用于 Windows x64 的便携应用：观察雷神当前加速会话及该会话关联的游戏进程。当雷神确认没有加速会话，或已确认会话所映射的全部游戏进程连续 120 秒都不存在时，应用读取账户状态；若账户尚未暂停，则请求暂停，并连续 45 秒三次读回确认暂停状态和余额没有真实下降。

应用只会暂停雷神账户时长。它不会关闭游戏、停止雷神加速、退出雷神客户端或结束雷神进程。

## 行为规则

- 正在运行的雷神加速游戏：只观察，不请求暂停。
- 多个雷神加速会话：检查全部会话日志映射的游戏进程；任一进程还在运行，就不开始空闲倒计时。
- 雷神未加速的其他游戏：不在雷神当前会话映射中，不阻止暂停。例如正在加速的游戏退出后，另一个未加速游戏仍运行，120 秒后仍可暂停。
- 雷神日志明确证明加速游戏已停止，但该游戏进程还在运行：不暂停，直到进程退出。
- 雷神日志缺失、矛盾、游戏映射不完整、进程清单读取失败或 SDK 读回未知：显示原因并重置倒计时，不发送暂停请求。仅当当前客户端日志已有普通事件但尚无加速事件时，允许用 SDK“未加速”读回开始倒计时；已有加速事件的未知日志不得这样回退。
- 雷神运行但当前没有加速会话：只有 SDK 明确读回“未加速”才开始 120 秒倒计时；日志和 SDK 都无法证明时保持等待。
- 暂停前再次读取雷神会话和游戏进程；暂停请求后要求账户显示已暂停，并连续 45 秒三次读回中余额没有真实下降。任意一次明显下降都会显示“暂停未确认”，不会伪报成功。
- 每个连续空闲周期只请求一次暂停。检测到游戏、会话或雷神客户端变化后重新开始计时。

雷神日志中的 launch-local-game.game_process 是游戏进程识别依据。对没有进程路径的通用名称，保持游戏运行，点击窗口中的“确认当前游戏进程”，核对游戏 ID、进程名和完整路径。雷神日志缺少进程名时，应用列出当前 Windows 登录会话里路径可读的运行进程，只有用户确认后才保存映射；可以在“管理本机游戏映射”中查看和删除旧映射。它不硬编码一份特定电脑的游戏目录。

## 获取和运行

便携包从 dist 目录中选择最新时间戳的 LeiGodAutoStop_*.zip。解压后运行 LeiGodAutoStop.exe。应用默认自动暂停已启用，可在窗口中切换为仅观察。关闭窗口会缩至通知区域；托盘菜单可重新打开窗口或退出应用。旧包作为历史版本保留。

随包 SHA256.txt 用于核对 EXE 完整性：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\verify-package.ps1 .
```

首次设置完成后，雷神打开会自动启动监控。换电脑或应用目录移动后，再点“一键检查并开始”更新配置。也可以用托盘菜单退出应用。

## 跨电脑迁移

应用为自包含 EXE，不要求另装 .NET 运行时。当前发布为 Windows x64；32 位 Windows 无法运行。把整个应用包复制到另一台 Windows x64 电脑即可启动。每台电脑需要安装并登录雷神客户端；本应用通过目标电脑当前客户端日志读取会话和游戏进程映射，并通过雷神账户接口读回暂停状态。

配置位于 %LOCALAPPDATA%\LeiGodAutoStop。路径映射按当前 Windows 用户和电脑保存。换电脑时只复制应用 ZIP；在目标电脑登录其自己的雷神账号，点“一键检查并开始”，再在游戏运行时确认该机实际路径。不要复制源电脑的整个配置目录、账户状态、登录凭据或本机自动化日志。

## 构建与验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-portable.ps1
```

构建脚本会先执行离线场景，再发布 self-contained x64 应用、生成 ZIP 和 SHA-256 清单，并核对包内 EXE。离线场景只验证确定性逻辑，不等于在目标雷神账号上完成真实暂停验收。现场状态记录见 LIVE_ACCEPTANCE.md。


## Credits

- Creator and lead author: yog.
- Second credited contributor (AI collaboration): OpenAI Codex, at the creator's request.

This is an independent community project and is not affiliated with or endorsed by LeiGod. It reads the local LeiGod client environment and sends account pause/status requests through the user's own signed-in client context. Use only with an account and client you are authorized to use, and check the service terms that apply to you.

## License and bundled runtime

This project's source is licensed under the MIT License in `LICENSE`. The Windows x64 self-contained release also bundles the .NET 8.0.8 runtime. See `DOTNET-LICENSE.txt` and `ThirdPartyNotices.txt` for the runtime license and third-party notices.
