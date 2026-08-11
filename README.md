# Who Is Mark-Down

一个面向 Windows 10/11 的本地 Markdown 实时预览编辑器。当前版本已经具备可运行的单文档编辑纵向切片。

## 当前能力

- AvalonEdit 编辑器与 Markdig 100ms 防抖实时预览
- 预览、编辑 + 预览、编辑三种工作区模式
- 可折叠最近文件侧栏，可打开或移出记录而不删除原文件
- UTF-8、BOM、CRLF/LF 检测及同目录安全替换保存
- 本地自定义背景与透明度调节
- WebView2 脚本禁用、CSP 隔离和危险链接拦截
- 本地 JSON 设置持久化，无账号、遥测或文档上传

视图快捷键：`Ctrl+1` 仅预览、`Ctrl+2` 分栏、`Ctrl+3` 仅编辑。文件操作使用 `Ctrl+N`、`Ctrl+O`、`Ctrl+S` 和 `Ctrl+Shift+S`。

## 技术栈与结构

- C# / .NET 10 LTS / WPF
- AvalonEdit / Markdig / WebView2
- xUnit v3 / Coverlet

```text
src/WhoIsMarkdown.Core/         文档、渲染、安全策略和本地设置
src/WhoIsMarkdown.App/          WPF 外壳、视图模式和 WebView2 集成
tests/WhoIsMarkdown.Core.Tests/ 核心层单元测试
assets/                         应用图标和静态资源
packaging/                      安装与 Windows Shell 集成（待实现）
```

IR、SR、AR 等项目文档保存在代码仓库外，不随源码提交。

## 开发命令

需要 .NET SDK 10.0.302 或兼容的 10.0 新补丁版本。

```powershell
dotnet restore --locked-mode
dotnet build WhoIsMarkdown.sln --no-restore --configuration Release
dotnet test WhoIsMarkdown.sln --no-build --configuration Release
dotnet run --project src/WhoIsMarkdown.App/WhoIsMarkdown.App.csproj
dotnet format WhoIsMarkdown.sln --verify-no-changes
```

代码格式和静态规则以 `.editorconfig` 与 `Directory.Build.props` 为准，编译警告视为错误。应用设置保存在当前用户的 `%LocalAppData%\WhoIsMarkdown\settings.json`。
