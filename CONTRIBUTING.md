# 参与 SwitchEnvVar 开发

报告问题时，请说明 SwitchEnvVar 版本、Windows 版本和复现步骤。涉及变量写入、移动或配置组切换时，请提供使用模拟变量的例子，并说明期望结果和实际结果。请检查截图、日志和导出的配置，移除其中的凭据和本机路径。

提交代码前，在 Windows 上运行 `.\build.ps1` 和 `.\test.ps1`。托盘唤回相关的修改可运行 `.\test-reopen.ps1`。运行 `.\build\SwitchEnv.exe --demo` 可在模拟环境中验证界面。

如果改动涉及变量状态，请在描述中说明对用户变量和系统变量的影响，以及如何恢复原值。开发和验证细节见[开发指南](docs/development.md)。
