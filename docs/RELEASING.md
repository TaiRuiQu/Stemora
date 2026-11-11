# 自动检查与发布

推送分支或创建 / 更新 Pull Request 会运行 `CI`：

- C# 格式检查、C++ clang-format 18.1.8 格式检查和 PowerShell 语法检查。
- actionlint 1.7.12 检查 GitHub Actions 工作流。
- Windows x64 原生 CPU 后端构建 / CTest，以及 .NET / WinUI 构建和核心测试（含 WAV / MP3 导出）。

本地检查：

```powershell
python -m pip install clang-format==18.1.8
./scripts/Lint.ps1
# 自动修复 C# / C++ 格式
./scripts/Lint.ps1 -Fix
./scripts/Build-Native.ps1 -CpuOnly
./scripts/Build.ps1 -SkipNativeBuild
```

发布新版本：

1. 更新 `src/Stemora.App`、`src/Stemora.Cli`、`src/Stemora.Core` 的 `.csproj` 中的 `Version`，以及 `native/CMakeLists.txt` 的项目版本。预发布版本的 CMake 版本只填写数字部分。
2. 提交并推送到 GitHub，确认 CI 通过。
3. 创建并推送对应标签，例如：

```powershell
git tag -a v0.3.0 -m 'Stemora 0.3.0'
git push origin v0.3.0
```

`Release` 工作流会重新运行 lint / 构建 / 测试，校验标签和源码版本，然后构建 CUDA / CPU 运行时和自包含 WinUI / CLI，打包并发布 GitHub Release。附件为 `Stemora-v版本-windows-x64.zip` 和 `SHA256SUMS.txt`，更新日志由 GitHub 自动生成。带 `-` 的标签（如 `v0.3.0-rc.1`）发布为 prerelease。

发布包不附带模型、个人设置或任务数据。构建不需要 GPU；流水线不执行真实模型推理。GPU 推理仍需在兼容硬件上单独验证。

工作流使用仓库自带的 `GITHUB_TOKEN`，只有发布任务获得 `contents: write`；无需配置个人访问令牌。GitHub Actions 固定提交 SHA，由 Dependabot 每周检查更新。

若发布步骤因网络或上传失败而留下草稿，请在 Releases 页面删除该未发布草稿，再重新运行对应标签的 Release 工作流。
