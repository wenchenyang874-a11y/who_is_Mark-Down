# WIMD Rust Edition

这是 WIMD 的 Rust 重写实现，使用 Rust 核心、Tauri 2、Windows WebView2 和本地前端。旧 `src/` 中的 .NET/WPF 实现继续保留，仅用于行为对照、旧设置和旧安装器恢复会话迁移。

## 结构

- `crates/wimd-core/`：无 UI 依赖的文档、Markdown、安全 HTML、图片、设置和工作区核心。
- `crates/wimd-app/`：Tauri 桌面壳、WebView2 命令与本地 UI。
- `crates/wimd-app/ui/`：完全本地加载的 HTML/CSS/JavaScript 界面。
- `packaging/build-release.ps1`：格式、构建、测试和 NSIS 安装包打包入口。

## 当前能力

- Markdown 多文档编辑、实时安全预览、三栏布局切换和双向滚动。
- 严格 HTML 白名单、危险协议过滤、任务源码校验写回和代码复制宿主桥接。
- 最近文件、最近文件夹、按目录懒加载的工作区树、新建/重命名/删除和外部修改监视。
- 本地图片、剪贴板图片、ImgBB 显式上传与 DPAPI 密钥保护、托管背景、主题、字体和字号设置。
- 离线 Mermaid 和有界缓存、远程图片有界复用、原格式/PNG/自包含 HTML 图片另存为、等待图片完成的 PDF 导出，以及独立图片查看器。
- 多文档自动恢复、安装器更新重启会话、旧 .NET 恢复会话导入、跨窗口设置合并、外部修改全局/单文件策略和主进程资源统计。更新会话按窗口保存全部标签、活动文档、工作区状态、未保存正文、光标/滚动和窗口几何；安装后恢复全部窗口，未立即重开时保留到下一次启动。
- 安装器使用独立关闭助手先发送 `WM_CLOSE`，让运行中的 Rust 窗口完成更新快照并正常退出；Restart Manager 只在超时后兜底。
- GitHub 正式 Release 更新检查、流式下载、进度事件、取消、大小与 SHA-256 校验及显式安装确认。
- 独立启动诊断归档、UI 心跳卡顿记录、日志溢出汇总、WPR 启动采集入口，以及文件关联和资源管理器右键菜单。
- 设置使用独立 Tauri/WebView2 窗口；外观设置保留旧版“主题 / 字体 / 背景”分页、主题卡、字体试读和背景草稿，并支持应用、保存、取消与恢复默认。
- 设置写盘保留最近一次有效 `settings.json.bak`；字体字段兼容历史 `null`，单个旧字段异常不会再把主题、背景和字体整体回退默认。
- 设置保存/取消通过宿主先隐藏再销毁窗口，避免 WebView2 关闭过渡留下白色 `about:blank` 空页。
- Rust 安装使用独立 `WIMD.Rust.Markdown` ProgID 和 `WIMD.Rust` 右键动词，不覆盖旧版 `WIMD.Markdown`、`WIMD` 右键命令或默认 Markdown 处理器。
- Rust 安装显示名和开始菜单文件夹为 `WIMD Rust 预览版`，不复用旧版根 `WIMD.lnk`；安装钩子会修复早期测试包覆盖的旧入口。
- PDF 由隐藏内容 WebView 生成，只打印 Markdown 预览正文；系统文件对话框绑定当前 WIMD 窗口，设置和图片查看器跟随父窗口所在显示器。

## 构建与运行

在 Git 仓库根目录执行：

```powershell
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo run --manifest-path rust/Cargo.toml -p wimd
```

验证：

```powershell
cargo fmt --manifest-path rust/Cargo.toml --all -- --check
cargo check --manifest-path rust/Cargo.toml --workspace --all-targets --offline
cargo test --manifest-path rust/Cargo.toml --workspace --offline
cargo clippy --manifest-path rust/Cargo.toml --workspace --all-targets --offline -- -D warnings
node --check rust/crates/wimd-app/ui/js/app.js
```

当前本地结果为 82 项测试通过（Core 51、桌面模块 31），格式、Check、Clippy 严格模式和前端语法检查通过。`cargo-audit` 扫描 527 个锁定 crate 未发现漏洞；5 个 warning 包含 Linux 目标依赖和目标平台无关的未维护提示，不能直接当作 Windows 运行时漏洞，具体边界见仓库外《Rust 重写迁移记录》。

本地安装包：

```powershell
.\rust\packaging\build-release.ps1
```

安装包默认写入 `artifacts/rust-installer/WIMD-Setup-v2.0.0-win-x64.exe`。当前本地包为 27,588,894 字节，SHA-256 `443EA0F6C990513CBEC176832798E5A344188390A9DAAD0C92A8C98B7A368E87`；主程序为 39,991,296 字节，SHA-256 `67B821168508B879ED1057D7689EB47100988E972A72E6347262B5E2BEC9855C`；关闭助手为 233,984 字节，SHA-256 `668E3CF697485041405F81770E3B85B534681732E2B9B6C5758BF305CD1AD6B6`。均未签名。本地打包不代表发布；提交、推送、Tag 和 Release 仍需明确授权。

## 边界

正式安装前仍须验证 Windows 10、实际覆盖安装/升级/卸载、NSIS 关闭与完成页真实交互、文件关联和资源管理器右键菜单、PDF 实际回读、Mermaid/远程图片在正式安装包中的冷热运行、真实 ImgBB 账号和真实 GitHub 更新安装。自动化测试、Release 编译和安装包生成不能替代这些系统级验收。
