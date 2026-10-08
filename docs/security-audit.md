# Publication audit / Проверка перед публикацией

Date / Дата: **2026-10-08**. Release / Выпуск: **1.6.0**.

## Scope / Состав

Explicit export of source, tests, build scripts, GitHub workflow, documentation, MIT license and synthetic screenshots. Excluded: local credentials/state, `.tools/`, `work/`, intermediate `bin/obj`, old releases, real-account reports and development notes. The git history starts with this sanitized export; no prior local development history is uploaded.

Явный экспорт исходников, тестов, скриптов, CI, документации, лицензии и искусственных скриншотов. Исключены входы, локальное состояние, SDK, журналы, промежуточные сборки, старые выпуски, отчёты реальных аккаунтов и рабочие заметки. История Git начинается с очищенного экспорта.

## Checks / Проверки

- **Gitleaks 8.30.1**, downloaded from its official GitHub release and verified against the published archive SHA256. Default rules and decoding depth 3. No blanket token-rule exclusions.
- Exact public export: **zero findings**. Synthetic credential strings and example.com/example.invalid addresses were reviewed; they are not real account data.
- The preliminary workspace-wide pass reported two example secrets inside Gitleaks's own bundled README. The scanner folder is not exported. These were not application credentials.
- Public source and release documentation reviewed for personal Windows paths, real account addresses, private endpoints and authentication artifacts.
- **45/45 synthetic tests**, zero Release build warnings/errors; isolated signed-out app-server probe and light/dark/tray/recovery previews passed.
- Published EXE synthetic preview and live theme/add/remove checks passed. The EXE matches the previously verified v1.6 checksum. Binary provenance and hash were checked; text secret scanning is not an exhaustive audit of embedded binary data.
- No real-account switch, reset redemption, Codex shutdown or credential deletion was performed for publication.

Gitleaks не нашёл секретов в точном публичном экспорте. Тестовые токены и адреса проверены как искусственные. Два первоначальных срабатывания относятся к примерам самого сканера, не к программе. Все 45 тестов прошли; файлы входов и локальные данные не публикуются. Проверка не является гарантией отсутствия всех секретов, ошибок или уязвимостей.

## Executable identity / Контрольная сумма EXE

```text
2234CB1C74683B6873FEADA64721C04D7DD8A7E09C37D898DA468F7C0B218E73  CodexSwitcher.exe
```

The EXE and ZIP checksums are also included in the release asset `SHA256SUMS.txt`. See [SECURITY.md](../SECURITY.md) for the security model and private reporting.
