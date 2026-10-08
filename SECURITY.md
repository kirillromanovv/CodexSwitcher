# Security / Безопасность

## Reporting / Сообщение об уязвимости

Please use [GitHub private vulnerability reporting](https://github.com/kirillromanovv/CodexSwitcher/security/advisories/new). Do not publish tokens, authentication files, account emails, or exploitable details in public issues. Include the app version, Windows version, reproduction steps using synthetic data, and expected/actual behavior. There is no guaranteed response time.

Сообщайте об уязвимостях через [приватный отчёт GitHub](https://github.com/kirillromanovv/CodexSwitcher/security/advisories/new). Не публикуйте токены, файлы входа, реальные адреса аккаунтов и детали эксплуатации в открытых issues. Укажите версию программы и Windows, шаги на искусственных данных и ожидаемое/фактическое поведение. Срок ответа не гарантируется.

## Supported version / Поддерживаемая версия

Security fixes target the latest release (currently 1.6). Older versions are not maintained separately. Исправления предназначены для последнего выпуска; старые версии отдельно не поддерживаются.

## Boundaries / Границы защиты

- Saved credentials: DPAPI CurrentUser; private data folder ACLs. This does not defend against the same Windows user, administrators, or malware.
- Active Codex `auth.json`: normal Codex file format, not DPAPI encrypted by this utility.
- Temporary sign-in folders are cleaned on normal completion; crashes may leave private credential files behind.
- Shared local projects/history remain accessible to the local user across account switches; cloud access remains account-dependent.
- Only empty cards can be removed. Saved and damaged credential files are protected from empty-card deletion.
- Releases are unsigned. Compare the SHA256 checksum; it establishes file identity, not publisher trust or a security guarantee.

Сохранённые входы защищены DPAPI и правами папки, но это не защита от программ с правами того же пользователя Windows. Активный файл входа остаётся в обычном формате Codex. После аварии во временной папке может остаться авторизация. Локальные проекты и история общие; облачные права зависят от аккаунта. Выпуски не подписаны; сверка SHA256 подтверждает совпадение файла, но не является гарантией безопасности.

## Publication checks / Проверки публикации

Initial publication excludes local state, credentials, logs, SDK caches, intermediate builds and real-account quota reports. Source/export scanning and the release checksum are recorded in `docs/security-audit.md`. No scanner can prove the absence of all secrets or vulnerabilities.

Перед первой публикацией исключены локальное состояние, входы, журналы, SDK, промежуточные сборки и отчёты реальных аккаунтов. Результаты проверки перечислены в `docs/security-audit.md`. Сканер не доказывает отсутствие всех секретов и уязвимостей.
