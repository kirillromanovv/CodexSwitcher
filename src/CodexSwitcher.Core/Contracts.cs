namespace CodexSwitcher.Core;

// Legacy names preserve numeric disk compatibility. Additional accounts use unnamed numeric values.
public enum Slot { Work=0, Personal=1, Account3=2, Account4=3, Account5=4 }
public sealed record Identity(string Subject, string AccountId, string Email)
{
    public string Key => Subject + "\n" + AccountId;
}
public sealed record AuthDocument(string Json, Identity Identity)
{
    public static AuthDocument Parse(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("auth_mode", out var mode) && mode.GetString() != "chatgpt")
                throw new UserFault("Поддерживается вход через ChatGPT. API-ключи и внешние токены не поддерживаются.");
            if (root.TryGetProperty("OPENAI_API_KEY", out var apiKey) && apiKey.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(apiKey.GetString()))
                throw new UserFault("В файле входа присутствует API-ключ. Используйте отдельный вход через ChatGPT.");
            var tokens = root.GetProperty("tokens");
            string Required(string name)
            {
                var value = tokens.GetProperty(name).GetString();
                if (string.IsNullOrWhiteSpace(value)) throw new FormatException();
                return value;
            }
            var id = Required("id_token");
            _ = Required("access_token"); _ = Required("refresh_token");
            var account = Required("account_id");
            var parts = id.Split('.');
            if (parts.Length != 3) throw new FormatException();
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var claims = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
            var subject = claims.RootElement.GetProperty("sub").GetString();
            var email = claims.RootElement.GetProperty("email").GetString();
            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email)) throw new FormatException();
            // Local claims are an identity hint. App-server verification is required before switching.
            return new AuthDocument(json, new Identity(subject, account, email));
        }
        catch (UserFault) { throw; }
        catch { throw new UserFault("Файл входа повреждён или имеет неподдерживаемый формат. Повторите вход."); }
    }
}
public sealed record Profile(Slot Slot, string AuthJson, DateTimeOffset SavedAt, DateOnly? RenewalDate=null);
public interface IAccountGateway
{
    Task<string> VerifyAsync(string authJson, CancellationToken cancellationToken);
}
public interface IAppController
{
    Task CloseAsync(CancellationToken cancellationToken);
    void EnsureStopped();
    Task LaunchAsync(CancellationToken cancellationToken);
}
public sealed record SwitchResult(bool LaunchSucceeded);
public sealed class UserFault(string message) : Exception(message);
