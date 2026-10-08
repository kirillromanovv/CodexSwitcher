# Contributing / Участие в разработке

Use Windows 11 x64 and .NET SDK 10.x. Run `./scripts/verify.ps1 -Preview` before a pull request. Use `-ProtocolSmoke` only when the Store Codex package is installed; it uses an isolated signed-out folder. Do not switch real accounts or close a user's working Codex session as part of automated testing.

Для разработки нужны Windows 11 x64 и .NET SDK 10.x. Перед pull request выполните `./scripts/verify.ps1 -Preview`. Параметр `-ProtocolSmoke` требует установленного Codex и проверяет протокол в отдельной папке без входа. Автоматические проверки не должны переключать реальные аккаунты или закрывать рабочую сессию пользователя.

Keep changes scoped and explain the problem, resulting behavior and verification. Preserve numeric account IDs, legacy encrypted filenames, identity/workspace validation, atomic auth writes, recovery, unknown quota values and credential-safe deletion. Add focused regression checks when these behaviors change.

Описывайте проблему, результат и проверку. Сохраняйте совместимость номеров аккаунтов и защищённых файлов, проверку аккаунта/рабочего пространства, атомарную замену входа и восстановление. Не превращайте неизвестные лимиты в нулевые и не ослабляйте защиту сохранённых входов.

Do not commit `.env`, `auth.json`, `*.dpapi`, browser URLs with credentials, local account data, real screenshots, logs, `work/`, `.tools/`, `outputs/`, `bin/` or `obj/`. Use example.com/example.invalid and synthetic credentials. Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

The MIT license applies to contributions. Вклад в проект распространяется по лицензии MIT.
