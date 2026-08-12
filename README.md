# WIMD

WIMD 是面向 Windows 10/11 的本地 Markdown 实时预览编辑器，强调离线可用、清晰交互和安全的本地文件处理。

## 当前能力

- AvalonEdit 编辑器与 Markdig 100ms 防抖实时预览
- `F9` 在仅预览、编辑 + 预览、仅编辑三种模式间循环
- 紧凑菜单栏和 Markdown 快捷工具条；`Ctrl+1`～`Ctrl+6` 设置标题
- 编辑区与预览区双向滚动联动，光标按 Markdown 源行定位预览
- 安全显示当前文档目录内的 PNG、JPEG、GIF、BMP 和 WebP 相对路径图片
- 可折叠最近文件侧栏；移出记录不会删除原文件
- UTF-8、BOM、CRLF/LF 检测及同目录安全替换保存
- 本地自定义背景与透明度调节
- WebView2 脚本禁用、CSP 隔离和危险链接拦截

文件操作使用 `Ctrl+N`、`Ctrl+O`、`Ctrl+S`、`Ctrl+Shift+S`。应用无需账号，不包含遥测或文档上传；远程图片默认不会加载。

## 技术栈与结构

- C# / .NET 10 / WPF
- AvalonEdit / Markdig / WebView2
- xUnit v3 / Coverlet / Inno Setup

```text
src/WhoIsMarkdown.Core/         文档、编辑、渲染、安全策略和本地设置
src/WhoIsMarkdown.App/          WPF 外壳、视图和 WebView2 集成
tests/WhoIsMarkdown.Core.Tests/ 核心层单元测试
assets/                         应用图标和静态资源
packaging/                      WIMD Windows 安装包构建脚本
```

IR、SR、AR 保存在代码仓库外的统一项目文档目录，不随源码提交。

## 开发与发布

需要 .NET SDK 10.0.302 或兼容的 10.0 补丁版本。

```powershell
dotnet restore --locked-mode
dotnet build WhoIsMarkdown.sln --no-restore --configuration Release
dotnet test WhoIsMarkdown.sln --no-build --configuration Release
dotnet run --project src/WhoIsMarkdown.App/WhoIsMarkdown.App.csproj
dotnet format WhoIsMarkdown.sln --verify-no-changes
./packaging/build-release.ps1 -Version 1.0.0
```

发布脚本生成自包含 x64 程序和 `artifacts/installer/WIMD-Setup-v1.0.0-win-x64.exe`。设置保存在 `%LocalAppData%\WIMD\settings.json`；首次运行会兼容迁移旧版 `%LocalAppData%\WhoIsMarkdown` 设置。
