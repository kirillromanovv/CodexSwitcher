namespace CodexSwitcher.Core;

public sealed class Switcher(Vault vault, string authPath, IAccountGateway gateway, IAppController app)
{
    private readonly SemaphoreSlim operation = new(1,1);
    public async Task<SwitchResult> SwitchAsync(Slot slot, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await operation.WaitAsync(0,cancellationToken)) throw new UserFault("Дождитесь завершения текущей операции.");
        try
        {
            if (vault.HasRecovery) throw new UserFault("Сначала восстановите предыдущий вход.");
            var target = vault.Load(slot) ?? throw new UserFault("Сначала добавьте выбранный аккаунт.");
            var expected = AuthDocument.Parse(target.AuthJson).Identity;
            var initial = ReadCurrent();
            _ = KnownCurrent(initial);
            if (initial != null && AuthDocument.Parse(initial).Identity.Key == expected.Key)
                return new SwitchResult(await TryLaunch());
            progress?.Invoke("Проверяю выбранный вход…");
            var verified = await gateway.VerifyAsync(target.AuthJson,cancellationToken);
            CheckIdentity(verified,expected.Key);
            vault.Save(target with { AuthJson=verified, SavedAt=DateTimeOffset.UtcNow });
            progress?.Invoke("Закрываю Codex. Завершите активные задачи…");
            await app.CloseAsync(cancellationToken);
            // After closing the app, the transaction and rollback must finish even if the UI cancels.
            try
            {
                app.EnsureStopped();
                var original = ReadCurrent();
                var currentSlot = KnownCurrent(original);
                if (original != null && currentSlot != null)
                    vault.Save(vault.Load(currentSlot.Value)! with {AuthJson=original,SavedAt=DateTimeOffset.UtcNow});
                vault.BeginRecovery(new RecoveryRecord(Path.GetFullPath(authPath),original,expected.Key,slot));
                app.EnsureStopped();
                if (ReadCurrent() != original) throw new UserFault("Вход изменился во время переключения. Повторите операцию.");
                progress?.Invoke("Переключаю аккаунт…");
                SecureFiles.AtomicText(authPath,verified);
                var refreshed = await gateway.VerifyAsync(verified,CancellationToken.None);
                CheckIdentity(refreshed,expected.Key);
                app.EnsureStopped();
                SecureFiles.AtomicText(authPath,refreshed);
                vault.Save(target with { AuthJson=refreshed, SavedAt=DateTimeOffset.UtcNow });
                vault.ClearRecovery();
            }
            catch
            {
                if (vault.HasRecovery) RestoreCore();
                await TryLaunch();
                throw new UserFault("Переключение не завершено. Предыдущий вход восстановлен. Проверьте соединение или повторите вход.");
            }
            progress?.Invoke("Открываю Codex…");
            return new SwitchResult(await TryLaunch());
        }
        finally { operation.Release(); }
    }
    public async Task<SwitchResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (!await operation.WaitAsync(0,cancellationToken)) throw new UserFault("Дождитесь текущей операции.");
        try { await app.CloseAsync(cancellationToken); RestoreCore(); return new SwitchResult(await TryLaunch()); }
        finally { operation.Release(); }
    }
    private void RestoreCore()
    {
        app.EnsureStopped();
        var record = vault.ReadRecovery();
        if (!Path.GetFullPath(authPath).Equals(record.AuthPath,StringComparison.OrdinalIgnoreCase))
            throw new UserFault("Журнал относится к другой папке Codex. Восстановление остановлено.");
        var current = ReadCurrent();
        if (current != record.OriginalAuth && (current == null || AuthDocument.Parse(current).Identity.Key != record.TargetKey))
            throw new UserFault("После сбоя вход изменён другой программой. Автоматическое восстановление остановлено; журнал сохранён.");
        if (record.OriginalAuth == null) { SecureFiles.RejectLinks(authPath); File.Delete(authPath); }
        else SecureFiles.AtomicText(authPath,record.OriginalAuth);
        vault.ClearRecovery();
    }
    private string? ReadCurrent()
    {
        SecureFiles.RejectLinks(authPath);
        return File.Exists(authPath) ? File.ReadAllText(authPath) : null;
    }
    private Slot? KnownCurrent(string? json)
    {
        if (json == null) return null;
        var key = AuthDocument.Parse(json).Identity.Key;
        foreach (var candidate in vault.Slots())
        {
            var saved = vault.Load(candidate);
            if (saved != null && AuthDocument.Parse(saved.AuthJson).Identity.Key == key) return candidate;
        }
        throw new UserFault("Текущий аккаунт ещё не сохранён. Сначала нажмите «Сохранить текущий» в нужной карточке.");
    }
    private static void CheckIdentity(string json,string expected)
    {
        if (AuthDocument.Parse(json).Identity.Key != expected)
            throw new UserFault("Проверка вернула другой аккаунт. Переключение остановлено.");
    }
    private async Task<bool> TryLaunch()
    { try { await app.LaunchAsync(CancellationToken.None); return true; } catch { return false; } }
}
